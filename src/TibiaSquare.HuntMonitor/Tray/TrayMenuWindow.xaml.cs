using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TibiaSquare.HuntMonitor.Infrastructure;

namespace TibiaSquare.HuntMonitor.Tray;

public partial class TrayMenuWindow : Window
{
    private static readonly SolidColorBrush BgColor = new(Color.FromRgb(17, 17, 19));       // #111113
    private static readonly SolidColorBrush TextColor = new(Color.FromRgb(250, 250, 250));    // #fafafa
    private static readonly SolidColorBrush MutedColor = new(Color.FromRgb(161, 161, 170));   // #a1a1aa
    private static readonly SolidColorBrush GoldColor = new(Color.FromRgb(251, 191, 36));     // #fbbf24
    private static readonly SolidColorBrush HoverColor = new(Color.FromRgb(39, 39, 42));      // #27272a
    private static readonly SolidColorBrush SeparatorColor = new(Color.FromRgb(39, 39, 42));  // #27272a

    private readonly Typeface _segoeTypeface = new("Segoe UI");
    private readonly FontFamily _cinzelFont = _cinzelFontLazy.Value;

    private static readonly Lazy<FontFamily> _cinzelFontLazy = new(() =>
    {
        // Load from embedded resource stream
        var uri = new Uri("pack://application:,,,/TibiaSquare.HuntMonitor;component/Assets/Cinzel-Bold.ttf", UriKind.Absolute);
        var streamInfo = Application.GetResourceStream(uri);
        if (streamInfo != null)
        {
            // Write to temp file so WPF can load it as a font file
            var tempPath = Path.Combine(Path.GetTempPath(), "TibiaSquare-Cinzel-Bold.ttf");
            if (!File.Exists(tempPath))
            {
                using var fs = File.Create(tempPath);
                streamInfo.Stream.CopyTo(fs);
            }
            streamInfo.Stream.Dispose();
            return new FontFamily(new Uri(tempPath).AbsoluteUri + "#Cinzel");
        }
        return new FontFamily("Segoe UI");
    });

    private IntPtr _trayIconHWnd;
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;

    // ── Win32 API for tray icon positioning ──
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconRect);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private MenuItemControl? _obsStatusItem;
    private MenuItemControl? _authStatusItem;
    private MenuItemControl? _authActionItem;
    private MenuItemControl? _syncStatusItem;
    private MenuItemControl? _syncActionItem;
    private MenuItemControl? _startupItem;
    private MenuItemControl? _sendReportItem;
#if DEBUG
    private MenuItemControl? _endHuntItem;
#endif
    private MenuItemControl? _debugItem;
    private FrameworkElement? _debugSeparator;
    private FrameworkElement? _authSeparator;
    private FrameworkElement? _syncSeparator;
    private FrameworkElement? _reportSeparator;

    // Secret key sequence to toggle debug UI
    private static readonly Key[] DebugSequence = [Key.A, Key.S, Key.D, Key.F];
    private readonly List<Key> _keyBuffer = [];
    private bool _debugMode;
    private bool _syncInProgress;
    private int _lastUnsyncedCount;

    public bool IsDebugMode => _debugMode;

    public event Action? LoginRequested;
    public event Action? LogoutRequested;
    public event Action? SendReportRequested;
#if DEBUG
    public event Action? EndHuntRequested;
