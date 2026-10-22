using System.IO.Compression;
using ReLaunch.Core.Imaging;
using Xunit;

namespace ReLaunch.Core.Tests;

public class PngEncoderTests
{
    [Fact]
    public void ProducesValidPngWithExpectedPixels()
    {
        byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0, 1, 2, 3, 4];
        var png = PngEncoder.EncodeRgba(2, 2, rgba);

        Assert.Equal(new byte[] {0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A}, png[..8]);
        Assert.Equal(2, ReadBe(png, 16));
        Assert.Equal(2, ReadBe(png, 20));

        // IDAT starts after signature(8) + IHDR chunk(25) + length(4) + type(4).
        var idatLen = ReadBe(png, 33);
        var idat = png[41..(41 + idatLen)];
        using var z = new ZLibStream(new MemoryStream(idat), CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        var data = raw.ToArray();
        Assert.Equal(2 * (1 + 8), data.Length);
        Assert.Equal(0, data[0]);
        Assert.Equal(rgba[..8], data[1..9]);
        Assert.Equal(rgba[8..], data[10..18]);
    }

    [Fact]
    public void UnpremultiplyRestoresColour()
    {
        // 50% alpha, premultiplied blue channel 64 -> straight 127/128.
        var rgba = PngEncoder.BgraPremultipliedToRgba([64, 0, 0, 128]);
        Assert.Equal(128, rgba[3]);
        Assert.InRange(rgba[2], 126, 128);
    }

    [Fact]
    public void FullyTransparentBufferBecomesOpaque()
    {
        var rgba = PngEncoder.BgraPremultipliedToRgba([10, 20, 30, 0]);
        Assert.Equal(255, rgba[3]);
        Assert.Equal(30, rgba[0]);
    }

    private static int ReadBe(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
}
