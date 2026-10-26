using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PcMonitor.Widget.Native;

/// <summary>Extracts a file's icon exactly as Explorer shows it (including UWP/overlay icons) and stores it as PNG.</summary>
internal static class ShellIcons
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, uint flags, out IntPtr hbitmap);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public short bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[]? bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    private const uint SIIGBF_ICONONLY = 0x4;
    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    /// <summary>Writes a square PNG icon; returns false if the shell could not produce one. Call from an STA thread.</summary>
    public static bool TrySavePng(string filePath, string pngPath, int size)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = IID_IShellItemImageFactory;
            if (SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref iid, out var factory) != 0) return false;
            try
            {
                if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY, out hbm) != 0 || hbm == IntPtr.Zero) return false;
            }
            finally { Marshal.ReleaseComObject(factory); }

            GetObjectW(hbm, Marshal.SizeOf<BITMAP>(), out var bm);
            int w = bm.bmWidth, h = bm.bmHeight;
            if (w <= 0 || h <= 0) return false;

            var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var pixels = new byte[w * h * 4];
            IntPtr dc = GetDC(IntPtr.Zero);
            try { if (GetDIBits(dc, hbm, 0, (uint)h, pixels, ref bmi, 0) == 0) return false; }
            finally { ReleaseDC(IntPtr.Zero, dc); }

            // Some icons come back without an alpha channel: treat them as opaque instead of invisible.
            bool anyAlpha = false;
            for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                using var bitmap = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, handle.AddrOfPinnedObject());
                Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
                string tmp = pngPath + ".tmp";
                bitmap.Save(tmp, ImageFormat.Png);
                File.Move(tmp, pngPath, overwrite: true);
            }
            finally { handle.Free(); }
            return true;
        }
        catch { return false; }
        finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }
    }
}
