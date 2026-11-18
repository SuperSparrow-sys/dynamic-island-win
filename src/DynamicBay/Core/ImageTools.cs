using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicBay.Core;

internal static class ImageTools
{
    /// <summary>Decoded at <paramref name="decodeWidth"/> or <paramref name="decodeHeight"/> px (not both: keeps the aspect).</summary>
    public static BitmapImage? Load(Stream stream, int decodeWidth = 0, int decodeHeight = 0)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) img.DecodePixelWidth = decodeWidth;
            else if (decodeHeight > 0) img.DecodePixelHeight = decodeHeight;
            img.StreamSource = stream;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    /// <summary>
    /// What to decode a picture of this size at so its longer side is at most <paramref name="longSide"/> px: width or
    /// height (the other stays 0), or (0, 0) for a picture that is small already - decoding it larger would only cost memory.
    /// </summary>
    public static (int width, int height) DecodeSize(int pixelWidth, int pixelHeight, int longSide)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return (longSide, 0); // size unknown: by width, as before
        if (Math.Max(pixelWidth, pixelHeight) <= longSide) return (0, 0);
        return pixelHeight > pixelWidth ? (0, longSide) : (longSide, 0);
    }

    public static BitmapImage? Load(string path, int decodeWidth = 0, int decodeHeight = 0)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Load(fs, decodeWidth, decodeHeight);
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

    /// <summary>
    /// Frosted glass from a screen capture (BGRA, 32 bit): shrunk by <paramref name="factor"/> (averaged), then three box
    /// blurs of <paramref name="radius"/>, edges repeated so the border stays as bright as the middle. On the CPU once
    /// per opening: a WPF BlurEffect would run on every frame under software rendering.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) Frost(byte[] bgra, int width, int height, int factor, int radius)
    {
        int w = Math.Max(1, width / factor), h = Math.Max(1, height / factor);
        var px = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float sum = 0;
                    for (int dy = 0; dy < factor; dy++)
                        for (int dx = 0; dx < factor; dx++)
                            sum += bgra[(((y * factor + dy) * width) + x * factor + dx) * 4 + c];
                    px[(y * w + x) * 4 + c] = sum / (factor * factor);
                }
            }
        var tmp = new float[px.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlur(px, tmp, w, h, radius, horizontal: true);
            BoxBlur(tmp, px, w, h, radius, horizontal: false);
        }
        var result = new byte[px.Length];
        for (int i = 0; i < px.Length; i++) result[i] = (byte)Math.Clamp(px[i] + 0.5f, 0, 255);
        for (int i = 3; i < result.Length; i += 4) result[i] = 255; // the screen is opaque
        return (result, w, h);
    }

    private static void BoxBlur(float[] src, float[] dst, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        float k = 1f / (2 * r + 1);
        for (int line = 0; line < lines; line++)
        {
            int At(int i) => (horizontal ? line * w + Math.Clamp(i, 0, len - 1) : Math.Clamp(i, 0, len - 1) * w + line) * 4;
            for (int c = 0; c < 4; c++)
            {
                float sum = 0;
                for (int i = -r; i <= r; i++) sum += src[At(i) + c];
                for (int i = 0; i < len; i++)
                {
                    dst[At(i) + c] = sum * k;
                    sum += src[At(i + r + 1) + c] - src[At(i - r) + c];
                }
            }
        }
    }

    public static void SavePng(BitmapSource src, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
