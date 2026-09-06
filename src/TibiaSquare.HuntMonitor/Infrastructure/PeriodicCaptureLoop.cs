using TibiaSquare.HuntMonitor.Capture;
using TibiaSquare.HuntMonitor.Models;
using TibiaSquare.HuntMonitor.Notifications;
using TibiaSquare.HuntMonitor.Obs;
using TibiaSquare.HuntMonitor.Ocr;
using TibiaSquare.HuntMonitor.Private;
using TibiaSquare.HuntMonitor.Screenshots;
using Windows.Graphics.Imaging;

namespace TibiaSquare.HuntMonitor.Infrastructure;

public sealed class PeriodicCaptureLoop : IDisposable
{
    private readonly ICaptureService _captureService;
    private readonly OcrService _ocrService;
    private readonly IPanelLocator _regionLocator;
    private readonly IPanelLocator _skillsLocator;
    private readonly IPanelLocator _xpAnalyserLocator;
    private readonly IHuntAnalyserParser _parser;
    private readonly ISessionLifecycle _sessionManager;
    private readonly ToastNotificationService _notifications;
    private readonly ILogger _logger;
    private readonly string _characterName;
    private readonly DebugDiagnostics _diagnostics;
    private readonly PersonalBestScreenshotCapture? _screenshotCapture;
    private readonly Action<bool>? _onAnalyserStatusChanged;
    private Action<HuntSnapshot?, IReadOnlyList<string>, IReadOnlyList<OcrWordInfo>, double?, byte[]?, string?>? _onOcrTick;
    private Action<int?, IReadOnlyList<OcrWordInfo>, byte[]?, string?>? _onSkillsTick;
    private Action<XpAnalyserRates?, IReadOnlyList<OcrWordInfo>, byte[]?, string?>? _onXpAnalyserTick;
    private Action<string, int?, bool>? _onStaminaSync;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private IntPtr _hwnd;
    private bool? _lastAnalyserFound;
    private readonly RawXpNotificationTracker _rawXpNotificationTracker = new();
    private int _consecutiveEmptyFrames;
    private DateTime _lastAnalyserFoundTime = DateTime.MinValue;

    // Rate-limit Skills panel reads — stamina changes slowly (1 min per real-time minute).
    private int _skillsReadCounter;
    private bool _skillsRetrying;

    // The XP Analyser is checked periodically while absent, then on every frame while
    // visible because its rolling rates are the preferred live source.
    private int _xpAnalyserReadCounter;
    private int _xpAnalyserRetryCount;
    private bool _xpAnalyserVisible;
    private DateTime _lastXpAnalyserFoundTime = DateTime.MinValue;
    private const int XpAnalyserAbsentScanIntervalTicks = 5;
    private const int XpAnalyserRetryLimit = 5;

    // Periodic stamina sync to server (every 60s during active hunting)
    private int? _lastStamina;
    private DateTime _lastStaminaSyncTime = DateTime.MinValue;
    private static readonly TimeSpan StaminaSyncInterval = TimeSpan.FromSeconds(60);

    // Cumulative field trackers for validation — these fields only go up during a session.
    // Used to detect field misassignment (e.g. Supplies read as RawXpPerHour).
    private long? _lastLoot;
    private long? _lastDamage;
    private long? _lastHealing;
    private Dictionary<string, int> _lastKilledMonsterCounts = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PausedInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// After this many consecutive frames where OBS returns an image but no analyser
    /// is found, recycle the Game Capture source. OBS Game Capture sometimes goes stale
    /// (black frames) and needs a disable/re-enable to re-hook the game window.
    /// At idle tick rate (3s), 10 ticks = ~30s.
    /// </summary>
    private const int RecycleAfterEmptyFrames = 10;

    /// <summary>
    /// Suppress the "Hunt Analyser not visible" toast until we've seen this many
    /// consecutive empty frames. Right after login OBS Game Capture takes a couple
    /// of seconds to hook the window and OCR needs a pass to warm up; firing on the
    /// first empty frame races against that startup and produces a spurious toast
    /// even though the analyser is open. At idle tick rate (3s), 4 ticks = ~12s.
    /// </summary>
    private const int NotifyAfterEmptyFrames = 4;

