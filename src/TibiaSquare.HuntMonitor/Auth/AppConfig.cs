using System.IO;
using System.Text.Json;

namespace TibiaSquare.HuntMonitor.Auth;

public sealed class AppConfig
{
    public string SupabaseUrl { get; init; } = "";
    public string SupabaseAnonKey { get; init; } = "";
    public string ApiBaseUrl { get; init; } = "";

    public static AppConfig Load()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var configPath = Path.Combine(exeDir, "config.json");

        if (!File.Exists(configPath))
            return new AppConfig();

        try
        {
            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public bool IsValid => !string.IsNullOrEmpty(SupabaseUrl) && !string.IsNullOrEmpty(SupabaseAnonKey);

    /// <summary>
    /// Returns true if all configured URLs use HTTPS (prevents accidental plaintext traffic).
    /// </summary>
    public bool HasSecureUrls =>
        (string.IsNullOrEmpty(ApiBaseUrl) || ApiBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrEmpty(SupabaseUrl) || SupabaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}
