using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TibiaSquare.HuntMonitor.Screenshots;

public static class JpegEncoder
{
    /// <summary>
    /// Encodes a SoftwareBitmap (Bgra8) to JPEG bytes.
    /// Quality range: 0.0 (worst) to 1.0 (best). Default 80 → ~200-400KB at 1080p.
    /// </summary>
    public static async Task<byte[]> EncodeAsync(SoftwareBitmap bitmap, int quality = 80)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);

        var propertySet = new BitmapPropertySet();
        var qualityValue = new BitmapTypedValue((double)quality / 100.0, PropertyType.Single);
        propertySet.Add("ImageQuality", qualityValue);
        await encoder.BitmapProperties.SetPropertiesAsync(propertySet);

        await encoder.FlushAsync();

        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