    /// <summary>
    /// After the analyser is last seen, wait this long before showing the "not visible"
    /// toast. Validation failures can invalidate the crop and cause a few consecutive OCR
    /// misses even though the analyser IS visible; a short grace period avoids annoying
    /// false-positive notifications.
    /// </summary>
    private static readonly TimeSpan NotifyGracePeriod = TimeSpan.FromSeconds(20);

    public PeriodicCaptureLoop(
        ICaptureService captureService,
        OcrService ocrService,
        IPanelLocator analyserLocator,
        IPanelLocator skillsLocator,
        IPanelLocator xpAnalyserLocator,
        IHuntAnalyserParser parser,
        ISessionLifecycle sessionManager,
        ToastNotificationService notifications,
        ILogger logger,
        string characterName,
        PersonalBestScreenshotCapture? screenshotCapture = null,
        IReadOnlySet<DateOnly>? keepDiagnosticsDates = null,
        Action<bool>? onAnalyserStatusChanged = null,
        Action<HuntSnapshot?, IReadOnlyList<string>, IReadOnlyList<OcrWordInfo>, double?, byte[]?, string?>? onOcrTick = null,
        Action<int?, IReadOnlyList<OcrWordInfo>, byte[]?, string?>? onSkillsTick = null,
        Action<XpAnalyserRates?, IReadOnlyList<OcrWordInfo>, byte[]?, string?>? onXpAnalyserTick = null,
        Action<string, int?, bool>? onStaminaSync = null)
    {
        _onOcrTick = onOcrTick;
        _onSkillsTick = onSkillsTick;
        _onXpAnalyserTick = onXpAnalyserTick;
        _onStaminaSync = onStaminaSync;
        _captureService = captureService;
        _ocrService = ocrService;
        _screenshotCapture = screenshotCapture;
        _regionLocator = analyserLocator;
        _skillsLocator = skillsLocator;
        _xpAnalyserLocator = xpAnalyserLocator;
        _parser = parser;
        _sessionManager = sessionManager;
        _notifications = notifications;
        _logger = logger;
        _characterName = characterName;
        _diagnostics = new DebugDiagnostics(keepDiagnosticsDates);
        _onAnalyserStatusChanged = onAnalyserStatusChanged;
        sessionManager.SessionEnded += OnSessionEnded;
    }

    private void OnSessionEnded(HuntSession session, SessionEndReason reason)
    {
        _lastLoot = null;
        _lastDamage = null;
        _lastHealing = null;
        _lastKilledMonsterCounts = new(StringComparer.OrdinalIgnoreCase);
    }

