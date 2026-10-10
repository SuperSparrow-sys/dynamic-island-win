using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace DynamicBay.Services;

/// <summary>
/// Reads the text in a picture with the OCR built into Windows (offline, in the user's languages, e.g. German and English).
/// </summary>
public static class TextRecognition
{
    /// <summary>The recognised text, line by line ("" if there is none or OCR isn't available).</summary>
    public static async Task<string> ReadAsync(string imagePath)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        if (engine is null) return "";
        var file = await StorageFile.GetFileFromPathAsync(imagePath);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        // OCR handles at most MaxImageDimension pixels per side: scale big screenshots down.
        uint max = OcrEngine.MaxImageDimension;
        var transform = new BitmapTransform();
        if (decoder.PixelWidth > max || decoder.PixelHeight > max)
        {
            double k = Math.Min((double)max / decoder.PixelWidth, (double)max / decoder.PixelHeight);
            transform.ScaledWidth = (uint)(decoder.PixelWidth * k);
            transform.ScaledHeight = (uint)(decoder.PixelHeight * k);
        }
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text)).Trim();
    }
}
