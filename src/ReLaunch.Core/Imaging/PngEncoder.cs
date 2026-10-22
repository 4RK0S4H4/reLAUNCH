using System.IO.Compression;

namespace ReLaunch.Core.Imaging;

/// <summary>Minimal PNG writer (RGBA, 8 bit) so icons need no imaging dependency.</summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Converts top-down premultiplied BGRA (as returned by the shell) to straight RGBA.</summary>
    public static byte[] BgraPremultipliedToRgba(ReadOnlySpan<byte> bgra)
    {
        var rgba = new byte[bgra.Length];
        var anyAlpha = false;
        for (var i = 0; i < bgra.Length; i += 4)
            if (bgra[i + 3] != 0) { anyAlpha = true; break; }

        for (var i = 0; i < bgra.Length; i += 4)
        {
            int a = anyAlpha ? bgra[i + 3] : 255;
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            if (anyAlpha && a > 0 && a < 255)
            {
                r = Math.Min(255, r * 255 / a);
                g = Math.Min(255, g * 255 / a);
                b = Math.Min(255, b * 255 / a);
            }
            rgba[i] = (byte)r; rgba[i + 1] = (byte)g; rgba[i + 2] = (byte)b; rgba[i + 3] = (byte)a;
        }
        return rgba;
    }

    public static byte[] EncodeRgba(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("Pixel buffer does not match dimensions.");

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var stride = width * 4;
            for (var y = 0; y < height; y++)
            {
                z.WriteByte(0); // filter: none
                z.Write(rgba.Slice(y * stride, stride));
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBe(ihdr, 0, (uint)width);
        WriteBe(ihdr, 4, (uint)height);
        ihdr[8] = 8; ihdr[9] = 6; // 8 bit, RGBA
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", raw.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBe(len, 0, (uint)data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        var crcBytes = new byte[4];
        WriteBe(crcBytes, 0, crc ^ 0xFFFFFFFFu);
        s.Write(crcBytes);
    }

    private static void WriteBe(byte[] buf, int offset, uint v)
    {
        buf[offset] = (byte)(v >> 24); buf[offset + 1] = (byte)(v >> 16);
        buf[offset + 2] = (byte)(v >> 8); buf[offset + 3] = (byte)v;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
