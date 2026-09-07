using Windows.Graphics.Imaging;
using TibiaSquare.HuntMonitor.Ocr;

namespace TibiaSquare.HuntMonitor.Private;

public enum PanelLocateStatus
{
    NotAttempted,
    Found,
    NotFound,
    InvalidCrop,
}

/// <summary>
/// Locates a game panel in a full-screen frame, crops it, and returns OCR words.
/// Implemented by the Processing DLL.
/// </summary>
public interface IPanelLocator : IDisposable
{
    Task<(SoftwareBitmap Cropped, IReadOnlyList<OcrWordInfo> Words)?> LocateAsync(
        SoftwareBitmap fullFrame, OcrService ocrService);
    byte[]? LastPreprocessedPng { get; }
    string? LastRegionDebug { get; }
    PanelLocateStatus LastLocateStatus { get; }
    void InvalidateCache();
}
