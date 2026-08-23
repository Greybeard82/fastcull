using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Fastcull.Services;
using Windows.Storage.Streams;
using Xunit;

namespace Fastcull.Tests;

/// <summary>
/// PRD 3.3's 512 MB dimension guard. The failure mode it exists for is a stitched panorama
/// taking the app out with an OOM mid-cull.
/// </summary>
public class DimensionGuardTests
{
    [Fact]
    public void OrdinaryTierRequestsAreNeverLimited()
    {
        // The thumbnail, display and a fullscreen zoom must all pass through untouched, or the
        // guard would be silently degrading every photo rather than only the pathological ones.
        foreach (var edge in new uint[] { 160, 960, 2176, 3440 })
        {
            Assert.Equal(edge, DimensionGuard.ClampLongEdge(edge, 1.5));
            Assert.False(DimensionGuard.WouldLimit(edge, 1.5));
        }
    }

    [Fact]
    public void AnEnormousRequestIsCappedRatherThanRefused()
    {
        const uint absurd = 40_000;
        var capped = DimensionGuard.ClampLongEdge(absurd, 1.5);

        Assert.True(capped < absurd, "an oversized request must be reduced");
        Assert.True(capped > 0, "the guard must degrade the decode, not fail it");
        Assert.True(DimensionGuard.EstimateBytes(capped, 1.5) <= DimensionGuard.MaxDecodedBytes);
    }

    [Fact]
    public void TheCapLandsJustUnderTheCeilingRatherThanFarBelowIt()
    {
        // A guard that over-corrected would make large photos needlessly soft.
        var capped = DimensionGuard.ClampLongEdge(40_000, 1.5);
        var bytes = DimensionGuard.EstimateBytes(capped, 1.5);

        Assert.True(bytes <= DimensionGuard.MaxDecodedBytes);
        Assert.True(bytes > DimensionGuard.MaxDecodedBytes * 0.98,
            $"cap is wasteful: {bytes:N0} of a {DimensionGuard.MaxDecodedBytes:N0} budget");
    }

    [Theory]
    [InlineData(1.5)]        // 3:2 landscape
    [InlineData(2.0 / 3)]    // 3:2 portrait
    [InlineData(1.0)]        // square
    [InlineData(12.0)]       // stitched panorama - the case the PRD names
    [InlineData(1.0 / 12)]   // and its portrait twin
    public void TheCeilingHoldsAtEveryAspect(double aspect)
    {
        var capped = DimensionGuard.ClampLongEdge(100_000, aspect);
        Assert.True(DimensionGuard.EstimateBytes(capped, aspect) <= DimensionGuard.MaxDecodedBytes,
            $"aspect {aspect} produced {DimensionGuard.EstimateBytes(capped, aspect):N0} bytes");
    }

    [Fact]
    public void APanoramaIsCappedButStillUsablyLarge()
    {
        // 12:1 at 100,000px wide would be ~3.3 GB. Capped, it should still be several thousand
        // pixels on the long edge - degraded, not destroyed.
        var capped = DimensionGuard.ClampLongEdge(100_000, 12.0);
        Assert.InRange(capped, 10_000u, 60_000u);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-4.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnUndecodedAspectFallsBackInsteadOfProducingNonsense(double bad)
    {
        var capped = DimensionGuard.ClampLongEdge(3440, bad);
        Assert.Equal(3440u, capped);
    }

    [Fact]
    public void SourceDimensionOverloadAgreesWithTheAspectOverload()
    {
        Assert.Equal(
            DimensionGuard.ClampLongEdge(100_000, 6000 / 4000.0),
            DimensionGuard.ClampLongEdge(100_000, 6000, 4000));

        // A zero-sized source cannot be reasoned about; pass the request through untouched.
        Assert.Equal(3440u, DimensionGuard.ClampLongEdge(3440, 0, 0));
    }

    [Fact]
    public void ZeroRequestStaysZero()
        => Assert.Equal(0u, DimensionGuard.ClampLongEdge(0, 1.5));

    [Fact]
    public async Task RealDecodeOfAnOversizedImageStaysUnderTheCeiling()
    {
        // Generated at test time rather than committed as a fixture: a genuinely >512 MB image
        // would be an enormous binary in the repo. 6000x4000 is small enough to build quickly but
        // real enough to prove the guard runs inside the actual decode path.
        var png = BuildPng(6000, 4000);

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer()).AsTask();
        stream.Seek(0);

        // Ask for something absurd; the guard inside DecodeScaledFromStreamAsync must clamp it.
        using var bitmap = await ThumbnailService.DecodeScaledFromStreamAsync(stream, 100_000, CancellationToken.None);

        Assert.NotNull(bitmap);
        var bytes = (long)bitmap!.PixelWidth * bitmap.PixelHeight * 4;
        Assert.True(bytes <= DimensionGuard.MaxDecodedBytes,
            $"decoded {bitmap.PixelWidth}x{bitmap.PixelHeight} = {bytes:N0} bytes");

        // And it never upscales past the source.
        Assert.True(bitmap.PixelWidth <= 6000);
    }

    /// <summary>Minimal uncompressed-ish PNG built by hand so no image library is needed.</summary>
    private static byte[] BuildPng(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 2;    // truecolour RGB
        WriteChunk(ms, "IHDR", ihdr);

        // One filter byte plus 3 bytes per pixel per row, deflate-stored via zlib.
        var raw = new byte[(1 + width * 3) * height];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * (1 + width * 3);
            raw[rowStart] = 0;
            for (var x = 0; x < width; x++)
            {
                var p = rowStart + 1 + x * 3;
                raw[p] = (byte)(x % 251);
                raw[p + 1] = (byte)(y % 241);
                raw[p + 2] = (byte)((x ^ y) % 239);
            }
        }

        WriteChunk(ms, "IDAT", ZlibCompress(raw));
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var outMs = new MemoryStream();
        outMs.WriteByte(0x78);
        outMs.WriteByte(0x01);

        using (var deflate = new System.IO.Compression.DeflateStream(
                   outMs, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }

        uint a = 1, b = 0;
        foreach (var v in data) { a = (a + v) % 65521; b = (b + a) % 65521; }
        var adler = (b << 16) | a;
        outMs.Write(new[] { (byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler });

        return outMs.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBigEndian(len, 0, data.Length);
        s.Write(len);

        var typeBytes = type.Select(c => (byte)c).ToArray();
        s.Write(typeBytes);
        s.Write(data);

        var crc = Crc32(typeBytes.Concat(data).ToArray());
        s.Write(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc });
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }
}
