using System.IO;
using Windows.Graphics.Imaging;
using TibiaSquare.HuntMonitor.Infrastructure;
using TibiaSquare.HuntMonitor.Models;
using TibiaSquare.HuntMonitor.Private;
using TibiaSquare.HuntMonitor.Storage;

namespace TibiaSquare.HuntMonitor.Screenshots;

/// <summary>
/// Captures JPEG screenshots when the user is beating their all-time best raw XP/h.
/// Ticks every ~1s (active session); encodes a JPEG every 5th tick while above the record.
/// On session end, keeps the 5 screenshots closest to the peak XP/h and deletes the rest.
/// </summary>
public sealed class PersonalBestScreenshotCapture
{
    private readonly SqliteSessionStore _store;
    private readonly ISessionProcessor _processor;
    private readonly ILogger _logger;

    private long? _previousAllTimeBest;
    private int _tickCounter;
    private bool _isBeating;
    private string? _sessionFolder;

    /// <summary>
    /// Hard cap: at most this many screenshots per session to bound disk usage.
    /// At 1 capture/5s over a typical 2h session = ~1440, so 200 gives comfortable headroom
    /// for sessions where PB is beaten frequently.
    /// </summary>
    private const int MaxScreenshotsPerSession = 200;

    /// <summary>
    /// Number of screenshots to keep after session ends (the ones closest to peak time).
    /// </summary>
    private const int KeepAfterSession = 5;

    /// <summary>
    /// Capture a JPEG every N ticks while above the record.
    /// At 1s tick interval (active), 5 = every 5 seconds.
    /// </summary>
    private const int CaptureEveryNTicks = 5;

    public PersonalBestScreenshotCapture(SqliteSessionStore store, ISessionProcessor processor, ILogger logger)
    {
        _store = store;
        _processor = processor;
        _logger = logger;
    }

    /// <summary>
    /// Called when a new hunt session starts. Loads the previous all-time best raw XP/h
    /// so we know what threshold to beat.
    /// </summary>
    public void OnSessionStarted(string sessionId, string characterName)
    {
        _previousAllTimeBest = GetBestRawXpPerHour(characterName);
        _tickCounter = 0;
        _isBeating = false;

        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TibiaSquare", "screenshots", sessionId);
        Directory.CreateDirectory(baseDir);
        _sessionFolder = baseDir;

        _logger.Info($"PB Screenshot: session started, previous best = {_previousAllTimeBest?.ToString() ?? "none"}");
    }

    private long? GetBestRawXpPerHour(string characterName)
    {
        long? best = null;
        foreach (var session in _store.GetCompletedSessionsForCharacter(characterName))
        {
            var snapshots = _store.GetAllSnapshots(session.Id);
            if (snapshots.Count == 0)
                continue;

            var processed = _processor.Process(session, snapshots);
            if (processed.PeakRawXpPerHour is > 0 && (best == null || processed.PeakRawXpPerHour > best))
                best = processed.PeakRawXpPerHour;
        }

        return best;
    }