#endif
    public event Action? DebugOverlayRequested;
    public event Action? SyncRequested;
    public event Action? ExitRequested;

    public TrayMenuWindow()
    {
#if DEBUG
        _debugMode = true;
#endif
        InitializeComponent();
        BuildMenu();
        UpdateSeparators();
    }

    public void SetTrayIconHandle(IntPtr hWnd)
    {
        _trayIconHWnd = hWnd;
    }

    public new void Show()
    {
        _keyBuffer.Clear();
        PositionNearTrayIcon();
        base.Show();
        Activate();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        _dpiScaleX = source.CompositionTarget.TransformFromDevice.M11;
        _dpiScaleY = source.CompositionTarget.TransformFromDevice.M22;
    }

    private void PositionNearTrayIcon()
    {
        // Force layout so DesiredSize is populated on first call
        MenuPanel.Measure(new Size(Width, double.PositiveInfinity));

        double menuWidth = Width;
        double menuHeight = MenuPanel.DesiredSize.Height + 12;
        Height = menuHeight;
        var workArea = SystemParameters.WorkArea;

        // Try Shell_NotifyIconGetRect for exact icon position
        if (_trayIconHWnd != IntPtr.Zero)
        {
            var nid = new NOTIFYICONIDENTIFIER
            {
                cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
                hWnd = _trayIconHWnd,
                uID = 0,
                guidItem = Guid.Empty,
            };

            if (Shell_NotifyIconGetRect(ref nid, out RECT iconRect) == 0)
            {
                double iconCenterX = (iconRect.Left + iconRect.Right) / 2.0 * _dpiScaleX;
                double iconTop = iconRect.Top * _dpiScaleY;

                double left = iconCenterX - menuWidth / 2;
                double top = iconTop - menuHeight - 30;

                if (left < workArea.Left) left = workArea.Left + 4;
                if (left + menuWidth > workArea.Right) left = workArea.Right - menuWidth - 4;
                if (top < workArea.Top) top = workArea.Top + 4;

                Left = left;
                Top = top;
                return;
            }
        }

        // Fallback: cursor position (user clicked the tray icon)
        if (GetCursorPos(out POINT cursor))
        {
            double cursorX = cursor.X * _dpiScaleX;
            double cursorY = cursor.Y * _dpiScaleY;

            double left = cursorX - menuWidth / 2;
            double top = cursorY - menuHeight - 30;

            if (left < workArea.Left) left = workArea.Left + 4;
            if (left + menuWidth > workArea.Right) left = workArea.Right - menuWidth - 4;
            if (top < workArea.Top) top = workArea.Top + 4;

            Left = left;
            Top = top;
            return;
        }

        // Fallback: find the system tray area via Win32
        var trayRect = FindTrayAreaRect();
        if (trayRect.HasValue)
        {
            var r = trayRect.Value;
            double trayCenterX = (r.Left + r.Right) / 2.0 * _dpiScaleX;
            double trayTop = r.Top * _dpiScaleY;

            double left = trayCenterX - menuWidth / 2;
            double top = trayTop - menuHeight - 30;

            if (left < workArea.Left) left = workArea.Left + 4;
            if (left + menuWidth > workArea.Right) left = workArea.Right - menuWidth - 4;
            if (top < workArea.Top) top = workArea.Top + 4;

            Left = left;
            Top = top;
            return;
        }

        // Last resort: bottom-right of work area
        Left = workArea.Right - menuWidth - 8;
        Top = workArea.Bottom - menuHeight - 8;
    }

    private static RECT? FindTrayAreaRect()
    {
        // Shell_TrayWnd → TrayNotifyClass → SysPager → NotificationArea (main tray icons)
        var shellTray = FindWindow("Shell_TrayWnd", null);
        if (shellTray == IntPtr.Zero) return null;

        var trayNotify = FindWindowEx(shellTray, IntPtr.Zero, "TrayNotifyClass", null);
        if (trayNotify == IntPtr.Zero) return null;

        // Try the main notification area first
        var sysPager = FindWindowEx(trayNotify, IntPtr.Zero, "SysPager", null);
        if (sysPager != IntPtr.Zero)
        {
            var notifyArea = FindWindowEx(sysPager, IntPtr.Zero, "ToolbarWindow32", null);
            if (notifyArea != IntPtr.Zero && GetWindowRect(notifyArea, out RECT r) && r.Right > r.Left)
                return r;
        }

        // Fallback: use the TrayNotifyClass bounds (covers overflow arrow area too)
        if (GetWindowRect(trayNotify, out RECT trayRect) && trayRect.Right > trayRect.Left)
            return trayRect;

        return null;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Hide());
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            return;
        }

        _keyBuffer.Add(e.Key);
        if (_keyBuffer.Count > DebugSequence.Length)
            _keyBuffer.RemoveAt(0);

        if (_keyBuffer.Count == DebugSequence.Length && _keyBuffer.SequenceEqual(DebugSequence))
        {
            _debugMode = !_debugMode;
            _keyBuffer.Clear();
            UpdateDebugVisibility();
        }
    }

    private void UpdateDebugVisibility()
    {
        var vis = _debugMode ? Visibility.Visible : Visibility.Collapsed;
        if (_debugItem != null) _debugItem.Visibility = vis;
        if (_debugSeparator != null) _debugSeparator.Visibility = vis;
        UpdateSeparators();
    }

    private void UpdateSeparators()
    {
        // Auth separator: visible when sync items are visible (they appear after auth)
        bool hasSyncContent = _syncStatusItem?.Visibility == Visibility.Visible ||
                              _syncActionItem?.Visibility == Visibility.Visible;
        if (_authSeparator != null)
            _authSeparator.Visibility = hasSyncContent ? Visibility.Visible : Visibility.Collapsed;

        // Sync separator: visible when report is visible
        if (_syncSeparator != null)
            _syncSeparator.Visibility = _sendReportItem?.Visibility == Visibility.Visible
                ? Visibility.Visible : Visibility.Collapsed;

        // Report separator: visible when report is visible
        if (_reportSeparator != null)
            _reportSeparator.Visibility = _sendReportItem?.Visibility == Visibility.Visible
                ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Menu item helpers ──

    private MenuItemControl CreateMenuItem(string text, bool isStatus = false, Action? onClick = null)
    {
        var item = new MenuItemControl(text, isStatus, _segoeTypeface);
        if (onClick != null)
            item.Click += (_, _) =>
            {
                onClick();
                Hide();
            };
        return item;
    }

    private FrameworkElement CreateSeparator()
    {
        return new Border
        {
            Height = 1,
            Margin = new Thickness(12, 4, 12, 4),
            Background = SeparatorColor,
        };
    }

    // ── Build menu ──

    private void BuildMenu()
    {
        // Logo header
        var logo = new StackPanel
        {
            Margin = new Thickness(16, 10, 16, 8),
        };
        logo.Children.Add(new TextBlock
        {
            Text = "T I B I A",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = MutedColor,
        });
        logo.Children.Add(new TextBlock
        {
            Text = "Square",
            FontFamily = _cinzelFont,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = GoldColor,
            Margin = new Thickness(0, -4, 0, 0),
        });
        MenuPanel.Children.Add(logo);
        MenuPanel.Children.Add(CreateSeparator());

        // OBS status
        _obsStatusItem = CreateMenuItem("OBS: Initializing...", isStatus: true);
        MenuPanel.Children.Add(_obsStatusItem);
        MenuPanel.Children.Add(CreateSeparator());

#if DEBUG
        _endHuntItem = CreateMenuItem("End Hunt & Upload", onClick: () => EndHuntRequested?.Invoke());
        _endHuntItem.Visibility = Visibility.Collapsed;
        MenuPanel.Children.Add(_endHuntItem);
#endif

        // Debug items
        _debugItem = CreateMenuItem("Live OCR Debug", onClick: () => DebugOverlayRequested?.Invoke());
        _debugItem.Visibility = _debugMode ? Visibility.Visible : Visibility.Collapsed;
        MenuPanel.Children.Add(_debugItem);

        _debugSeparator = CreateSeparator();
        _debugSeparator.Visibility = _debugMode ? Visibility.Visible : Visibility.Collapsed;
        MenuPanel.Children.Add(_debugSeparator);

        // Auth
        _authStatusItem = CreateMenuItem("", isStatus: true);
        _authStatusItem.Visibility = Visibility.Collapsed;
        MenuPanel.Children.Add(_authStatusItem);

        _authActionItem = CreateMenuItem("Login with Discord");
        _authActionItem.Click += OnLoginClick;
        MenuPanel.Children.Add(_authActionItem);

        _authSeparator = CreateSeparator();
        MenuPanel.Children.Add(_authSeparator);

        // Sync
        _syncStatusItem = CreateMenuItem("", isStatus: true);
        _syncStatusItem.Visibility = Visibility.Collapsed;
        MenuPanel.Children.Add(_syncStatusItem);

        _syncActionItem = CreateMenuItem("Sync", onClick: () => SyncRequested?.Invoke());
        _syncActionItem.Visibility = Visibility.Collapsed;
        MenuPanel.Children.Add(_syncActionItem);

        _syncSeparator = CreateSeparator();
        MenuPanel.Children.Add(_syncSeparator);

        // Report
        _sendReportItem = CreateMenuItem("Send Diagnostic Report", onClick: () => SendReportRequested?.Invoke());
        _sendReportItem.Visibility = Visibility.Collapsed;
        MenuPanel.Children.Add(_sendReportItem);

        _reportSeparator = CreateSeparator();
        MenuPanel.Children.Add(_reportSeparator);

        // Startup
        _startupItem = CreateMenuItem("Start with Windows");
        _startupItem.IsCheckable = true;
        _ = InitStartupCheckboxAsync();
        _startupItem.Click += async (_, _) =>
        {
            _startupItem.IsChecked = !_startupItem.IsChecked;
            await StartupManager.SetEnabledAsync(_startupItem.IsChecked);
        };
        MenuPanel.Children.Add(_startupItem);
        MenuPanel.Children.Add(CreateSeparator());

        // Exit
        var exitItem = CreateMenuItem("Exit", onClick: () => ExitRequested?.Invoke());
        MenuPanel.Children.Add(exitItem);
    }

    private async Task InitStartupCheckboxAsync()
    {
        var enabled = await StartupManager.IsEnabledAsync();
        if (_startupItem != null)
            _startupItem.IsChecked = enabled;
    }

    // ── Dynamic update methods ──

    public void UpdateObsStatus(string status)
    {
        Dispatcher.Invoke(() =>
        {
            if (_obsStatusItem != null)
                _obsStatusItem.Text = $"OBS: {status}";
        });
    }

    public void UpdateAuthStatus(string? userName)
    {
        Dispatcher.Invoke(() =>
        {
            if (_authStatusItem == null || _authActionItem == null) return;

            if (userName != null)
            {
                _authStatusItem.Text = $"Logged in as: {userName}";
                _authStatusItem.Visibility = Visibility.Visible;
                _authActionItem.Text = "Logout";
                _authActionItem.Click -= OnLoginClick;
                _authActionItem.Click -= OnLogoutClick;
                _authActionItem.Click += OnLogoutClick;
            }
            else
            {
                _authStatusItem.Visibility = Visibility.Collapsed;
                _authActionItem.Text = "Login with Discord";
                _authActionItem.Click -= OnLoginClick;
                _authActionItem.Click -= OnLogoutClick;
                _authActionItem.Click += OnLoginClick;
            }
            UpdateSeparators();
        });
    }

    public void UpdateReportAuth(bool isAuthenticated)
    {
        Dispatcher.Invoke(() =>
        {
            if (_sendReportItem != null)
                _sendReportItem.Visibility = isAuthenticated ? Visibility.Visible : Visibility.Collapsed;
            UpdateSeparators();
        });
    }

    public void UpdateReportSent()
    {
        Dispatcher.Invoke(() =>
        {
            if (_sendReportItem != null)
            {
                _sendReportItem.Text = "Report sent ✓";
                _sendReportItem.IsEnabled = false;
            }
        });
    }

    public void SetHuntActive(bool active)
    {
#if DEBUG
        Dispatcher.Invoke(() =>
        {
            if (_endHuntItem != null)
                _endHuntItem.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        });
#endif
    }

    public void UpdateSyncStatus(int unsyncedCount)
    {
        Dispatcher.Invoke(() =>
        {
            if (_syncStatusItem == null || _syncActionItem == null) return;

            _lastUnsyncedCount = unsyncedCount;
            if (_syncInProgress) return;

            if (unsyncedCount == 0)
            {
                _syncStatusItem.Text = "All sessions synced";
                _syncActionItem.Visibility = Visibility.Collapsed;
            }
            else
            {
                _syncStatusItem.Text = $"{unsyncedCount} session{(unsyncedCount == 1 ? "" : "s")} unsynced";
                _syncActionItem.Visibility = Visibility.Visible;
            }
            _syncStatusItem.Visibility = Visibility.Visible;
            UpdateSeparators();
        });
    }

    public void UpdateSyncInProgress(bool syncing)
    {
        Dispatcher.Invoke(() =>
        {
            if (_syncStatusItem == null) return;

            _syncInProgress = syncing;
            if (syncing)
            {
                _syncStatusItem.Text = "Syncing...";
                _syncStatusItem.Visibility = Visibility.Visible;
                if (_syncActionItem != null)
                    _syncActionItem.Visibility = Visibility.Collapsed;
                UpdateSeparators();
            }
            else
            {
                UpdateSyncStatus(_lastUnsyncedCount);
            }
        });
    }

    private void OnLoginClick(object? sender, EventArgs e)
    {
        LoginRequested?.Invoke();
        Hide();
    }

    private void OnLogoutClick(object? sender, EventArgs e)
    {
        LogoutRequested?.Invoke();
        Hide();
    }

    // ── Custom menu item control ──

    public class MenuItemControl : UserControl
    {
        private readonly TextBlock _textBlock;
        private readonly Border _hoverBorder;
        private readonly UIElement _checkMark;
        private readonly bool _isStatus;

        public string Text
        {
            get => _textBlock.Text;
            set => _textBlock.Text = value;
        }

        private bool _isChecked;
        public bool IsCheckable { get; set; }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                _isChecked = value;
                _checkMark.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public event EventHandler? Click;

        public MenuItemControl(string text, bool isStatus, Typeface typeface)
        {
            _isStatus = isStatus;

            _hoverBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(4, 1, 4, 1),
                Padding = new Thickness(12, 6, 12, 6),
                Background = Brushes.Transparent,
            };

            var stack = new StackPanel { Orientation = Orientation.Horizontal };

            _checkMark = new TextBlock
            {
                Text = "✓",
                Foreground = GoldColor,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Visibility = Visibility.Collapsed,
            };
            stack.Children.Add(_checkMark);

            _textBlock = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                Foreground = isStatus ? MutedColor : TextColor,
                VerticalAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(_textBlock);

            _hoverBorder.Child = stack;
            Content = _hoverBorder;

            if (!isStatus)
            {
                MouseEnter += OnMouseEnter;
                MouseLeave += OnMouseLeave;
                Cursor = Cursors.Hand;
            }
        }

        private void OnMouseEnter(object sender, MouseEventArgs e)
        {
            _hoverBorder.Background = HoverColor;
            _textBlock.Foreground = GoldColor;
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            _hoverBorder.Background = Brushes.Transparent;
            _textBlock.Foreground = TextColor;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!_isStatus && IsEnabled)
                Click?.Invoke(this, EventArgs.Empty);
        }
    }
}
