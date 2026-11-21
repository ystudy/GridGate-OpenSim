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

    [Fact]
    public void Over_2_MB_is_413()
    {
        var big = new byte[SnapshotConverter.MaxBytes + 1];

        Assert.Equal(413, SnapshotConverter.Answer(new MemoryStream(big)).Status);
    }
}
