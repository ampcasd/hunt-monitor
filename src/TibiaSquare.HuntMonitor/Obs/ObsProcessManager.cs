using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using TibiaSquare.HuntMonitor.Infrastructure;

using Directory = System.IO.Directory;
using File = System.IO.File;
using Path = System.IO.Path;

namespace TibiaSquare.HuntMonitor.Obs;

/// <summary>
/// Manages the OBS Portable process lifecycle: launch, health monitoring, and shutdown.
/// OBS is launched as a child process in portable mode with WebSocket enabled.
/// </summary>
public sealed class ObsProcessManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly ProcessGpuAdapterDetector _gpuDetector = new();
    private Process? _obsProcess;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private int _restartCount;
    private int? _targetProcessId;
    private bool _disposed;

    private const int MaxRestarts = 3;
    private const int StatusDllNotFound = -1073741515; // 0xC0000135
    private const string VersionStampFileName = ".tibia-square-version";
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LaunchWaitTime = TimeSpan.FromSeconds(8);

    // Files we require in any healthy local OBS copy. If any are missing post-copy
    // (e.g. AV quarantine, partial copy), wipe and recopy from the package.
    // Use forward slashes — Path.Combine handles separators per platform.
    private static readonly string[] CriticalRelativeFiles =
    {
        "portable_mode.txt",
        "bin/64bit/obs64.exe",
        "bin/64bit/avformat-61.dll",
        "bin/64bit/avcodec-61.dll",
        "bin/64bit/libcurl.dll",
        "bin/64bit/Qt6Core.dll",
        "data/obs-studio/locale/en-US.ini",
        "obs-plugins/64bit/obs-websocket.dll",
    };

    private static string CurrentAppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

    // Settings file stores port + password across runs
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TibiaSquare");
    private static readonly string SettingsFile = Path.Combine(SettingsDir, "obs-settings.txt");

    public int WebSocketPort { get; private set; } = 4456;
    public string WebSocketPassword { get; private set; } = "";
    public bool IsRunning => _obsProcess is { HasExited: false };
    public string? ObsDirectory { get; private set; }

    public event Action? ObsStarted;
    public event Action<string>? ObsFailed;

    public ObsProcessManager(ILogger logger)
    {
        _logger = logger;
        LoadOrGenerateSettings();
    }

    /// <summary>
    /// Sets the game process whose GPU adapter OBS must match. Adapter detection
    /// uses Windows GPU telemetry only and does not access the game process.
    /// </summary>
    public void SetTargetProcessId(int processId)
    {
        if (processId > 0)
            _targetProcessId = processId;
    }

    /// <summary>
    /// Reports a confirmed adapter mismatch for an already-running OBS process.
    /// False also covers unavailable telemetry so capture is never blocked solely
    /// because performance counters are missing.
    /// </summary>
    public bool HasConfirmedGpuMismatch()
    {
        return _targetProcessId.HasValue &&
               _obsProcess is { HasExited: false } &&
               _gpuDetector.TryGetAdapter(_targetProcessId.Value, out var targetAdapter) &&
               _gpuDetector.TryGetAdapter(_obsProcess.Id, out var obsAdapter) &&
               targetAdapter != obsAdapter;
    }

    /// <summary>
    /// Locates OBS Portable directory. Searches in order:
    /// 1. obs-portable/ next to the app executable
    /// 2. obs-portable/ in the app data directory
    /// 3. Standard OBS installation at Program Files
    /// </summary>
    public string? FindObsDirectory()
    {
        var candidates = new List<string>
        {
            // Next to our executable
            Path.Combine(AppContext.BaseDirectory, "obs-portable"),
            // In app data
            Path.Combine(SettingsDir, "obs-portable"),
        };

        // Walk up from the executable directory to find obs-portable/ in parent dirs
        // (covers dev scenario where obs-portable is in desktop/ or repo root)
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
            candidates.Add(Path.Combine(dir, "obs-portable"));
        }

        // Standard OBS installation
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio"));

        // MSIX packages are read-only — if OBS is bundled inside the package,
        // copy it to a writable location so OBS can write configs/logs.
        if (PackageHelper.IsMsixPackaged)
        {
            var packageObs = Path.Combine(AppContext.BaseDirectory, "obs-portable");
            // MUST use LocalCacheFolder, NOT %LocalAppData%\TibiaSquare. The latter
            // can be silently redirected by MSIX to Packages\<Name>\LocalCache\Local\...,
            // visible to our packaged process but NOT (or inconsistently) to a breakaway
            // obs64.exe child whose DLL loader sees the real FS. That mismatch causes
            // STATUS_DLL_NOT_FOUND on fresh machines. LocalCacheFolder is the real,
            // non-virtualized MSIX path and is identical inside and outside the container.
            var localObs = Path.Combine(
                Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path,
                "obs-portable");

            if (Directory.Exists(packageObs) && GetObsExePath(packageObs) != null)
            {
                if (!IsLocalCopyValid(localObs))
                {
                    _logger.Info($"MSIX: local OBS copy invalid — wiping and recopying from package");
                    WipeLocalCopy(localObs);
                    CopyDirectoryRecursive(packageObs, localObs);
                    WriteVersionStamp(localObs);
                    _logger.Info($"MSIX: OBS copied to {localObs} (stamped {CurrentAppVersion})");
                }
                else
                {
                    _logger.Info($"MSIX: local OBS at {localObs} is valid (version {CurrentAppVersion})");
                }

                ObsDirectory = localObs;
                return localObs;
            }
        }

        _logger.Info($"Searching for OBS (BaseDirectory: {AppContext.BaseDirectory})");
        foreach (var candidate in candidates)
        {
            var exePath = GetObsExePath(candidate);
            var exists = Directory.Exists(candidate);
            _logger.Info($"  [{(exePath != null ? "FOUND" : exists ? "NO EXE" : "MISSING")}] {candidate}");
            if (exePath != null)
            {
                ObsDirectory = candidate;
                return candidate;
            }
            if (exists && exePath == null)
            {
                // Directory exists but exe not found — log contents for debugging
                try
                {
                    var entries = Directory.GetFileSystemEntries(candidate);
                    _logger.Info($"    Contents: {string.Join(", ", entries.Select(Path.GetFileName))}");
                    var binDir = Path.Combine(candidate, "bin", "64bit");
                    if (Directory.Exists(binDir))
                    {
                        var binFiles = Directory.GetFiles(binDir).Select(Path.GetFileName);
                        _logger.Info($"    bin/64bit/: {string.Join(", ", binFiles)}");
                    }
                }
                catch { }
            }
        }

        _logger.Error("OBS Portable not found in any candidate location");
        return null;
    }

    /// <summary>
    /// Launches OBS and waits for it to be ready.
    /// Returns true if OBS started successfully.
    /// </summary>
    public async Task<bool> LaunchAsync()
    {
        if (IsRunning)
        {
            _logger.Info("OBS is already running");
            return true;
        }

        var obsDir = ObsDirectory ?? FindObsDirectory();
        if (obsDir == null)
        {
            ObsFailed?.Invoke("OBS Portable not found. Place OBS Portable in the obs-portable/ folder next to the application.");
            return false;
        }

        var exePath = GetObsExePath(obsDir);
        if (exePath == null)
        {
            ObsFailed?.Invoke($"obs64.exe not found in {obsDir}");
            return false;
        }

        // Kill any orphaned OBS instances from previous runs
        KillOrphanedObsProcesses(exePath);

        // Generate config files
        var isPortable = IsPortableInstall(obsDir);
        if (isPortable)
        {
            ObsConfigGenerator.EnsureConfig(obsDir, WebSocketPort, WebSocketPassword);
            _logger.Info("OBS config generated/verified");
        }

        // Build launch arguments
        var args = new List<string>();
        if (isPortable)
            args.Add("--portable");
        args.Add("--minimize-to-tray");
        args.Add("--disable-shutdown-check");
        args.Add("--disable-updater");
        args.Add("--multi");
        args.Add("--collection \"Hunt Monitor\"");
        args.Add("--profile \"Hunt Monitor\"");

        var argsString = string.Join(" ", args);
        _logger.Info($"Launching OBS: {exePath} {argsString}");

        try
        {
            var workingDir = Path.GetDirectoryName(exePath)!;
            var targetAdapter = TryGetTargetAdapter();
            var currentPreference = ObsGpuPreferenceStore.GetPreference(exePath);
            var probeOrder = targetAdapter.HasValue
                ? ObsGpuPreferenceStore.BuildProbeOrder(currentPreference)
                : new int?[] { currentPreference };

            for (var attempt = 0; attempt < probeOrder.Count; attempt++)
            {
                var preference = probeOrder[attempt];
                var canProbe = true;

                if (targetAdapter.HasValue)
                {
                    canProbe = ObsGpuPreferenceStore.TrySetPreference(exePath, preference);
                    if (canProbe)
                    {
                        _logger.Info(
                            $"OBS GPU reconciliation: target={targetAdapter.Value}, " +
                            $"trying {FormatGpuPreference(preference)}");
                    }
                    else
                    {
                        _logger.Warn("OBS GPU reconciliation: Windows GPU preference could not be updated");
                    }
                }

                StartObsProcess(exePath, argsString, workingDir);
                _logger.Info($"OBS process started (PID: {_obsProcess!.Id})");

                // Wait for OBS to initialize its D3D renderer and WebSocket server.
                await Task.Delay(LaunchWaitTime);

                if (_obsProcess.HasExited)
                {
                    var exitCode = _obsProcess.ExitCode;
                    _logger.Error($"OBS exited immediately with code {exitCode}");
                    ObsFailed?.Invoke(FormatExitError(exitCode));
                    return false;
                }

                if (!targetAdapter.HasValue || !canProbe)
                    break;

                var obsAdapter = await WaitForAdapterAsync(_obsProcess.Id);
                if (!obsAdapter.HasValue)
                {
                    _logger.Warn("OBS GPU reconciliation: OBS adapter telemetry unavailable; keeping current preference");
                    break;
                }

                if (obsAdapter.Value == targetAdapter.Value)
                {
                    _logger.Info($"OBS GPU reconciliation successful: both processes use {obsAdapter.Value}");
                    break;
                }

                var isLastAttempt = attempt == probeOrder.Count - 1;
                if (isLastAttempt)
                {
                    _logger.Error(
                        $"OBS GPU reconciliation failed: Tibia={targetAdapter.Value}, OBS={obsAdapter.Value}; " +
                        "Windows could not route OBS to the same adapter");
                    break;
                }

                _logger.Warn(
                    $"OBS GPU mismatch detected: Tibia={targetAdapter.Value}, OBS={obsAdapter.Value}; " +
                    "restarting OBS with the next Windows GPU preference");
                StopProbeProcess();
            }

            // Start health monitoring
            _restartCount = 0;
            _monitorCts = new CancellationTokenSource();
            _monitorTask = MonitorProcessAsync(_monitorCts.Token);

            ObsStarted?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to launch OBS: {ex.Message}");
            ObsFailed?.Invoke($"Failed to launch OBS: {ex.Message}");
            return false;
        }
    }

    private void StartObsProcess(string exePath, string argsString, string workingDir)
    {
        if (PackageHelper.IsMsixPackaged)
        {
            // Break away from the MSIX package identity so OBS loads sibling DLLs
            // from its writable LocalCache copy.
            var pid = DesktopBreakawayProcess.Start(exePath, argsString, workingDir);
            _obsProcess = Process.GetProcessById(pid);
            _obsProcess.EnableRaisingEvents = true;
            return;
        }

        _obsProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = argsString,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = false,
            },
            EnableRaisingEvents = true,
        };
        _obsProcess.Start();
    }

    private GpuAdapterId? TryGetTargetAdapter()
    {
        if (!_targetProcessId.HasValue)
        {
            _logger.Debug("OBS GPU reconciliation deferred: Tibia process ID is not available yet");
            return null;
        }

        if (_gpuDetector.TryGetAdapter(_targetProcessId.Value, out var adapter))
            return adapter;

        _logger.Warn($"OBS GPU reconciliation: no GPU telemetry found for Tibia PID {_targetProcessId.Value}");
        return null;
    }

    private async Task<GpuAdapterId?> WaitForAdapterAsync(int processId)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (_gpuDetector.TryGetAdapter(processId, out var adapter))
                return adapter;

            await Task.Delay(500);
        }

        return null;
    }

    private void StopProbeProcess()
    {
        if (_obsProcess == null)
            return;

        try
        {
            if (!_obsProcess.HasExited)
            {
                _obsProcess.Kill(entireProcessTree: true);
                _obsProcess.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to stop mismatched OBS probe: {ex.Message}");
        }
        finally
        {
            _obsProcess.Dispose();
            _obsProcess = null;
        }
    }

    private static string FormatGpuPreference(int? preference) => preference switch
    {
        1 => "minimum-power preference",
        2 => "high-performance preference",
        _ => "Windows default preference",
    };

    /// <summary>
    /// Restarts OBS (stop + relaunch). Used when the OBS profile needs different canvas dimensions.
    /// </summary>
    public async Task<bool> RestartAsync()
    {
        _logger.Info("Restarting OBS to apply new configuration");
        Stop();
        return await LaunchAsync();
    }

    /// <summary>
    /// Gracefully stops OBS. Tries to close the process, then kills if necessary.
    /// </summary>
    public void Stop()
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;

        if (_obsProcess == null) return;

        try
        {
            if (!_obsProcess.HasExited)
            {
                _logger.Info("Stopping OBS...");
                _obsProcess.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error stopping OBS: {ex.Message}");
        }
        finally
        {
            _obsProcess.Dispose();
            _obsProcess = null;
        }

        _logger.Info("OBS stopped");
    }

    private async Task MonitorProcessAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_obsProcess == null || !_obsProcess.HasExited)
                continue;

            var unexpectedExitCode = _obsProcess.ExitCode;
            _logger.Error($"OBS process exited unexpectedly (code {unexpectedExitCode})");
            _obsProcess.Dispose();
            _obsProcess = null;

            if (_restartCount >= MaxRestarts)
            {
                _logger.Error($"OBS crashed {MaxRestarts} times, giving up");
                ObsFailed?.Invoke(unexpectedExitCode == StatusDllNotFound
                    ? FormatExitError(unexpectedExitCode)
                    : $"OBS crashed {MaxRestarts} times. Please restart the application.");
                break;
            }

            _restartCount++;
            _logger.Info($"Restarting OBS (attempt {_restartCount}/{MaxRestarts})...");

            try
            {
                await Task.Delay(RestartDelay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var success = await LaunchAsync();
            if (!success)
                break;
        }
    }

    private void LoadOrGenerateSettings()
    {
        Directory.CreateDirectory(SettingsDir);

        if (File.Exists(SettingsFile))
        {
            try
            {
                var lines = File.ReadAllLines(SettingsFile);
                foreach (var line in lines)
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;

                    switch (parts[0].Trim())
                    {
                        case "port":
                            if (int.TryParse(parts[1].Trim(), out var port))
                                WebSocketPort = port;
                            break;
                        case "password":
                            WebSocketPassword = parts[1].Trim();
                            break;
                    }
                }

                if (!string.IsNullOrEmpty(WebSocketPassword))
                    return;
            }
            catch
            {
                // Regenerate on parse failure
            }
        }

        // Generate new password
        WebSocketPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        WebSocketPort = 4456;

        File.WriteAllText(SettingsFile, $"port={WebSocketPort}\npassword={WebSocketPassword}\n");
    }

    private static string? GetObsExePath(string obsDir)
    {
        // OBS Portable layout: bin/64bit/obs64.exe
        var portable = Path.Combine(obsDir, "bin", "64bit", "obs64.exe");
        if (File.Exists(portable))
            return portable;

        // Installed OBS layout: also bin/64bit/obs64.exe
        // Some layouts have obs64.exe directly
        var direct = Path.Combine(obsDir, "obs64.exe");
        if (File.Exists(direct))
            return direct;

        return null;
    }

    private void KillOrphanedObsProcesses(string exePath)
    {
        try
        {
            var exeName = Path.GetFileNameWithoutExtension(exePath);
            var processes = Process.GetProcessesByName(exeName);
            foreach (var proc in processes)
            {
                try
                {
                    // Only kill OBS processes launched from our portable directory
                    if (proc.MainModule?.FileName?.Equals(exePath, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        _logger.Info($"Killing orphaned OBS process (PID: {proc.Id})");
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(3000);
                    }
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to clean up orphaned OBS processes: {ex.Message}");
        }
    }

    private void CopyDirectoryRecursive(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            var dest = Path.Combine(destination, Path.GetFileName(file));
            try
            {
                File.Copy(file, dest, overwrite: true);
                if (!File.Exists(dest))
                    _logger.Error($"Copy reported success but destination missing: {dest}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to copy {file} -> {dest}: {ex.Message}");
            }
        }

        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectoryRecursive(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    private bool IsLocalCopyValid(string localObs)
    {
        if (!Directory.Exists(localObs))
            return false;

        foreach (var rel in CriticalRelativeFiles)
        {
            var path = Path.Combine(localObs, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                _logger.Info($"Local OBS missing critical file: {rel}");
                return false;
            }
        }

        var stampPath = Path.Combine(localObs, VersionStampFileName);
        if (!File.Exists(stampPath))
        {
            _logger.Info("Local OBS missing version stamp");
            return false;
        }

        try
        {
            var stamp = File.ReadAllText(stampPath).Trim();
            if (stamp != CurrentAppVersion)
            {
                _logger.Info($"Local OBS version stamp mismatch: stamp={stamp}, current={CurrentAppVersion}");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to read version stamp: {ex.Message}");
            return false;
        }

        return true;
    }

    private void WipeLocalCopy(string localObs)
    {
        if (!Directory.Exists(localObs))
            return;
        try
        {
            Directory.Delete(localObs, recursive: true);
            _logger.Info($"Wiped local OBS copy at {localObs}");
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to wipe local OBS copy at {localObs}: {ex.Message}");
        }
    }

    private void WriteVersionStamp(string localObs)
    {
        try
        {
            File.WriteAllText(Path.Combine(localObs, VersionStampFileName), CurrentAppVersion);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to write version stamp: {ex.Message}");
        }
    }

    private string FormatExitError(int exitCode)
    {
        if (exitCode == StatusDllNotFound)
        {
            var folder = ObsDirectory ?? "(OBS folder)";
            return $"OBS files were blocked from loading (exit code {exitCode}). " +
                   "This is usually caused by antivirus quarantining OBS DLLs. " +
                   $"Add this folder to your antivirus exclusions and restart Hunt Monitor:\n{folder}";
        }
        return $"OBS exited unexpectedly (code {exitCode})";
    }

    private static bool IsPortableInstall(string obsDir)
    {
        // If config directory already exists alongside bin, it's portable
        // Or if there's no uninstall info, treat as portable
        return Directory.Exists(Path.Combine(obsDir, "bin"))
            || !File.Exists(Path.Combine(obsDir, "uninstall.exe"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
