using System.Drawing;
using GridGate.Robust.Snapshots;
using Xunit;

namespace GridGate.Snapshots.Tests;

/// <summary>
/// The Robust converter (spec 2026-11-21-featured-places-design, "Pictures"): JPEG 2000 in, a JPEG at most 512 px wide out,
/// and the refusals the launcher relies on.
/// </summary>
public sealed class SnapshotConverterTests
{
    private static byte[] Texture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "texture.j2c"));

    [Fact]
    public void Converts_a_real_texture()
    {
        var j2k = Texture();
        using var original = CSJ2K.J2kImage.FromBytes(j2k);

        var jpeg = SnapshotConverter.ToJpeg(j2k, maxWidth: 64);

        Assert.NotNull(jpeg);
        Assert.Equal([0xFF, 0xD8], jpeg[..2]);
        using var image = Image.FromStream(new MemoryStream(jpeg));
        Assert.Equal(64, image.Width);
        Assert.Equal((int)Math.Round(64.0 * original.Height / original.Width), image.Height);
    }

    [Fact]
    public void A_small_texture_keeps_its_size()
    {
        var j2k = Texture();
        using var original = CSJ2K.J2kImage.FromBytes(j2k);

        using var image = Image.FromStream(new MemoryStream(SnapshotConverter.ToJpeg(j2k, maxWidth: 4096)!));

        Assert.Equal(original.Width, image.Width);
    }

    [Fact]
    public void Junk_is_refused()
    {
        Assert.Null(SnapshotConverter.ToJpeg([1, 2, 3, 4, 5]));
        Assert.Equal(400, SnapshotConverter.Answer(new MemoryStream([1, 2, 3])).Status);
        Assert.Equal(400, SnapshotConverter.Answer(new MemoryStream([])).Status);
    }

    [Fact]
    public void A_real_texture_is_answered()
    {
        var (status, body) = SnapshotConverter.Answer(new MemoryStream(Texture()));

        Assert.Equal(200, status);
        Assert.Equal([0xFF, 0xD8], body[..2]);
    }

    /// <summary>A JPEG 2000 codestream header (SOC, SIZ) claiming <paramref name="width"/> x <paramref name="height"/>, then junk.</summary>
    private static byte[] Claiming(uint width, uint height)
    {
        var b = new byte[64];
        b[0] = 0xFF; b[1] = 0x4F; b[2] = 0xFF; b[3] = 0x51; b[4] = 0; b[5] = 41;
        void Put(int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }
        Put(8, width); Put(12, height); // Xsiz, Ysiz; XOsiz and YOsiz stay 0
        return b;
    }

    [Theory]
    [InlineData(30000u, 30000u)]
    [InlineData(1024u, 1000000u)]
    [InlineData(4096u, 16u)]
    public void A_huge_claimed_size_is_refused_before_decoding(uint width, uint height)
    {
        var decodes = SnapshotConverter.Decodes;

        Assert.Null(SnapshotConverter.ToJpeg(Claiming(width, height)));
        Assert.Equal(400, SnapshotConverter.Answer(new MemoryStream(Claiming(width, height))).Status);
        Assert.Equal(decodes, SnapshotConverter.Decodes); // refused from the header, never handed to the decoder
    }

    [Fact]
    public void The_texture_size_is_read_from_its_header()
    {
        using var original = CSJ2K.J2kImage.FromBytes(Texture());

        Assert.Equal((original.Width, original.Height), SnapshotConverter.SizeOf(Texture()));
        Assert.Equal((30000, 20), SnapshotConverter.SizeOf(Claiming(30000, 20)));
        Assert.Null(SnapshotConverter.SizeOf([0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20])); // a JP2 wrapper, not a codestream
        Assert.Null(SnapshotConverter.SizeOf([0xFF, 0x4F, 0xFF, 0x51]));                         // cut short
    }

    [Fact]
    public void Over_2_MB_is_413()
    {
        var big = new byte[SnapshotConverter.MaxBytes + 1];

        Assert.Equal(413, SnapshotConverter.Answer(new MemoryStream(big)).Status);
    }
}
