using Windows.Graphics.Imaging;

namespace TibiaSquare.HuntMonitor.Capture;

public interface ICaptureService : IDisposable
{
    bool IsCapturing { get; }
    void StartCapture(IntPtr hwnd);
    void StopCapture();
    Task<SoftwareBitmap?> TryGetFrameAsync();
}
