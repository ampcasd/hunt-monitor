using TibiaSquare.HuntMonitor.Analytics;
using TibiaSquare.HuntMonitor.Auth;
using TibiaSquare.HuntMonitor.Capture;
using TibiaSquare.HuntMonitor.Models;
using TibiaSquare.HuntMonitor.Notifications;
using TibiaSquare.HuntMonitor.Obs;
using TibiaSquare.HuntMonitor.Ocr;
using TibiaSquare.HuntMonitor.Private;
using TibiaSquare.HuntMonitor.Screenshots;
using TibiaSquare.HuntMonitor.Storage;
using TibiaSquare.HuntMonitor.Sync;
using TibiaSquare.HuntMonitor.Tray;

namespace TibiaSquare.HuntMonitor.Infrastructure;

public sealed class AppLifecycleManager : IDisposable
{
    private readonly TrayIconManager _trayIconManager;
    private readonly FileLogger _logger = new();
    private readonly ToastNotificationService _notifications = new();

    private WindowDetectionLoop? _windowDetectionLoop;
    private SqliteSessionStore? _store;
    private ISessionLifecycle? _sessionManager;

    // OBS management — created on startup, launched/stopped per Tibia lifecycle
    private ObsProcessManager? _obsManager;
    private ObsCaptureService? _obsCaptureService;
    private Task<bool>? _obsLaunchTask;
    private CancellationTokenSource? _obsLaunchCts;

    // Capture resources — created/destroyed per character session
    private OcrService? _ocrService;
    private PeriodicCaptureLoop? _captureLoop;
    private PersonalBestScreenshotCapture? _screenshotCapture;

    // Auth & Sync & Analytics
    private DiscordOAuthService? _authService;
    private SyncService? _syncService;
    private ISessionProcessor? _sessionProcessor;
    private AnalyticsService? _analytics;

    // Diagnostics
    private DiagnosticReportService? _reportService;

    // Debug overlay
    private IHuntAnalyserParser? _parser;
    private DebugOverlayWindow? _debugOverlay;

    // Suppress "New Hunt Session" toast after a mob-change split (we already showed "New Mobs Detected")
    private bool _suppressNextStartNotification;

    // Track last known game resolution to detect changes between sessions
    private (int W, int H) _lastGameResolution;

    // Track last tooltip to avoid redundant updates
    private string _lastTooltipText = "";

    // Lazy init guard — heavy services only start when Tibia is first detected
    private bool _servicesInitialized;

    public AppLifecycleManager(TrayIconManager trayIconManager)
    {
        _trayIconManager = trayIconManager;
    }

