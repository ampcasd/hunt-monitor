using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using TibiaSquare.HuntMonitor.Obs;

namespace TibiaSquare.HuntMonitor.Infrastructure;

public sealed class FileLogger : ILogger, IDisposable
{
    private readonly string _logDir;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _currentDate = "";
    private int _currentFileIndex;
    private bool _systemInfoWritten;
    private Func<List<DateOnly>>? _getHuntDates;

    private const long MaxFileSizeBytes = 5L * 1024 * 1024; // 5 MB per file

    public FileLogger()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _logDir = Path.Combine(appData, "TibiaSquare", "logs");
        Directory.CreateDirectory(_logDir);

        // Fallback purge: delete log files older than 7 days (before DB is available)
        PurgeOlderThan(7);

        EnsureWriter();
    }

    public void SetHuntDatesProvider(Func<List<DateOnly>> getHuntDates)
    {
        _getHuntDates = getHuntDates;
        PurgeByHuntDates();
    }

    public void Debug(string message) => Write("DEBUG", message);
    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Error(string message, Exception ex)
    {
        Write("ERROR", message);
        Write("ERROR", $"  Exception: {ex.GetType().Name}: {ex.Message}");
        if (ex.StackTrace != null)
            Write("ERROR", $"  StackTrace: {ex.StackTrace}");
        if (ex.InnerException != null)
            Write("ERROR", $"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
    }

    private void Write(string level, string message)
    {
        var utcNow = DateTime.UtcNow;
        var line = $"[{utcNow:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";

        lock (_lock)
        {
            try
            {
                EnsureWriter();

                // System info block on first write of each day
                if (!_systemInfoWritten)
                {
                    _systemInfoWritten = true;
                    WriteSystemInfo();
                }

                _writer!.WriteLine(line);

                // Rotate if file is too large
                if (_writer.BaseStream.Length > MaxFileSizeBytes && _currentFileIndex < 1)
                {
                    _currentFileIndex = 1;
                    _writer.Dispose();
                    var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
                    var path = Path.Combine(_logDir, $"hunt-monitor-{date}.2.log");
                    _writer = new StreamWriter(path, append: true) { AutoFlush = true };
                }
            }
            catch { }
        }
    }

    private void EnsureWriter()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (_writer == null || _currentDate != today)
        {
            _writer?.Dispose();
            _currentDate = today;
            _currentFileIndex = 0;
            _systemInfoWritten = false;
            var path = Path.Combine(_logDir, $"hunt-monitor-{today}.log");
            _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        }
    }

    private void WriteSystemInfo()
    {
        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
            var os = Environment.OSVersion;
            var runtime = RuntimeInformation.FrameworkDescription;
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;
            var monitorCount = GetSystemMetrics(SM_CMONITORS);

            // OBS plugins
            var obsPlugins = GetObsPluginList();
            var packageType = IsMsixPackage() ? "MSIX" : "Portable";

            _writer!.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [INFO] ═══ System Info ═══");
            _writer.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [INFO] App version: {version}");
            _writer.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [INFO] OS: {os}");
            _writer.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [INFO] .NET: {runtime}");
            _writer.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [INFO] Screen: {screenWidth}×{screenHeight} ({monitorCount} monitors)");
            _writer.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [INFO] OBS canvas: {GetObsCanvasSize()}");
            _writer.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [INFO] OBS plugins: {obsPlugins}");
            _writer.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [INFO] Package: {packageType}");
            _writer.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [INFO] ═══ End System Info ═══");
        }
        catch { }
    }

    private static string GetObsCanvasSize()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(appData, "TibiaSquare", "obs-portable"),
                Path.Combine(appData, "Packages", "Ampcasd.TibiaSquareHuntUploader_am3n3n1zjkpne", "LocalCache", "obs-portable"),
            };

            var obsDir = candidates.FirstOrDefault(Directory.Exists);
            if (obsDir == null)
                return "N/A (OBS not found)";

            var (w, h) = ObsConfigGenerator.ReadCanvasSize(obsDir);
            return w > 0 && h > 0 ? $"{w}×{h}" : "N/A (profile not found)";
        }
        catch
        {
            return "N/A (error reading profile)";
        }
    }

    private static string GetObsPluginList()
    {
        try
        {
            // Check both portable and MSIX LocalCache paths
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(appData, "TibiaSquare", "obs-portable", "obs-plugins", "64bit"),
                Path.Combine(appData, "Packages", "Ampcasd.TibiaSquareHuntUploader_am3n3n1zjkpne", "LocalCache", "obs-portable", "obs-plugins", "64bit"),
            };

            var pluginDir = candidates.FirstOrDefault(Directory.Exists);
            if (pluginDir == null)
                return "N/A (OBS not found)";

            var dlls = Directory.GetFiles(pluginDir, "*.dll")
                .Select(Path.GetFileName)
                .OrderBy(n => n)
                .ToList();

            return dlls.Count > 0 ? string.Join(", ", dlls) : "N/A (no plugins found)";
        }
        catch
        {
            return "N/A (error reading plugins)";
        }
    }

    private static bool IsMsixPackage()
    {
        try
        {
            // Check if running from a WindowsApps folder (MSIX package)
            var location = Assembly.GetExecutingAssembly().Location;
            return location.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void PurgeOlderThan(int days)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-days);
            foreach (var file in Directory.GetFiles(_logDir, "hunt-monitor-*.log"))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < cutoff)
                    info.Delete();
            }
        }
        catch { }
    }

    public void PurgeByHuntDates()
    {
        if (_getHuntDates == null) return;

        try
        {
            var huntDates = _getHuntDates().Take(2).ToHashSet();

            foreach (var file in Directory.GetFiles(_logDir, "hunt-monitor-*.log"))
            {
                var date = ExtractDateFromFilename(file);
                if (date.HasValue && !huntDates.Contains(date.Value))
                    File.Delete(file);
            }

            // Also purge diagnostics files
            var capturesDir = Path.Combine(Path.GetDirectoryName(_logDir)!, "debug-captures");
            if (Directory.Exists(capturesDir))
            {
                foreach (var file in Directory.GetFiles(capturesDir, "diagnostics-*.jsonl"))
                {
                    var date = ExtractDateFromFilename(file);
                    if (date.HasValue && !huntDates.Contains(date.Value))
                        File.Delete(file);
                }
            }
        }
        catch { }
    }

    private static DateOnly? ExtractDateFromFilename(string filePath)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            // "hunt-monitor-2026-04-25" or "diagnostics-2026-04-25"
            var parts = name.Split('-');
            // Find the date portion (last 3 parts: yyyy, MM, dd)
            if (parts.Length >= 3)
            {
                var dateStr = string.Join("-", parts[^3..]);
                if (DateOnly.TryParse(dateStr, out var date))
                    return date;
            }
        }
        catch { }
        return null;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CMONITORS = 80;

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
