using System.Drawing;
using System.Reflection;
using System.Windows;
using H.NotifyIcon;

namespace TibiaSquare.HuntMonitor.Tray;

public sealed class TrayIconManager : IDisposable
{
    private TaskbarIcon? _taskbarIcon;
    private TrayMenuWindow? _menuWindow;
    private readonly Action _onExitRequested;

    public bool IsDebugMode => _menuWindow?.IsDebugMode ?? false;

    public event Action? LoginRequested
    {
        add => _menuWindow!.LoginRequested += value;
        remove => _menuWindow!.LoginRequested -= value;
    }
    public event Action? LogoutRequested
    {
        add => _menuWindow!.LogoutRequested += value;
        remove => _menuWindow!.LogoutRequested -= value;
    }
    public event Action? SendReportRequested
    {
        add => _menuWindow!.SendReportRequested += value;
        remove => _menuWindow!.SendReportRequested -= value;
    }
#if DEBUG
    public event Action? EndHuntRequested
    {
        add => _menuWindow!.EndHuntRequested += value;
        remove => _menuWindow!.EndHuntRequested -= value;
    }
#endif
    public event Action? DebugOverlayRequested
    {
        add => _menuWindow!.DebugOverlayRequested += value;
        remove => _menuWindow!.DebugOverlayRequested -= value;
    }
    public event Action? SyncRequested
    {
        add => _menuWindow!.SyncRequested += value;
        remove => _menuWindow!.SyncRequested -= value;
    }
    public event Action? ExitRequested
    {
        add => _menuWindow!.ExitRequested += value;
        remove => _menuWindow!.ExitRequested -= value;
    }

    public TrayIconManager(Action onExitRequested)
    {
        _onExitRequested = onExitRequested;
    }

    public void Initialize()
    {
        _menuWindow = new TrayMenuWindow();

        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "Hunt Monitor",
            Icon = LoadIcon(),
        };

        _taskbarIcon.TrayLeftMouseUp += (_, _) => ToggleMenu();
        _taskbarIcon.TrayRightMouseUp += (_, _) => ToggleMenu();

        _taskbarIcon.ForceCreate();

        _menuWindow.ExitRequested += () => _onExitRequested();

        // Pass the tray icon's HWND to the menu window for positioning
        var hWnd = GetIconHWnd();
        if (hWnd != IntPtr.Zero)
            _menuWindow.SetTrayIconHandle(hWnd);
    }

    private IntPtr GetIconHWnd()
    {
        if (_taskbarIcon == null) return IntPtr.Zero;

        // TaskbarIcon._messageSink.HWnd — the hidden window that receives tray icon messages
        var sinkField = typeof(TaskbarIcon).GetField("_messageSink", BindingFlags.NonPublic | BindingFlags.Instance);
        if (sinkField?.GetValue(_taskbarIcon) is not { } sink) return IntPtr.Zero;

        var hWndProp = sink.GetType().GetProperty("HWnd", BindingFlags.Public | BindingFlags.Instance);
        return hWndProp?.GetValue(sink) is IntPtr hWnd ? hWnd : IntPtr.Zero;
    }

    private void ToggleMenu()
    {
        if (_menuWindow == null) return;

        if (_menuWindow.IsVisible)
            _menuWindow.Hide();
        else
            _menuWindow.Show();
    }

    public void UpdateTooltip(string text)
    {
        if (_taskbarIcon != null)
            _taskbarIcon.ToolTipText = text;
    }

    public void UpdateObsStatus(string status)
    {
        _menuWindow?.UpdateObsStatus(status);
    }

    public void UpdateAuthStatus(string? userName)
    {
        _menuWindow?.UpdateAuthStatus(userName);
    }

    public void UpdateReportAuth(bool isAuthenticated)
    {
        _menuWindow?.UpdateReportAuth(isAuthenticated);
    }

    public void UpdateReportSent()
    {
        _menuWindow?.UpdateReportSent();
    }

    public void SetHuntActive(bool active)
    {
        _menuWindow?.SetHuntActive(active);
    }

    public void UpdateSyncStatus(int unsyncedCount)
    {
        _menuWindow?.UpdateSyncStatus(unsyncedCount);
    }

    public void UpdateSyncInProgress(bool syncing)
    {
        _menuWindow?.UpdateSyncInProgress(syncing);
    }

    private static Icon LoadIcon()
    {
        var resourceUri = new Uri("pack://application:,,,/Assets/tray-icon.ico", UriKind.Absolute);
        var streamInfo = Application.GetResourceStream(resourceUri);
        if (streamInfo != null)
            return new Icon(streamInfo.Stream);
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _taskbarIcon?.Dispose();
        _taskbarIcon = null;
        _menuWindow?.Close();
        _menuWindow = null;
    }
}
