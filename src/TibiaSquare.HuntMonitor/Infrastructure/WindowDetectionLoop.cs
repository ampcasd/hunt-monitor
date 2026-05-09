using TibiaSquare.HuntMonitor.Capture;

namespace TibiaSquare.HuntMonitor.Infrastructure;

public sealed class WindowDetectionLoop : IDisposable
{
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    private bool _tibiaRunning;
    private TibiaWindowInfo? _currentWindow;

    public TibiaWindowInfo? CurrentWindow => _currentWindow;

    /// <summary>Any Tibia window appeared (login screen or logged in).</summary>
    public event Action? TibiaOpened;

    /// <summary>All Tibia windows gone.</summary>
    public event Action? TibiaClosed;

    /// <summary>Character logged in (window title has character name).</summary>
    public event Action<TibiaWindowInfo>? TibiaDetected;

    /// <summary>Character lost (logged out to login screen, or window closed).</summary>
    public event Action? TibiaLost;

    public WindowDetectionLoop()
    {
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
    }

    public void Start()
    {
        _loopTask = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Do an immediate first check
        CheckForTibia();

        while (await _timer.WaitForNextTickAsync(ct))
        {
            CheckForTibia();
        }
    }

    private void CheckForTibia()
    {
        var found = TibiaWindowDetector.FindFirst();

        if (found != null)
        {
            // Tibia is running
            if (!_tibiaRunning)
            {
                _tibiaRunning = true;
                TibiaOpened?.Invoke();
            }

            if (found.CharacterName != null)
            {
                // Character is logged in
                if (_currentWindow == null)
                {
                    _currentWindow = found;
                    TibiaDetected?.Invoke(found);
                }
                else if (found.Hwnd != _currentWindow.Hwnd || found.CharacterName != _currentWindow.CharacterName)
                {
                    // Different window or character — lost + re-detected
                    TibiaLost?.Invoke();
                    _currentWindow = found;
                    TibiaDetected?.Invoke(found);
                }
            }
            else
            {
                // Login screen — no character
                if (_currentWindow != null)
                {
                    _currentWindow = null;
                    TibiaLost?.Invoke();
                }
            }
        }
        else
        {
            // No Tibia window at all
            if (_currentWindow != null)
            {
                _currentWindow = null;
                TibiaLost?.Invoke();
            }
            if (_tibiaRunning)
            {
                _tibiaRunning = false;
                TibiaClosed?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        _cts.Dispose();
    }
}
