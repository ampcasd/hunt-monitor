using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TibiaSquare.HuntMonitor.Auth;
using TibiaSquare.HuntMonitor.Infrastructure;
using TibiaSquare.HuntMonitor.Models;
using TibiaSquare.HuntMonitor.Private;
using TibiaSquare.HuntMonitor.Storage;

namespace TibiaSquare.HuntMonitor.Sync;

public sealed class SyncService
{
    private readonly SqliteSessionStore _store;
    private readonly DiscordOAuthService _auth;
    private readonly ISessionProcessor _processor;
    private readonly ILogger _logger;
    private readonly string _apiBaseUrl;
    private readonly HttpClient _http = new();

    public SyncService(SqliteSessionStore store, DiscordOAuthService auth, ISessionProcessor processor, ILogger logger, string apiBaseUrl)
    {
        _store = store;
        _auth = auth;
        _processor = processor;
        _logger = logger;
        _apiBaseUrl = apiBaseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Syncs a single completed session. Called when a session ends.
    /// </summary>
    public async Task<SyncResult> SyncSessionAsync(HuntSession session)
    {
        if (!await _auth.EnsureValidTokenAsync())
        {
            _logger.Info($"Not authenticated — skipping sync for session {session.Id}");
            return SyncResult.NotAuthenticated;
        }

        try
        {
            var allSnapshots = _store.GetAllSnapshots(session.Id);
            if (allSnapshots.Count == 0)
            {
                _logger.Info($"No snapshots for session {session.Id} — skipping sync");
                return SyncResult.Success;
            }

            var processed = _processor.Process(session, allSnapshots);
            var payload = BuildSyncPayload(session, processed);
            await PostSessionAsync(payload);
            _store.MarkSynced(session.Id);
            _logger.Info($"Session {session.Id} synced successfully");
            return SyncResult.Success;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to sync session {session.Id}: {ex.Message}");
            return SyncResult.Failed;
        }
    }

    /// <summary>
    /// Syncs all completed but unsynced sessions. Called on app startup.
    /// Also retries uploading any pending screenshots.
    /// </summary>
    public async Task<SyncPendingResult> SyncPendingAsync()
    {
        if (!await _auth.EnsureValidTokenAsync())
        {
            _logger.Info("Not authenticated — skipping pending sync");
            return new SyncPendingResult(0, 0);
        }

        var pending = _store.GetUnsyncedCompletedSessions();
        if (pending.Count == 0)
            return new SyncPendingResult(0, 0);

        _logger.Info($"Syncing {pending.Count} pending sessions");

        int synced = 0, failed = 0;
        foreach (var session in pending)
        {
            try
            {
                var allSnapshots = _store.GetAllSnapshots(session.Id);
                if (allSnapshots.Count == 0) continue;

                var processed = _processor.Process(session, allSnapshots);
                var payload = BuildSyncPayload(session, processed);
                await PostSessionAsync(payload);
                _store.MarkSynced(session.Id);
                _logger.Info($"Pending session {session.Id} synced");
                synced++;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to sync pending session {session.Id}: {ex.Message}");
                failed++;
            }
        }

        // Retry uploading any unuploaded screenshots for already-synced sessions
        await UploadPendingScreenshotsAsync();

        return new SyncPendingResult(synced, failed);
    }

    /// <summary>
    /// Uploads PB screenshots for a specific session to the server.
    /// Called after session sync completes.
    /// </summary>
    public async Task UploadScreenshotsAsync(string sessionId)
    {
        if (!await _auth.EnsureValidTokenAsync())
        {
            _logger.Info("Not authenticated — skipping screenshot upload");
            return;
        }

        // Get unuploaded screenshots and filter to this session via file path
        var allUnuploaded = _store.GetUnuploadedScreenshots();
        var screenshots = allUnuploaded
            .Where(s => Path.GetFileName(Path.GetDirectoryName(s.FilePath)) == sessionId)
            .ToList();

        if (screenshots.Count == 0)
            return;

        _logger.Info($"Uploading {screenshots.Count} screenshots for session {sessionId}");

        try
        {
            using var form = new MultipartFormDataContent();

            for (int i = 0; i < screenshots.Count; i++)
            {
                var screenshot = screenshots[i];
                if (!File.Exists(screenshot.FilePath))
                {
                    _logger.Warn($"Screenshot file missing: {screenshot.FilePath}");
                    continue;
                }

                var fileBytes = await File.ReadAllBytesAsync(screenshot.FilePath);
                var fileContent = new ByteArrayContent(fileBytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                form.Add(fileContent, $"screenshots[{i}].file", Path.GetFileName(screenshot.FilePath));
                form.Add(new StringContent(screenshot.SessionTimeSeconds.ToString()), $"screenshots[{i}].sessionTimeSeconds");
                form.Add(new StringContent(screenshot.RawXpPerHour?.ToString() ?? ""), $"screenshots[{i}].rawXpPerHour");
            }

            if (form.Count() == 0)
                return;

            _http.DefaultRequestHeaders.Clear();
            _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_auth.CurrentTokens!.AccessToken}");

            var response = await _http.PostAsync($"{_apiBaseUrl}/api/desktop/sessions/{sessionId}/screenshots", form);

            if (response.IsSuccessStatusCode)
            {
                _store.MarkScreenshotsUploaded(screenshots.Select(s => s.Id).ToList());
                _logger.Info($"Screenshots uploaded for session {sessionId}");
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.Error($"Screenshot upload failed: {response.StatusCode} - {body}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to upload screenshots for session {sessionId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Retries uploading any unuploaded screenshots for synced sessions.
    /// Called during startup pending sync.
    /// </summary>
    private async Task UploadPendingScreenshotsAsync()
    {
        var unuploaded = _store.GetUnuploadedScreenshots();
        if (unuploaded.Count == 0)
            return;

        _logger.Info($"Retrying upload for {unuploaded.Count} pending screenshots");

        // Derive session IDs from file path: screenshots/{sessionId}/{time}.jpg
        var sessionIds = unuploaded
            .Select(s => Path.GetFileName(Path.GetDirectoryName(s.FilePath))!)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct();

        foreach (var sessionId in sessionIds)
        {
            await UploadScreenshotsAsync(sessionId);
        }
    }

    private SyncPayload BuildSyncPayload(HuntSession session, ProcessedSession processed)
    {
        return new SyncPayload
        {
            Id = session.Id,
            CharacterName = session.CharacterName,
            StartedAt = session.StartedAtUtc.ToString("o"),
            EndedAt = session.EndedAtUtc?.ToString("o"),
            ActiveDurationSeconds = session.ActiveDurationSeconds,
            EndReason = session.EndReason,
            PeakXpPerHour = processed.PeakXpPerHour,
            AverageTopXpPerHour = processed.AverageTopXpPerHour,
            PeakRawXpPerHour = processed.PeakRawXpPerHour,
            AverageTopRawXpPerHour = processed.AverageTopRawXpPerHour,
            TotalXpGain = processed.TotalXpGain,
            TotalLoot = processed.TotalLoot,
            TotalSupplies = processed.TotalSupplies,
            FinalBalance = processed.FinalBalance,
            KilledMonsters = processed.Monsters
                .Select(m => new MonsterKill { Name = m.Name, Count = m.Count })
                .ToList(),
            Snapshots = processed.DownsampledSnapshots.Select(s => new SnapshotPayload
            {
                SessionTimeSeconds = s.SessionTimeSeconds,
                RawXpPerHour = s.RawXpPerHour,
                XpPerHour = s.XpPerHour,
                RawXpGain = s.RawXpGain,
                XpGain = s.XpGain,
                Loot = s.Loot,
                Supplies = s.Supplies,
                Balance = s.Balance,
                DamagePerHour = s.DamagePerHour,
                HealingPerHour = s.HealingPerHour,
                Stamina = s.Stamina,
            }).ToList(),
        };
    }

    /// <summary>
    /// Updates the character's stamina on the server. Called periodically (every ~60s)
    /// during active hunting and on character logout.
    /// </summary>
    public async Task SyncStaminaAsync(string characterName, int staminaMinutes, bool isLogout = false)
    {
        if (!await _auth.EnsureValidTokenAsync())
            return;

        try
        {
            var json = JsonSerializer.Serialize(new
            {
                characterName,
                staminaMinutes,
                logout = isLogout,
            });
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _http.DefaultRequestHeaders.Clear();
            _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_auth.CurrentTokens!.AccessToken}");

            var response = await _http.PostAsync($"{_apiBaseUrl}/api/desktop/stamina", content);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.Error($"Stamina sync failed: {response.StatusCode} - {body}");
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"Stamina sync error: {ex.Message}");
        }
    }

    private async Task PostSessionAsync(SyncPayload payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_auth.CurrentTokens!.AccessToken}");

        var response = await _http.PostAsync($"{_apiBaseUrl}/api/desktop/sessions", content);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Sync failed: {response.StatusCode} - {body}");
        }
    }
}

public enum SyncResult
{
    Success,
    NotAuthenticated,
    Failed,
}

public record SyncPendingResult(int Synced, int Failed);
