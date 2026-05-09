using Windows.Graphics.Imaging;

namespace TibiaSquare.HuntMonitor.Private;

/// <summary>
/// Image preprocessing for OCR — contrast stretch, denoise, binarize, border crop.
/// Implemented by the Processing DLL.
/// </summary>
public interface IImagePreprocessor
{
    byte[] PreprocessForOcr(SoftwareBitmap bitmap, bool detectBottomBorder = true);
}
