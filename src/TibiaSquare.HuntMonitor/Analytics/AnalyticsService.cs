using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TibiaSquare.HuntMonitor.Auth;
namespace TibiaSquare.HuntMonitor.Analytics;

/// <summary>
/// Fire-and-forget analytics event tracker for the desktop app.
/// Sends events to the backend analytics endpoint.
/// </summary>
public sealed class AnalyticsService : IDisposable
{
    private readonly string _analyticsUrl;
    private readonly DiscordOAuthService? _auth;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly string AppVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";

    public AnalyticsService(string apiBaseUrl, DiscordOAuthService? auth)
    {
        _analyticsUrl = apiBaseUrl.TrimEnd('/') + "/api/analytics/track";
        _auth = auth;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Track an analytics event. Fire-and-forget — never throws.
    /// </summary>
    public void Track(string eventType, Dictionary<string, object?>? properties = null)
    {
        _ = TrackInternalAsync(eventType, properties);
    }

    private async Task TrackInternalAsync(string eventType, Dictionary<string, object?>? properties)
    {
        try
        {
            var enrichedProperties = new Dictionary<string, object?>
            {
                ["platform"] = "desktop",
                ["os"] = "windows",
                ["app_version"] = AppVersion,
            };

            if (properties != null)
            {
                foreach (var kvp in properties)
                    enrichedProperties[kvp.Key] = kvp.Value;
            }

            var payload = new
            {
                events = new[]
                {
                    new
                    {
                        eventType,
                        userId = _auth?.CurrentTokens?.UserName,
                        properties = enrichedProperties,
                    },
                },
            };

            var json = JsonSerializer.Serialize(payload);
            var request = new HttpRequestMessage(HttpMethod.Post, _analyticsUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

            // Attach bearer token if authenticated
            if (_auth?.CurrentTokens?.AccessToken is { } token)
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            await _http.SendAsync(request);
        }
        catch
        {
            // Silently fail — analytics should never affect app behavior
        }
    }
}
