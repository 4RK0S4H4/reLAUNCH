using System.Runtime.InteropServices;
using ReLaunch.Core.Imaging;
using static ReLaunch.Agent.Native;

namespace ReLaunch.Agent;

internal static class IconExtractor
{
    private const int MaxPngBytes = 60_000;

    /// <summary>PNG of the app icon; falls back to smaller sizes when the file would be too large for the relay.</summary>
    public static byte[]? GetPng(string parsingName)
    {
        byte[]? last = null;
        foreach (var size in new[] { 256, 128, 64 })
        {
            last = Render(parsingName, size) ?? last;
            if (last is { Length: <= MaxPngBytes }) return last;
        }
        return last;
    }

    private static byte[]? Render(string parsingName, int size)
    {
        var iid = IidShellItemImageFactory;
        try
        {
            SHCreateItemFromParsingName("shell:AppsFolder\\" + parsingName, IntPtr.Zero, ref iid, out var factory);
            factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY, out var hbm);
            try { return BitmapToPng(hbm); }
            finally { DeleteObject(hbm); Marshal.ReleaseComObject(factory); }
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static byte[]? BitmapToPng(IntPtr hbm)
    {
        if (GetObjectW(hbm, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
        int w = bm.bmWidth, h = bm.bmHeight;
        if (w <= 0 || h <= 0) return null;

        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[w * h * 4];
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hbm, 0, (uint)h, pixels, ref header, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }

        return PngEncoder.EncodeRgba(w, h, PngEncoder.BgraPremultipliedToRgba(pixels));
    }
}
