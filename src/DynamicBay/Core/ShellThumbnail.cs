using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicBay.Core;

/// <summary>Explorer-quality thumbnails/icons for any file via IShellItemImageFactory.</summary>
internal static class ShellThumbnail
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    private const int SIIGBF_RESIZETOFIT = 0x0, SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    public static ImageSource? Get(string path, int size = 96, bool iconOnly = false)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            int hr = factory.GetImage(new SIZE { cx = size, cy = size },
                SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK | (iconOnly ? SIIGBF_ICONONLY : 0), out var hbmp);
            Marshal.ReleaseComObject(factory);
            if (hr != 0 || hbmp == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                // Shell bitmaps carry premultiplied alpha in a 32bpp DIB; re-wrap so transparency survives.
                var fixedSrc = FixAlpha(src);
                fixedSrc.Freeze();
                return fixedSrc;
            }
            finally { DeleteObject(hbmp); }
        }
        catch { return null; }
    }

    private static BitmapSource FixAlpha(BitmapSource src)
    {
        if (src.Format.BitsPerPixel != 32) return src;
        int stride = src.PixelWidth * 4;
        var px = new byte[stride * src.PixelHeight];
        src.CopyPixels(px, stride, 0);
        bool anyAlpha = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
        var fmt = anyAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;
        return BitmapSource.Create(src.PixelWidth, src.PixelHeight, 96, 96, fmt, null, px, stride);
    }
}
