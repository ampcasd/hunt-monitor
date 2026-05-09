using System.Threading;
using System.Windows;
using TibiaSquare.HuntMonitor.Infrastructure;
using TibiaSquare.HuntMonitor.Tray;

namespace TibiaSquare.HuntMonitor;

public partial class App : Application
{
    private const string MutexName = @"Global\TibiaSquareHuntMonitor";
    private Mutex? _singleInstanceMutex;
    private TrayIconManager? _trayIconManager;
    private AppLifecycleManager? _lifecycleManager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global error handlers — log before swallowing
        DispatcherUnhandledException += (_, args) =>
        {
            _lifecycleManager?.LogUnhandledException(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                _lifecycleManager?.LogUnhandledException(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            if (args.Exception != null)
                _lifecycleManager?.LogUnhandledException(args.Exception);
            args.SetObserved();
        };

        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Hunt Monitor is already running.",
                "Hunt Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _trayIconManager = new TrayIconManager(OnExitRequested);
        _trayIconManager.Initialize();

        _lifecycleManager = new AppLifecycleManager(_trayIconManager);
        _lifecycleManager.Start();
    }

    private void OnExitRequested()
    {
        // Dispose tray icon immediately so it vanishes from the taskbar
        _trayIconManager?.Dispose();
        _trayIconManager = null;

        // OnExit will handle heavy cleanup (OBS, SQLite, etc.)
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _lifecycleManager?.Shutdown();
        _trayIconManager?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
