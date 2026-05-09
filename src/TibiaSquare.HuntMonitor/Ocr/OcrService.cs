using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace TibiaSquare.HuntMonitor.Ocr;

public sealed class OcrService
{
    private readonly OcrEngine _engine;

    public OcrService()
    {
        _engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
                  ?? OcrEngine.TryCreateFromUserProfileLanguages()
                  ?? throw new InvalidOperationException(
                      "No OCR engine available. Ensure English language pack is installed.");
    }

    public async Task<OcrResult> RecognizeAsync(SoftwareBitmap bitmap)
    {
        // OcrEngine requires Gray8 or Bgra8
        SoftwareBitmap ocrBitmap;
        if (bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8)
        {
            ocrBitmap = bitmap;
        }
        else
        {
            ocrBitmap = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        return await _engine.RecognizeAsync(ocrBitmap);
    }
}