    public void Start(IntPtr hwnd)
    {
        _hwnd = hwnd;
        Stop();
        _captureService.StartCapture(hwnd);
        _lastAnalyserFoundTime = DateTime.UtcNow;
        _lastXpAnalyserFoundTime = DateTime.UtcNow;
        _rawXpNotificationTracker.Reset();
        _cts = new CancellationTokenSource();
        _loopTask = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _captureService.StopCapture();
        _regionLocator.InvalidateCache();
        _skillsLocator.InvalidateCache();
        _xpAnalyserLocator.InvalidateCache();
        _xpAnalyserVisible = false;
        _xpAnalyserRetryCount = 0;
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Fires a final logout sync. Includes the last known stamina when OCR found one,
    /// but still notifies the server when stamina was never parsed.
    /// </summary>
    public void FlushStaminaSync()
    {
        _onStaminaSync?.Invoke(_characterName, _lastStamina, true);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync();
            }
            catch (Exception ex)
            {
                _logger.Error($"Capture loop error: {ex.Message}");
            }

            var interval = _sessionManager.State switch
            {
                SessionState.Active => ActiveInterval,
                SessionState.Paused => PausedInterval,
                _ => IdleInterval,
            };

            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private int _tickCount;
    private DateTime _lastSummaryLogTime = DateTime.MinValue;

    private async Task TickAsync()
    {
        _tickCount++;
        var bitmap = await _captureService.TryGetFrameAsync();
        if (bitmap == null)
        {
            if (_tickCount <= 5 || _tickCount % 30 == 0)
                _logger.Debug($"Tick {_tickCount}: no frame available");
            return;
        }

        try
        {
            var xpAnalyserRates = await ReadXpAnalyserAsync(bitmap);
            var result = await _regionLocator.LocateAsync(bitmap, _ocrService);

            bool analyserFound = result != null;
            if (_lastAnalyserFound != analyserFound)
            {
                _lastAnalyserFound = analyserFound;
                _onAnalyserStatusChanged?.Invoke(analyserFound);
            }

            if (result == null)
            {
                _consecutiveEmptyFrames++;

                // Log at key thresholds only
                if (_consecutiveEmptyFrames == 1 || _consecutiveEmptyFrames == NotifyAfterEmptyFrames
                    || _consecutiveEmptyFrames % RecycleAfterEmptyFrames == 0)
                {
                    var seconds = _consecutiveEmptyFrames * IdleInterval.TotalSeconds;
                    _logger.Debug($"Tick {_tickCount}: analyser not found for {_consecutiveEmptyFrames} frames (~{seconds:F0}s)");
                }

                // OBS Game Capture sometimes goes stale (black frames). Retry
                // recovery every RecycleAfterEmptyFrames ticks with escalating strategies.
                if (_consecutiveEmptyFrames > 0 &&
                    _consecutiveEmptyFrames % RecycleAfterEmptyFrames == 0 &&
                    _captureService is ObsCaptureService obs)
                {
                    int attempt = _consecutiveEmptyFrames / RecycleAfterEmptyFrames;
                    _logger.Info($"Recycling Game Capture — attempt {attempt}");
                    await obs.RecycleGameCaptureAsync(attempt);
                }

                // Only notify if the loop is still active and the character is still logged in.
                // After logout the window title drops the character name before the window
                // detection loop fires TibiaLost, so we'd otherwise show a spurious
                // "Hunt Analyser Not Visible" right before "Hunt Session Saved".
                // Also wait for NotifyAfterEmptyFrames consecutive misses so we don't race
                // OBS Game Capture warm-up right after login.
                if (_consecutiveEmptyFrames >= NotifyAfterEmptyFrames
                    && DateTime.UtcNow - _lastAnalyserFoundTime >= NotifyGracePeriod
                    && _cts is { IsCancellationRequested: false }
                    && TibiaWindowDetector.IsCharacterWindow(_hwnd))
                    _notifications.NotifyAnalyserNotFound();

                // A preprocessing experiment can make Tesseract reject an otherwise
                // valid crop. Still publish that failed frame to the debug window so
                // controls never appear frozen on the last successful preview.
                _onOcrTick?.Invoke(
                    null,
                    Array.Empty<string>(),
                    Array.Empty<OcrWordInfo>(),
                    null,
                    _regionLocator.LastPreprocessedPng,
                    _regionLocator.LastRegionDebug);
                return;
            }

            _consecutiveEmptyFrames = 0;

            var (_, words) = result.Value;

            // Positional spatial parsing: uses row order + column boundary to assign values
            // even when OCR drops labels
            var rows = _parser.GroupIntoRows(words);
            var valueColumnX = _parser.FindValueColumnX(rows);

            // Reconstruct lines for debug overlay
            var lines = valueColumnX.HasValue
                ? _parser.ReconstructLinesColumnAware(rows, valueColumnX.Value)
                : _parser.ReconstructLines(words);

            var snapshot = _parser.ParseSpatial(words);

            // Push to debug overlay if open
            _onOcrTick?.Invoke(snapshot, lines, words, valueColumnX, _regionLocator.LastPreprocessedPng, _regionLocator.LastRegionDebug);

            // Save per-tick diagnostics to disk for live debugging
            _diagnostics.RecordTick(_tickCount, words, valueColumnX, lines, snapshot,
                _regionLocator.LastPreprocessedPng, _sessionManager.State, _consecutiveEmptyFrames);

            // Determine if this is an anomaly worth detailed logging
            bool isAnomaly = snapshot == null
                || (snapshot.RawXpGain == null && snapshot.XpGain == null &&
                    snapshot.Loot == null && snapshot.Supplies == null &&
                    snapshot.Damage == null && snapshot.Healing == null);

            // Tiered logging: periodic summary or anomaly detail
            var summaryInterval = _sessionManager.State switch
            {
                SessionState.Active => TimeSpan.FromSeconds(30),
                SessionState.Paused => TimeSpan.FromSeconds(60),
                _ => TimeSpan.FromMinutes(3),
            };

            var now = DateTime.UtcNow;
            bool shouldLogSummary = isAnomaly || (now - _lastSummaryLogTime >= summaryInterval);

            if (shouldLogSummary)
            {
                _lastSummaryLogTime = now;
                if (snapshot != null)
                {
                    var monsterSummary = snapshot.KilledMonsters.Count > 0
                        ? string.Join(", ", snapshot.KilledMonsters.Take(3).Select(m => $"{m.Name}×{m.Count}"))
                        : "none";
                    _logger.Debug($"Tick {_tickCount}: xp={snapshot.XpGain} loot={snapshot.Loot} balance={snapshot.Balance} mobs=[{monsterSummary}] state={_sessionManager.State}");
                }
                else
                {
                    _logger.Debug($"Tick {_tickCount}: parser returned null — no session time in OCR output");
                }
            }

            // Full OCR detail ONLY on anomaly
            if (isAnomaly && lines.Count > 0)
            {
                _logger.Debug($"  Anomaly OCR detail: {words.Count} words, {lines.Count} lines, valueColX={valueColumnX?.ToString("F0") ?? "none"}");
                foreach (var line in lines)
                    _logger.Debug($"    OCR: {line}");
            }

            // The locator can occasionally anchor on a lower analyser row such as
            // Balance. That crop still OCRs cleanly, but it omits the XP rows we need.
            if (IsCropMissingTrackedRows(lines))
            {
                _logger.Debug($"Invalidating analyser crop: OCR starts below tracked rows ({lines.FirstOrDefault() ?? "no lines"})");
                _regionLocator.InvalidateCache();
                return;
            }

            if (snapshot == null)
                return;

            bool huntAnalyserRawXpMissing = snapshot.XpGain.HasValue
                && snapshot.RawXpGain == null
                && snapshot.RawXpPerHour is null or 0;
            bool xpAnalyserRawXpMissing = xpAnalyserRates?.XpPerHour.HasValue == true
                && xpAnalyserRates.RawXpPerHour is null or 0;

            // XP Analyser rates represent Tibia's rolling window and are more current
            // than the Hunt Analyser's session-adjusted rates. Each field falls back
            // independently when OCR can only read one of the two XP Analyser rows.
            snapshot = XpAnalyserParser.ApplyRates(snapshot, xpAnalyserRates);

            // If all stat values are null (dashes), the crop is likely wrong
            // (e.g. only showing the monster/loot section). Invalidate and re-scan.
            if (snapshot.RawXpGain == null && snapshot.XpGain == null &&
                snapshot.Loot == null && snapshot.Supplies == null &&
                snapshot.Damage == null && snapshot.Healing == null)
            {
                _regionLocator.InvalidateCache();
                return;
            }

            // Cross-field validation: detect field misassignment caused by the parser's
            // positional fallback reading the wrong rows (e.g. Supplies/Balance stored as XP
            // when the panel is scrolled and labels aren't recognized).
            if (_sessionManager.CurrentSession != null && !ValidateSnapshot(snapshot))
            {
                _regionLocator.InvalidateCache();
                return;
            }

            TrackMissingRawXp(huntAnalyserRawXpMissing, xpAnalyserRawXpMissing);

            // Update cumulative trackers after validation passes
            if (snapshot.Loot.HasValue) _lastLoot = snapshot.Loot;
            if (snapshot.Damage.HasValue) _lastDamage = snapshot.Damage;
            if (snapshot.Healing.HasValue) _lastHealing = snapshot.Healing;
            UpdateKilledMonsterCounts(snapshot);

            // Only reset the notification grace period after a genuinely valid read —
            // not on every OCR detection, since validation failures can cascade.
            _lastAnalyserFoundTime = DateTime.UtcNow;

            // Always use our own elapsed session time instead of the Hunt Analyser's timer.
            // Our app manages its own sessions (starts on first kill, ends on mob set change
            // or inactivity), so the in-game timer doesn't match our session boundaries.
            if (_sessionManager.CurrentSession is { } session)
            {
                snapshot = snapshot with { SessionTime = DateTime.UtcNow - session.StartedAtUtc };
            }

            // Read stamina from Skills widget (rate-limited — stamina changes slowly,
            // but retry every tick when the crop is failing so we invalidate in ~5s)
            if (_skillsRetrying || ++_skillsReadCounter % 10 == 0)
            {
                try
                {
                    var skillsResult = await _skillsLocator.LocateAsync(bitmap, _ocrService);
                    if (skillsResult.HasValue)
                    {
                        _skillsRetrying = false;
                        var stamina = SkillsParser.ParseStamina(_parser, skillsResult.Value.Words);
                        if (stamina.HasValue)
                        {
                            snapshot = snapshot with { Stamina = stamina };
                            _lastStamina = stamina;
                        }

                        _onSkillsTick?.Invoke(stamina, skillsResult.Value.Words,
                            _skillsLocator.LastPreprocessedPng, _skillsLocator.LastRegionDebug);
                    }
                    else
                    {
                        _skillsRetrying = true;
                        _onSkillsTick?.Invoke(null, Array.Empty<OcrWordInfo>(),
                            _skillsLocator.LastPreprocessedPng, _skillsLocator.LastRegionDebug);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Skills panel read failed: {ex.Message}");
                }
            }

            // Sync stamina to server every 60 seconds during active hunting
            if (_lastStamina.HasValue && DateTime.UtcNow - _lastStaminaSyncTime >= StaminaSyncInterval)
            {
                _lastStaminaSyncTime = DateTime.UtcNow;
                _onStaminaSync?.Invoke(_characterName, _lastStamina.Value, false);
            }

            _sessionManager.OnSnapshot(snapshot, _characterName);

            // Capture PB screenshot if beating the record
            if (_screenshotCapture != null && _sessionManager.CurrentSession is { } activeSession)
            {
                var sessionTimeSeconds = (int)(DateTime.UtcNow - activeSession.StartedAtUtc).TotalSeconds;
                await _screenshotCapture.OnTickAsync(snapshot, bitmap, sessionTimeSeconds);
            }
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    private async Task<XpAnalyserRates?> ReadXpAnalyserAsync(SoftwareBitmap bitmap)
    {
        bool wasVisibleOrRetrying = _xpAnalyserVisible || _xpAnalyserRetryCount > 0;
        bool shouldRead = wasVisibleOrRetrying
            || ++_xpAnalyserReadCounter % XpAnalyserAbsentScanIntervalTicks == 0;
        if (!shouldRead)
            return null;

        try
        {
            var result = await _xpAnalyserLocator.LocateAsync(bitmap, _ocrService);
            if (result.HasValue)
            {
                _xpAnalyserVisible = true;
                _xpAnalyserRetryCount = 0;
                _lastXpAnalyserFoundTime = DateTime.UtcNow;
                var rates = XpAnalyserParser.Parse(_parser, result.Value.Words);
                _onXpAnalyserTick?.Invoke(rates, result.Value.Words,
                    _xpAnalyserLocator.LastPreprocessedPng, _xpAnalyserLocator.LastRegionDebug);
                return rates;
            }

            _xpAnalyserVisible = false;
            if (wasVisibleOrRetrying)
            {
                _xpAnalyserRetryCount++;
                if (_xpAnalyserRetryCount >= XpAnalyserRetryLimit)
                    _xpAnalyserRetryCount = 0;
            }
            NotifyIfXpAnalyserMissing();

            _onXpAnalyserTick?.Invoke(null, Array.Empty<OcrWordInfo>(),
                _xpAnalyserLocator.LastPreprocessedPng, _xpAnalyserLocator.LastRegionDebug);
        }
        catch (Exception ex)
        {
            _logger.Debug($"XP Analyser panel read failed: {ex.Message}");
            _xpAnalyserVisible = false;
            _xpAnalyserRetryCount = Math.Min(_xpAnalyserRetryCount + 1, XpAnalyserRetryLimit - 1);
            NotifyIfXpAnalyserMissing();
        }

        return null;
    }

    private void NotifyIfXpAnalyserMissing()
    {
        if (DateTime.UtcNow - _lastXpAnalyserFoundTime < NotifyGracePeriod
            || _cts is not { IsCancellationRequested: false }
            || !TibiaWindowDetector.IsCharacterWindow(_hwnd))
        {
            return;
        }

        _notifications.NotifyXpAnalyserNotFound();
    }

    private void TrackMissingRawXp(bool huntAnalyserMissing, bool xpAnalyserMissing)
    {
        var missing = _rawXpNotificationTracker.Observe(
            huntAnalyserMissing,
            xpAnalyserMissing);
        if (missing == null)
            return;

        _notifications.NotifyRawExpNotTracked(
            missing.HuntAnalyser,
            missing.XpAnalyser);
    }

    /// <summary>
    /// Cross-field validation to detect field misassignment caused by the parser's
    /// positional fallback reading the wrong rows (e.g. Supplies/Balance stored as
    /// RawXpPerHour/XpPerHour when the panel is scrolled and labels aren't recognized).
    /// Returns false if the snapshot looks suspicious and should be rejected.
    /// </summary>
    private static bool IsCropMissingTrackedRows(IReadOnlyList<string> lines)
    {
        var meaningfulLines = lines
            .Select(NormalizeOcrLine)
            .Where(line => line.Length > 0 && line != "none")
            .ToList();

        if (meaningfulLines.Count == 0)
            return false;

        bool hasExpectedTopRows = meaningfulLines.Any(line =>
            line.Contains("session", StringComparison.Ordinal)
            || line.Contains("raw xp", StringComparison.Ordinal)
            || line.Contains("xp gain", StringComparison.Ordinal)
            || IsStatLabel(line, "loot")
            || IsStatLabel(line, "supplies"));

        if (hasExpectedTopRows)
            return false;

        var first = meaningfulLines[0];
        return first.StartsWith("balance", StringComparison.Ordinal)
            || first.StartsWith("damage", StringComparison.Ordinal)
            || first.StartsWith("healing", StringComparison.Ordinal)
            || first.StartsWith("killed monsters", StringComparison.Ordinal)
            || first.StartsWith("looted items", StringComparison.Ordinal);
    }

    private static string NormalizeOcrLine(string line)
    {
        return line
            .Trim()
            .TrimStart('\\', '/', '|', '-', '_', ':')
            .Trim()
            .ToLowerInvariant();
    }

    private static bool IsStatLabel(string line, string label)
    {
        return line == label
            || line.StartsWith(label + ":", StringComparison.Ordinal)
            || line.StartsWith(label + " ", StringComparison.Ordinal);
    }

    private bool ValidateSnapshot(HuntSnapshot snapshot)
    {
        // 0. Analyser reset — all cumulative fields dropped to zero/null simultaneously.
        // This happens when the user resets the Hunt Analyser in-game. Let it through
        // so the state machine's IsAnalyserReset check can end the session cleanly.
        bool allZero = (snapshot.Loot is null or 0)
            && (snapshot.Damage is null or 0)
            && (snapshot.Healing is null or 0)
            && (snapshot.XpGain is null or 0);
        if (allZero && (_lastLoot is > 0 || _lastDamage is > 0 || _lastHealing is > 0))
        {
            _logger.Debug("Validation passed: all fields zero/null — likely analyser reset");
            return true;
        }

        // 1. Cumulative field decrease — Loot, Damage, Healing can only go up during a
        // session. However, OCR noise causes ±2-5% fluctuation on individual fields
        // frame-to-frame, which is normal. A genuine row-level misassignment (e.g. after
        // the user scrolls the analyser panel) affects ALL visible cumulative fields
        // simultaneously. So we only invalidate when every tracked field decreased.
        // (RawXpGain/XpGain can decrease on death, so we don't check those.)
        bool lootDecreased = snapshot.Loot.HasValue && _lastLoot.HasValue
            && snapshot.Loot.Value < _lastLoot.Value;
        bool damageDecreased = snapshot.Damage.HasValue && _lastDamage.HasValue
            && snapshot.Damage.Value < _lastDamage.Value;
        bool healingDecreased = snapshot.Healing.HasValue && _lastHealing.HasValue
            && snapshot.Healing.Value < _lastHealing.Value;

        // Count how many fields we're actually tracking (have both previous and current)
        int trackedCount = (snapshot.Loot.HasValue && _lastLoot.HasValue ? 1 : 0)
            + (snapshot.Damage.HasValue && _lastDamage.HasValue ? 1 : 0)
            + (snapshot.Healing.HasValue && _lastHealing.HasValue ? 1 : 0);
        int decreasedCount = (lootDecreased ? 1 : 0) + (damageDecreased ? 1 : 0) + (healingDecreased ? 1 : 0);

        // Only fail if every tracked field decreased — single-field drops are OCR noise
        if (trackedCount >= 2 && decreasedCount == trackedCount)
        {
            if (HasKillCounterDecrease(snapshot))
            {
                _logger.Debug($"Validation passed: cumulative fields and kill counters decreased " +
                    $"(loot={_lastLoot}→{snapshot.Loot}, damage={_lastDamage}→{snapshot.Damage}, " +
                    $"healing={_lastHealing}→{snapshot.Healing}) — likely analyser counter reset");
                return true;
            }

            _logger.Debug($"Validation failed: all {trackedCount} tracked fields decreased " +
                $"(loot={_lastLoot}→{snapshot.Loot}, damage={_lastDamage}→{snapshot.Damage}, " +
                $"healing={_lastHealing}→{snapshot.Healing}) — likely row-level misassignment");
            return false;
        }

        // 2. XP values present but ALL cumulative fields null — the "XP" values are
        // almost certainly financial fields misread as XP. When the parser's positional
        // fallback kicks in after a scroll, Supplies/Balance get assigned to XP fields
        // while the actual XP/loot/damage rows aren't visible.
        if (snapshot.RawXpPerHour is > 0
            && snapshot.RawXpGain == null && snapshot.XpGain == null
            && snapshot.Loot == null && snapshot.Supplies == null
            && snapshot.Damage == null && snapshot.Healing == null)
        {
            _logger.Debug($"Validation failed: XP rates present ({snapshot.RawXpPerHour}/{snapshot.XpPerHour}) but all cumulative fields null — likely field misassignment");
            return false;
        }

        // 3. Inverted XP ratio — XpPerHour should always be >= RawXpPerHour since
        // boosts (stamina, store) multiply raw XP. An inversion means the fields are
        // swapped or contain values from different rows.
        if (snapshot.RawXpPerHour is > 0 && snapshot.XpPerHour is > 0
            && snapshot.XpPerHour.Value < snapshot.RawXpPerHour.Value)
        {
            _logger.Debug($"Validation failed: XpPerHour ({snapshot.XpPerHour}) < RawXpPerHour ({snapshot.RawXpPerHour})");
            return false;
        }

        return true;
    }

    private bool HasKillCounterDecrease(HuntSnapshot snapshot)
    {
        if (_lastKilledMonsterCounts.Count == 0 || snapshot.KilledMonsters.Count == 0)
            return false;

        var previousTotal = _lastKilledMonsterCounts.Values.Sum();
        var currentTotal = snapshot.KilledMonsters.Sum(m => m.Count);

        if (currentTotal >= previousTotal)
            return false;

        return snapshot.KilledMonsters.Any(m =>
            _lastKilledMonsterCounts.TryGetValue(m.Name, out var previous) && m.Count < previous);
    }

    private void UpdateKilledMonsterCounts(HuntSnapshot snapshot)
    {
        foreach (var monster in snapshot.KilledMonsters)
        {
            var current = _lastKilledMonsterCounts.TryGetValue(monster.Name, out var known) ? known : 0;
            _lastKilledMonsterCounts[monster.Name] = Math.Max(current, monster.Count);
        }
    }

    public void Dispose()
    {
        Stop();
        _diagnostics.Dispose();
        _regionLocator.Dispose();
        _skillsLocator.Dispose();
        _xpAnalyserLocator.Dispose();
    }
}
