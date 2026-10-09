using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicBay.Core;

internal static class ImageTools
{
    public static BitmapImage? Load(Stream stream, int decodeWidth = 0)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) img.DecodePixelWidth = decodeWidth;
            img.StreamSource = stream;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    public static BitmapImage? Load(string path, int decodeWidth = 0)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Load(fs, decodeWidth);
        }
        catch { return null; }
    }

    /// <summary>Picks a vivid, readable accent from artwork (like Apple Music's tinted controls).</summary>
    public static Color Accent(BitmapSource src, Color fallback)
    {
        try
        {
            var small = new TransformedBitmap(src, new ScaleTransform(24.0 / src.PixelWidth, 24.0 / src.PixelHeight));
            var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            conv.CopyPixels(px, stride, 0);

            double bestScore = -1;
            Color best = fallback;
            // Bucket hues so a single noisy pixel can't win.
            var buckets = new (double r, double g, double b, double weight)[12];
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                RgbToHsv(r, g, b, out double hue, out double sat, out double val);
                if (val < 0.25 || sat < 0.25) continue;
                int bi = (int)(hue / 30) % 12;
                double wgt = sat * val;
                buckets[bi].r += r * wgt; buckets[bi].g += g * wgt; buckets[bi].b += b * wgt; buckets[bi].weight += wgt;
            }
            foreach (var bk in buckets)
            {
                if (bk.weight <= 0) continue;
                double r = bk.r / bk.weight, g = bk.g / bk.weight, b = bk.b / bk.weight;
                RgbToHsv(r, g, b, out _, out double s, out double v);
                double score = bk.weight * (0.6 + s);
                if (score > bestScore)
                {
                    bestScore = score;
                    // Lift brightness so it reads on black.
                    v = Math.Max(v, 0.85);
                    s = Math.Min(s, 0.85);
                    HsvToRgb(Hue(r, g, b), s, v, out r, out g, out b);
                    best = Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
                }
            }
            return best;
        }
        catch { return fallback; }
    }

    private static double Hue(double r, double g, double b) { RgbToHsv(r, g, b, out var h, out _, out _); return h; }

    private static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max;
        s = max == 0 ? 0 : d / max;
        if (d == 0) h = 0;
        else if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }

    private static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        r += m; g += m; b += m;
    }

    public static void SavePng(BitmapSource src, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
