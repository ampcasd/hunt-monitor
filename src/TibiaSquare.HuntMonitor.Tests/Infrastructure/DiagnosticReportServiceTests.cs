using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TibiaSquare.HuntMonitor.Tests.Infrastructure;

/// <summary>
/// Integration test that sends a real diagnostic report to the endpoint.
/// Reads auth tokens from the local token store and hits the live API.
/// </summary>
public class DiagnosticReportServiceTests
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TibiaSquare");

    private static readonly string TokenFilePath = Path.Combine(AppDataDir, "auth.dat");

    private record AuthTokens(string AccessToken, string RefreshToken, long ExpiresAt);
    private record AppConfig(string SupabaseUrl, string SupabaseAnonKey, string ApiBaseUrl);

    private static AuthTokens? LoadTokens()
    {
        if (!File.Exists(TokenFilePath)) return null;
        try
        {
            var encrypted = File.ReadAllBytes(TokenFilePath);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(decrypted);
            return JsonSerializer.Deserialize<AuthTokens>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch
        {
            return null;
        }
    }

    private static AppConfig? LoadConfig()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "config.json");
            if (File.Exists(candidate))
            {
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(candidate),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            dir = Path.GetDirectoryName(dir)!;
            if (dir == null) break;
        }
        return null;
    }

    private static async Task<string?> RefreshTokenAsync(string refreshToken, string supabaseUrl, string supabaseAnonKey)
    {
        using var http = new HttpClient();
        var payload = JsonSerializer.Serialize(new { refresh_token = refreshToken });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        http.DefaultRequestHeaders.Add("apikey", supabaseAnonKey);

        var response = await http.PostAsync($"{supabaseUrl}/auth/v1/token?grant_type=refresh_token", content);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("access_token").GetString();
    }

    [Fact]
    public async Task SendDiagnosticReport_EndToEnd()
    {
        if (Environment.GetEnvironmentVariable("RUN_LIVE_DESKTOP_TESTS") != "1")
            return;

        // 1. Load config
        var config = LoadConfig();
        Assert.NotNull(config);
        Assert.False(string.IsNullOrEmpty(config!.SupabaseUrl), "SupabaseUrl not configured in config.json");

        var apiBaseUrl = !string.IsNullOrEmpty(config.ApiBaseUrl)
            ? config.ApiBaseUrl
            : config.SupabaseUrl;

        // 2. Load and refresh auth token
        var tokens = LoadTokens();
        Assert.NotNull(tokens);

        var accessToken = tokens!.AccessToken;

        // Refresh if expired
        if (tokens.ExpiresAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            Assert.False(string.IsNullOrEmpty(config.SupabaseAnonKey), "SupabaseAnonKey not configured");
            var refreshed = await RefreshTokenAsync(tokens.RefreshToken, config.SupabaseUrl, config.SupabaseAnonKey);
            Assert.NotNull(refreshed);
            accessToken = refreshed!;
        }

        // 3. Build a minimal test ZIP
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("system-info.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.WriteLine("App version: 0.0.0-test");
            writer.WriteLine($"Timestamp: {DateTime.UtcNow:O}");
            writer.WriteLine("This is a test diagnostic report.");
        }

        var zipBase64 = Convert.ToBase64String(zipStream.ToArray());

        // 4. POST to the endpoint
        using var http = new HttpClient();
        var requestPayload = JsonSerializer.Serialize(new
        {
            zipBase64,
            appVersion = "0.0.0-test",
            description = "Automated integration test — please ignore"
        });

        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
        var response = await http.PostAsync(
            $"{apiBaseUrl}/api/desktop/diagnostics",
            new StringContent(requestPayload, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();

        // 5. Assert
        Assert.True(response.IsSuccessStatusCode,
            $"Endpoint returned {response.StatusCode}: {body}");

        var result = JsonDocument.Parse(body);
        Assert.True(result.RootElement.GetProperty("success").GetBoolean(),
            $"Endpoint returned success=false: {body}");
    }
}