    /// <summary>
    /// Called on each active-state tick with the current snapshot.
    /// If the current raw XP/h exceeds the all-time best, captures a JPEG every 5th tick.
    /// </summary>
    /// <param name="snapshot">The parsed OCR snapshot.</param>
    /// <param name="bitmap">The raw SoftwareBitmap from OBS (Bgra8). Must NOT be disposed by caller until this method returns.</param>
    /// <param name="sessionTimeSeconds">Elapsed session time in seconds.</param>
    public async Task OnTickAsync(HuntSnapshot snapshot, SoftwareBitmap bitmap, int sessionTimeSeconds)
    {
        if (_sessionFolder == null) return;

        var currentRawXp = snapshot.RawXpPerHour;
        if (currentRawXp == null || currentRawXp <= 0)
        {
            // No valid raw XP data — stop beating state
            _isBeating = false;
            _tickCounter = 0;
            return;
        }

        // Check if we're beating the previous best (or have no previous best at all)
        bool aboveRecord = _previousAllTimeBest == null || currentRawXp > _previousAllTimeBest;

        if (!aboveRecord)
        {
            _isBeating = false;
            _tickCounter = 0;
            return;
        }

        _isBeating = true;
        _tickCounter++;

        if (_tickCounter % CaptureEveryNTicks != 0)
            return;

        // Count existing screenshots to enforce the cap
        var existingCount = Directory.GetFiles(_sessionFolder, "*.jpg").Length;
        if (existingCount >= MaxScreenshotsPerSession)
        {
            _logger.Debug($"PB Screenshot: cap reached ({MaxScreenshotsPerSession}), skipping capture");
            return;
        }

        try
        {
            var jpegBytes = await JpegEncoder.EncodeAsync(bitmap, quality: 80);
            var fileName = $"{sessionTimeSeconds}.jpg";
            var filePath = Path.Combine(_sessionFolder, fileName);
            await File.WriteAllBytesAsync(filePath, jpegBytes);

            // Store the record in SQLite
            _store.InsertScreenshot(
                sessionId: Path.GetFileName(_sessionFolder), // folder name = session ID
                sessionTimeSeconds: sessionTimeSeconds,
                rawXpPerHour: currentRawXp.Value,
                filePath: filePath);

            _logger.Debug($"PB Screenshot: captured at {sessionTimeSeconds}s, rawXp/h={currentRawXp}");
        }
        catch (Exception ex)
        {
            _logger.Error($"PB Screenshot: capture failed — {ex.Message}");
        }
    }

    /// <summary>
    /// Called when the session ends. Selects the 5 screenshots closest to the peak
    /// XP/h time, deletes the rest, and updates the all-time best.
    /// </summary>
    public void OnSessionEnded(string sessionId)
    {
        if (_sessionFolder == null) return;

        try
        {
            var screenshots = _store.GetScreenshots(sessionId);
            if (screenshots.Count <= KeepAfterSession)
            {
                _logger.Info($"PB Screenshot: session ended, {screenshots.Count} screenshots kept (all)");
                UpdateAllTimeBest(screenshots);
                return;
            }

            // Find the screenshot with the highest raw XP/h (the peak)
            var peak = screenshots
                .Where(s => s.RawXpPerHour.HasValue)
                .MaxBy(s => s.RawXpPerHour!.Value);

            if (peak == null)
            {
                // No screenshots with XP data — keep all
                UpdateAllTimeBest(screenshots);
                return;
            }

            // Select the KeepAfterSession screenshots with session times closest to the peak
            var toKeep = screenshots
                .OrderBy(s => Math.Abs(s.SessionTimeSeconds - peak.SessionTimeSeconds))
                .Take(KeepAfterSession)
                .ToList();

            var toDelete = screenshots
                .Except(toKeep)
                .ToList();

            // Delete files and DB records
            foreach (var s in toDelete)
            {
                try
                {
                    if (File.Exists(s.FilePath))
                        File.Delete(s.FilePath);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"PB Screenshot: failed to delete {s.FilePath} — {ex.Message}");
                }
            }

            _store.DeleteScreenshotsByIds(toDelete.Select(s => s.Id).ToList());

            _logger.Info($"PB Screenshot: session ended, kept {toKeep.Count} of {screenshots.Count} screenshots (peak at {peak.SessionTimeSeconds}s)");

            UpdateAllTimeBest(toKeep);
        }
        catch (Exception ex)
        {
            _logger.Error($"PB Screenshot: session-end cleanup failed — {ex.Message}");
        }
        finally
        {
            _sessionFolder = null;
            _isBeating = false;
            _tickCounter = 0;
        }
    }

    private void UpdateAllTimeBest(List<ScreenshotRecord> keptScreenshots)
    {
        var bestInSession = keptScreenshots
            .Where(s => s.RawXpPerHour.HasValue)
            .Select(s => s.RawXpPerHour!.Value)
            .DefaultIfEmpty(0)
            .Max();

        if (bestInSession > 0 && (_previousAllTimeBest == null || bestInSession > _previousAllTimeBest))
        {
            _previousAllTimeBest = bestInSession;
            _logger.Info($"PB Screenshot: new all-time best raw XP/h = {bestInSession}");
        }
    }
}
