using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using TibiaSquare.HuntMonitor.Auth;
using TibiaSquare.HuntMonitor.Obs;
using TibiaSquare.HuntMonitor.Storage;

namespace TibiaSquare.HuntMonitor.Infrastructure;

public sealed class DiagnosticReportService
{
    private readonly SqliteSessionStore _store;
    private readonly DiscordOAuthService _auth;
    private readonly ILogger _logger;
    private readonly string _apiBaseUrl;
    private readonly HttpClient _http = new();

    public DiagnosticReportService(SqliteSessionStore store, DiscordOAuthService auth, ILogger logger, string apiBaseUrl)
    {
        _store = store;
        _auth = auth;
        _logger = logger;
        _apiBaseUrl = apiBaseUrl;
    }

    public async Task<bool> SendReportAsync(string userDescription)
    {
        try
        {
            if (!await _auth.EnsureValidTokenAsync())
            {
                _logger.Warn("Cannot send diagnostic report — not authenticated");
                return false;
            }

            var huntDates = _store.GetHuntDates().Take(2).ToList();
            var huntDateStrings = huntDates.Select(d => d.ToString("yyyy-MM-dd")).ToHashSet();

            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TibiaSquare");

            using var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
            {
                // 1. system-info.txt
                var sysInfoEntry = archive.CreateEntry("system-info.txt");
                using (var writer = new StreamWriter(sysInfoEntry.Open()))
                {
                    WriteSystemInfo(writer);
                }

                // 2. description.txt
                if (!string.IsNullOrWhiteSpace(userDescription))
                {
                    var descEntry = archive.CreateEntry("description.txt");
                    using var writer = new StreamWriter(descEntry.Open());
                    writer.Write(userDescription);
                }

                // 3. Log files for hunt dates
                var logDir = Path.Combine(appData, "logs");
                if (Directory.Exists(logDir))
                {
                    foreach (var file in new DirectoryInfo(logDir).GetFiles("hunt-monitor-*.log"))
                    {
                        var dateStr = ExtractDateFromFilename(file.Name);
                        if (dateStr != null && huntDateStrings.Contains(dateStr))
                            AddFileToArchive(archive, file.FullName, $"logs/{file.Name}");
                    }
                }

                // 4. diagnostics.jsonl for hunt dates
                var capturesDir = Path.Combine(appData, "debug-captures");
                if (Directory.Exists(capturesDir))
                {
                    foreach (var file in new DirectoryInfo(capturesDir).GetFiles("diagnostics-*.jsonl"))
                    {
                        var dateStr = ExtractDateFromFilename(file.Name);
                        if (dateStr != null && huntDateStrings.Contains(dateStr))
                            AddFileToArchive(archive, file.FullName, $"diagnostics/{file.Name}");
                    }
                }
            }

            // POST
            var zipBase64 = Convert.ToBase64String(zipStream.ToArray());
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
            var payload = JsonSerializer.Serialize(new
            {
                zipBase64,
                appVersion = version,
                description = userDescription
            });

            _http.DefaultRequestHeaders.Clear();
            _http.DefaultRequestHeaders.Add("Authorization",
                $"Bearer {_auth.CurrentTokens!.AccessToken}");

            var response = await _http.PostAsync(
                $"{_apiBaseUrl}/api/desktop/diagnostics",
                new StringContent(payload, Encoding.UTF8, "application/json"));

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.Error($"Diagnostic report upload failed: {response.StatusCode} - {body}");
                return false;
            }

            _logger.Info($"Diagnostic report sent — {zipStream.Length / 1024} KB");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to send diagnostic report", ex);
            return false;
        }
    }

    private static void WriteSystemInfo(StreamWriter writer)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        var os = Environment.OSVersion;
        var runtime = RuntimeInformation.FrameworkDescription;
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;
        var monitorCount = GetSystemMetrics(SM_CMONITORS);
        var packageType = IsMsixPackage() ? "MSIX" : "Portable";

        writer.WriteLine($"App version: {version}");
        writer.WriteLine($"OS: {os}");
        writer.WriteLine($"Runtime: {runtime}");
        writer.WriteLine($"Screen: {screenWidth}×{screenHeight} ({monitorCount} monitors)");
        writer.WriteLine($"OBS canvas: {GetObsCanvasSize()}");
        writer.WriteLine($"OBS plugins: {GetObsPluginList()}");
        writer.WriteLine($"Package: {packageType}");
        writer.WriteLine($"Timestamp: {DateTime.UtcNow:O}");
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
            var location = Assembly.GetExecutingAssembly().Location;
            return location.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CMONITORS = 80;

    private static void AddFileToArchive(ZipArchive archive, string sourcePath, string entryName)
    {
        var entry = archive.CreateEntry(entryName);
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var target = entry.Open();
        source.CopyTo(target);
    }

    private static string? ExtractDateFromFilename(string fileName)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            var parts = name.Split('-');
            if (parts.Length >= 3)
            {
                var dateStr = string.Join("-", parts[^3..]);
                if (DateOnly.TryParse(dateStr, out _))
                    return dateStr;
            }
        }
        catch { }
        return null;
    }
}