    public void Start()
    {
        _logger.Info($"Application starting from: {AppContext.BaseDirectory}");

        // Wire tray menu actions (safe with null checks — services init later)
        _trayIconManager.LoginRequested += () => { if (_authService != null) _ = _authService.LoginAsync(); };
        _trayIconManager.LogoutRequested += () =>
        {
            _authService?.Logout();
            _trayIconManager.UpdateReportAuth(isAuthenticated: false);
        };
#if DEBUG
        _trayIconManager.EndHuntRequested += () =>
        {
            if (_sessionManager?.EndSessionManually() == true)
                _logger.Info("Session ended manually via tray menu");
        };
#endif
        _trayIconManager.SyncRequested += () => _ = ManualSyncAsync();
        _trayIconManager.SendReportRequested += () => _ = OpenReportDialogAsync();
        _trayIconManager.DebugOverlayRequested += () =>
        {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                if (_parser == null) return;
                _debugOverlay ??= new DebugOverlayWindow(_parser);
                if (_debugOverlay.IsVisible)
                    _debugOverlay.Hide();
                else
                    _debugOverlay.Show();
            });
        };
        // Initialize auth early — triggers Discord login if no saved tokens
        _ = InitializeAuthEarlyAsync();

        // Start window detection (lightweight — just P/Invoke every 3s)
        _windowDetectionLoop = new WindowDetectionLoop();
        _windowDetectionLoop.TibiaOpened += OnTibiaOpened;
        _windowDetectionLoop.TibiaClosed += OnTibiaClosed;
        _windowDetectionLoop.TibiaDetected += OnTibiaDetected;
        _windowDetectionLoop.TibiaLost += OnTibiaLost;
        _windowDetectionLoop.Start();

        // Set initial status based on whether Tibia is already running.
        // The loop's first tick already fired events if Tibia was found,
        // but OnTibiaOpened is async and hasn't finished yet — so we show
        // an intermediate status instead of the misleading "sleeping" default.
        var tibiaOnStartup = TibiaWindowDetector.FindFirst();
        if (tibiaOnStartup != null)
        {
            _trayIconManager.UpdateTooltip("Tibia detected - launching OBS...");
            _trayIconManager.UpdateObsStatus("Starting...");
        }
        else
        {
            _trayIconManager.UpdateTooltip("Sleeping - No Tibia launched");
            _trayIconManager.UpdateObsStatus("Not running (No Tibia)");
        }

        // Enable efficiency mode while idle (green leaf in Task Manager)
        EfficiencyMode.Enable();

        _analytics?.Track("app_started");
        _logger.Info("Application started — waiting for Tibia");
    }

    /// <summary>
    /// Called by App.xaml.cs global error handlers before swallowing exceptions.
    /// </summary>
    public void LogUnhandledException(Exception ex)
    {
        _logger.Error("Unhandled exception — app will attempt to continue", ex);
    }

    /// <summary>
    /// Initializes auth eagerly on app startup so users can log in immediately.
    /// </summary>
    private async Task InitializeAuthEarlyAsync()
    {
        try
        {
            var config = AppConfig.Load();
            if (!config.IsValid)
            {
                _logger.Info("No config.json found or invalid — auth disabled");
                return;
            }

            if (!config.HasSecureUrls)
            {
                _logger.Warn("Config URLs must use HTTPS — refusing to start with insecure URLs");
                return;
            }

            _authService = new DiscordOAuthService(config.SupabaseUrl, config.SupabaseAnonKey, _logger);
            if (!string.IsNullOrEmpty(config.ApiBaseUrl))
                _analytics = new AnalyticsService(config.ApiBaseUrl, _authService);
            _authService.AuthStateChanged += userName =>
            {
                _trayIconManager.UpdateAuthStatus(userName);
                _trayIconManager.UpdateReportAuth(isAuthenticated: userName != null);
                _analytics?.Track("auth_completed");
            };

            await _authService.LoadSavedTokensAsync();
            _trayIconManager.UpdateAuthStatus(_authService.CurrentTokens?.UserName);
            _trayIconManager.UpdateReportAuth(isAuthenticated: _authService.IsAuthenticated);

            if (!_authService.IsAuthenticated)
            {
                if (!_authService.HadSavedTokens)
                {
                    _logger.Info("First run — triggering Discord login and enabling startup");
                    await StartupManager.SetEnabledAsync(true);
                    await _authService.LoginAsync();
                }
                else
                {
                    _logger.Info("Tokens expired, refresh failed — user can login via tray menu");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Early auth init failed", ex);
        }
    }

    /// <summary>
    /// Initializes all heavy services (DB, auth, sync, session manager, OBS).
    /// Called lazily on first Tibia detection so the app stays ultra-light until needed.
    /// </summary>
    private async Task EnsureServicesInitializedAsync()
    {
        if (_servicesInitialized) return;
        _servicesInitialized = true;

        _logger.Info("Initializing services (first Tibia detection)");

        // Init storage
        var connection = DatabaseMigrator.CreateAndMigrate();
        _store = new SqliteSessionStore(connection);

        _sessionProcessor = ProcessingAssemblyLoader.CreateSessionProcessor();
        if (_sessionProcessor == null)
            _logger.Warn("Processing DLL not loaded — session processing disabled");
        else
            _screenshotCapture = new PersonalBestScreenshotCapture(_store, _sessionProcessor, _logger);

        // Wire hunt-date-aware log purge now that DB is available
        _logger.SetHuntDatesProvider(() => _store.GetHuntDates());

        // Init sync (auth already initialized early in Start())
        await InitializeSyncAsync();

        // Init session manager + recover crashes
        _sessionManager = ProcessingAssemblyLoader.CreateSessionManager(_store, _logger);
        if (_sessionManager == null)
        {
            _logger.Warn("Processing DLL not loaded — session tracking disabled");
            return;
        }
        _sessionManager.SessionStarted += s =>
        {
            _analytics?.Track("hunt_session_started", new Dictionary<string, object?>
            {
                ["character_name"] = s.CharacterName,
            });
            if (_suppressNextStartNotification)
                _suppressNextStartNotification = false;
            else
                _notifications.NotifySessionStarted(s);
            _trayIconManager.SetHuntActive(true);
            _screenshotCapture?.OnSessionStarted(s.Id, s.CharacterName);
        };
        _sessionManager.SessionEnded += (s, reason) =>
        {
            var lastSnapshot = _store.GetLastSnapshot(s.Id);
            _analytics?.Track("hunt_session_ended", new Dictionary<string, object?>
            {
                ["character_name"] = s.CharacterName,
                ["reason"] = reason.ToString(),
                ["duration_seconds"] = s.ActiveDurationSeconds,
            });
            _trayIconManager.SetHuntActive(false);
            _notifications.NotifySessionEnded(s, reason, lastSnapshot);
            if (reason == SessionEndReason.MobSetChanged)
                _suppressNextStartNotification = true;

            // Finalize PB screenshots (pick best 5, delete rest)
            _screenshotCapture?.OnSessionEnded(s.Id);

            // Sync session + upload screenshots in background, then refresh tray status
            if (_syncService != null)
            {
                _trayIconManager.UpdateSyncInProgress(true);
                _ = _syncService.SyncSessionAsync(s)
                    .ContinueWith(async task =>
                    {
                        try
                        {
                            await _syncService.UploadScreenshotsAsync(s.Id);

                            var result = task.Result;
                            if (result == SyncResult.Failed)
                                _notifications.NotifySyncFailed();
                            else if (result == SyncResult.Success)
                                _notifications.NotifySyncSucceeded();
                            // NotAuthenticated: no toast — tray already shows status
                        }
                        finally
                        {
                            RefreshSyncStatus();
                            _trayIconManager.UpdateSyncInProgress(false);
                        }
                    });
            }
            else
            {
                RefreshSyncStatus();
            }
        };
        _sessionManager.RecoverUnfinishedSessions();

        // Sync pending sessions if authenticated
        if (_syncService != null && _authService?.IsAuthenticated == true)
        {
            var pendingResult = await _syncService.SyncPendingAsync();
            if (pendingResult.Synced > 0)
                _notifications.NotifyPendingSyncRecovered(pendingResult.Synced);
        }

        RefreshSyncStatus();

        // Init diagnostic report service
        if (_authService != null)
        {
            var config = AppConfig.Load();
            var apiBaseUrl = !string.IsNullOrEmpty(config.ApiBaseUrl) ? config.ApiBaseUrl : config.SupabaseUrl;
            _reportService = new DiagnosticReportService(_store, _authService, _logger, apiBaseUrl);
        }

        // Init OBS manager
        _obsManager = new ObsProcessManager(_logger);
        _obsManager.ObsStarted += () => _trayIconManager.UpdateObsStatus("Running");
        _obsManager.ObsFailed += error =>
        {
            _trayIconManager.UpdateObsStatus("Error");
            _notifications.ShowError("OBS Error", error);
        };

        var obsDir = _obsManager.FindObsDirectory();
        if (obsDir == null)
        {
            _notifications.ShowError("OBS Required",
                "OBS Portable not found. Place the obs-portable folder next to the application.");
        }
    }

    private Task InitializeSyncAsync()
    {
        if (_authService == null)
        {
            _logger.Info("Auth not initialized — sync disabled");
            return Task.CompletedTask;
        }

        var config = AppConfig.Load();
        var apiBaseUrl = !string.IsNullOrEmpty(config.ApiBaseUrl) ? config.ApiBaseUrl : config.SupabaseUrl;
        if (_sessionProcessor == null)
        {
            _logger.Warn("Processing DLL not found — session sync disabled");
            return Task.CompletedTask;
        }
        _syncService = new SyncService(_store!, _authService, _sessionProcessor, _logger, apiBaseUrl);
        return Task.CompletedTask;
    }

    private void RefreshSyncStatus()
    {
        if (_store == null) return;
        var count = _store.GetUnsyncedCompletedSessionCount();
        _trayIconManager.UpdateSyncStatus(count);
    }

    private async Task ManualSyncAsync()
    {
        if (_syncService == null || _authService == null)
        {
            _logger.Info("Sync not available — services not initialized");
            return;
        }

        if (!_authService.IsAuthenticated)
        {
            _logger.Info("Not authenticated — triggering login before sync");
            await _authService.LoginAsync();
            if (!_authService.IsAuthenticated)
            {
                _logger.Info("Login failed or cancelled — aborting sync");
                return;
            }
        }

        _trayIconManager.UpdateSyncInProgress(true);
        try
        {
            var result = await _syncService.SyncPendingAsync();
            if (result.Synced > 0)
                _notifications.NotifyPendingSyncRecovered(result.Synced);
        }
        finally
        {
            RefreshSyncStatus();
            _trayIconManager.UpdateSyncInProgress(false);
        }
    }

    private async Task OpenReportDialogAsync()
    {
        if (_reportService == null)
        {
            _logger.Warn("Diagnostic report service not initialized");
            return;
        }

        if (_authService == null || !_authService.IsAuthenticated)
        {
            _logger.Info("Not authenticated — triggering login before sending report");
            await _authService!.LoginAsync();
            if (!_authService.IsAuthenticated)
            {
                _logger.Info("Login failed or cancelled — aborting report");
                return;
            }
        }

        try
        {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                var dialog = new ReportDialogWindow(_reportService);
                var result = dialog.ShowDialog();
                if (result == true)
                {
                    _trayIconManager.UpdateReportSent();
                    _logger.Info("Diagnostic report sent successfully via dialog");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to open report dialog", ex);
        }
    }

    /// <summary>
    /// Any Tibia window appeared (login screen or logged in).
    /// Initializes services and launches OBS proactively.
    /// </summary>
    private async void OnTibiaOpened()
    {
        _logger.Info("Tibia process detected");
        _analytics?.Track("tibia_detected");
        EfficiencyMode.Disable();

        await EnsureServicesInitializedAsync();

        _trayIconManager.UpdateTooltip("Tibia detected");
        _notifications.NotifyTibiaDetected();

#if DEBUG
        // Show debug overlay when Tibia is detected
        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
        {
            if (_parser == null) return;
            _debugOverlay ??= new DebugOverlayWindow(_parser);
            _debugOverlay.Show();
        });
#endif

        // Start OBS launch in background so it's ready for capture
        _ = EnsureObsRunningAsync();
    }

    /// <summary>
    /// All Tibia windows gone. Shuts down OBS and goes to sleep.
    /// </summary>
    private void OnTibiaClosed()
    {
        _logger.Info("Tibia process closed");
        _analytics?.Track("tibia_closed");
        EfficiencyMode.Enable();
        _notifications.NotifyTibiaClosed();
        _trayIconManager.UpdateTooltip("Sleeping - No Tibia launched");

        RefreshSyncStatus();
        StopObs();
    }

    /// <summary>
    /// Character logged in — start the capture pipeline.
    /// </summary>
    private async void OnTibiaDetected(TibiaWindowInfo info)
    {
        if (_sessionManager == null)
        {
            _logger.Warn("Session tracking not initialized — capture will not start");
            return;
        }

        _logger.Info($"Character detected: {info.CharacterName} (HWND: {info.Hwnd})");
        _analytics?.Track("character_detected", new Dictionary<string, object?>
        {
            ["character_name"] = info.CharacterName,
        });
        _trayIconManager.UpdateTooltip("Hunt Analyser not visible");

        // Detect game resolution from the client area of the Tibia window.
        // We use this to set the OBS canvas and screenshot dimensions so that
        // the full game window is captured regardless of aspect ratio (ultrawide, etc.).
        var gameWidth = info.ClientWidth > 0 ? info.ClientWidth : 1920;
        var gameHeight = info.ClientHeight > 0 ? info.ClientHeight : 1080;
        _logger.Info($"Tibia monitor resolution: {gameWidth}×{gameHeight}");

        // Ensure OBS is ready (may already be running from OnTibiaOpened)
        var obsReady = await EnsureObsRunningAsync();
        if (!obsReady)
        {
            _notifications.ShowError("OBS Error",
                "Failed to start OBS. Capture cannot begin.");
            return;
        }

        // If the game resolution changed since last session, update OBS config and restart
        if (_lastGameResolution.W != gameWidth || _lastGameResolution.H != gameHeight)
        {
            var obsDir = _obsManager?.ObsDirectory;
            if (obsDir != null)
            {
                var canvasChanged = ObsConfigGenerator.UpdateCanvasSize(obsDir, gameWidth, gameHeight);
                if (canvasChanged)
                {
                    _logger.Info($"Game resolution changed to {gameWidth}×{gameHeight} — restarting OBS");
                    _obsCaptureService?.Dispose();
                    _obsCaptureService = null;

                    if (_obsManager != null)
                    {
                        var restarted = await _obsManager.RestartAsync();
                        if (restarted)
                        {
                            _obsCaptureService = new ObsCaptureService(_obsManager, _logger);
                        }
                        else
                        {
                            _logger.Error("OBS restart failed after resolution change");
                            _notifications.ShowError("OBS Error",
                                "Failed to restart OBS with new resolution. Capture cannot begin.");
                            return;
                        }
                    }
                }
                _lastGameResolution = (gameWidth, gameHeight);
            }
        }

        // Configure screenshot dimensions to match the game resolution
        _obsCaptureService?.SetGameResolution(gameWidth, gameHeight);

        try
        {
            _logger.Info("Using OBS WebSocket capture");

            // Configure Game Capture source to target the Tibia window
            _ = ConfigureObsForTibiaAsync(info);

            _ocrService = new OcrService();

            // Create processing components from the optional Processing DLL
            var preprocessor = ProcessingAssemblyLoader.CreateImagePreprocessor();
            _parser = ProcessingAssemblyLoader.CreateHuntAnalyserParser();
            var parser = _parser;
            var analyserLocator = preprocessor != null
                ? ProcessingAssemblyLoader.CreateAnalyserRegionLocator(_logger, preprocessor)
                : null;
            var skillsLocator = preprocessor != null
                ? ProcessingAssemblyLoader.CreateSkillsPanelLocator(_logger, preprocessor)
                : null;

            if (parser == null || analyserLocator == null || skillsLocator == null)
            {
                _logger.Warn("Processing DLL not found — capture will not start. Place TibiaSquare.HuntMonitor.Processing.dll in lib/.");
                _notifications.ShowError("Processing Missing",
                    "The OCR processing module was not found. Hunt tracking is disabled.");
                ReleaseCaptureResources();
                return;
            }

            var characterName = info.CharacterName!;
            var huntDates = _store?.GetHuntDates().Take(2).ToHashSet();
            _captureLoop = new PeriodicCaptureLoop(
                _obsCaptureService!,
                _ocrService,
                analyserLocator,
                skillsLocator,
                parser,
                _sessionManager!,
                _notifications,
                _logger,
                characterName,
                _screenshotCapture,
                huntDates,
                analyserFound =>
                {
                    UpdateTooltip(analyserFound);
                },
                (snapshot, lines, words, valueColumnX, preprocessedPng, regionDebug) =>
                {
                    _debugOverlay?.UpdateData(snapshot, lines, words, valueColumnX, preprocessedPng, regionDebug);
                    // Update tooltip periodically to keep hunting duration current
                    UpdateTooltip(true);
                },
                (stamina, words, preprocessedPng, regionDebug) =>
                {
                    _debugOverlay?.UpdateSkillsData(stamina, words, preprocessedPng, regionDebug);
                },
                (characterName, staminaMinutes, isLogout) =>
                {
                    if (_syncService != null)
                        _ = _syncService.SyncStaminaAsync(characterName, staminaMinutes, isLogout);
                });

            _captureLoop.Start(info.Hwnd);
            _logger.Info("Capture loop started");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to start capture", ex);
            _notifications.ShowError("Capture Failed",
                $"Failed to start capture: {ex.Message}");
            ReleaseCaptureResources();
        }
    }

    /// <summary>
    /// Character lost (logged out or window changed).
    /// Stops capture but keeps OBS running for the next login.
    /// </summary>
    private void OnTibiaLost()
    {
        _logger.Info("Character lost");

        _captureLoop?.FlushStaminaSync();
        _captureLoop?.Stop();
        _sessionManager?.OnWindowLost();
        ReleaseCaptureResources();

        // Tibia may still be running (login screen).
        // If TibiaClosed fires next, it'll overwrite this tooltip.
        _trayIconManager.UpdateTooltip("Tibia detected");
    }

    private void UpdateTooltip(bool analyserFound)
    {
        string text;
        if (!analyserFound)
        {
            text = "Hunt Analyser not visible";
        }
        else if (_sessionManager?.CurrentSession is { } session)
        {
            var elapsed = DateTime.UtcNow - session.StartedAtUtc;
            text = $"Hunt Analyser visible - Hunting {FormatDuration(elapsed)}";
        }
        else
        {
            text = "Hunt Analyser visible - Not Hunting";
        }

        if (text != _lastTooltipText)
        {
            _lastTooltipText = text;
            _trayIconManager.UpdateTooltip(text);
        }
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h:{ts.Minutes:D2}m";
        return $"{(int)ts.TotalMinutes}m";
    }

    #region OBS Lifecycle

    /// <summary>
    /// Ensures OBS is running. Multiple callers safely share the same launch task.
    /// </summary>
    private Task<bool> EnsureObsRunningAsync()
    {
        if (_obsManager == null)
            return Task.FromResult(false);

        if (_obsCaptureService != null && _obsManager.IsRunning)
            return Task.FromResult(true);

        if (_obsLaunchTask != null)
            return _obsLaunchTask;

        _obsLaunchCts = new CancellationTokenSource();
        _obsLaunchTask = LaunchObsInternalAsync(_obsLaunchCts.Token);
        return _obsLaunchTask;
    }

    private async Task<bool> LaunchObsInternalAsync(CancellationToken ct)
    {
        try
        {
            _trayIconManager.UpdateObsStatus("Starting...");

            var started = await _obsManager!.LaunchAsync();

            if (ct.IsCancellationRequested)
            {
                // Tibia closed while OBS was starting — stop it immediately
                _logger.Info("OBS launch cancelled (Tibia closed during startup)");
                _obsManager.Stop();
                _trayIconManager.UpdateObsStatus("Not running (No Tibia)");
                return false;
            }

            if (started)
            {
                _logger.Info("OBS started — using OBS WebSocket capture");
                _obsCaptureService = new ObsCaptureService(_obsManager, _logger);
                return true;
            }

            _logger.Error("OBS failed to start");
            _trayIconManager.UpdateObsStatus("Failed");
            return false;
        }
        finally
        {
            _obsLaunchTask = null;
            _obsLaunchCts?.Dispose();
            _obsLaunchCts = null;
        }
    }

    private void StopObs()
    {
        // Cancel any in-flight OBS launch
        _obsLaunchCts?.Cancel();

        _obsCaptureService?.Dispose();
        _obsCaptureService = null;

        _obsManager?.Stop();
        _obsLaunchTask = null;

        _trayIconManager.UpdateObsStatus("Not running (No Tibia)");
        _logger.Info("OBS stopped — going to sleep");
    }

    #endregion

    private async Task ConfigureObsForTibiaAsync(TibiaWindowInfo info)
    {
        if (_obsCaptureService == null) return;

        try
        {
            // Give OBS WebSocket a moment to come up on first use
            await Task.Delay(2000);

            _logger.Info($"Configuring OBS for Tibia: {info.ObsWindowString}");
            await _obsCaptureService.ConfigureForTibiaAsync(info);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to configure OBS Game Capture", ex);
        }
    }

    public void Shutdown()
    {
        _analytics?.Track("app_closed");
        _logger.Info("Application shutting down");

        _captureLoop?.Stop();
        _sessionManager?.OnShutdown();
        ReleaseCaptureResources();

        // Stop OBS
        _obsCaptureService?.Dispose();
        _obsCaptureService = null;
        _obsManager?.Dispose();
        _obsManager = null;

        _windowDetectionLoop?.Dispose();
        _analytics?.Dispose();
        _store?.Dispose();
        _notifications.Dispose();
        _logger.Info("Application shut down");
        _logger.Dispose();
    }

    private void ReleaseCaptureResources()
    {
        _captureLoop?.Dispose();
        _captureLoop = null;
        _ocrService = null;
    }

    public void Dispose()
    {
        Shutdown();
    }
}
