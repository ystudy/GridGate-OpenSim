using System.Text;
using OpenSim.Framework;
using Xunit;

namespace GridGate.Money.Tests;

/// <summary>What <see cref="UploadData.IsTextureOrSound"/> recognises by the first bytes of an upload.</summary>
public sealed class UploadDataTests
{
    private static readonly byte[] Jp2Box = [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void A_jpeg2000_codestream_is_a_texture() =>
        Assert.True(UploadData.IsTextureOrSound(Join([0xFF, 0x4F, 0xFF, 0x51], new byte[60])));

    [Fact]
    public void A_jp2_file_is_a_texture() =>
        Assert.True(UploadData.IsTextureOrSound(Join(Jp2Box, new byte[60])));

    [Fact]
    public void An_ogg_vorbis_stream_is_a_sound() =>
        Assert.True(UploadData.IsTextureOrSound(Join(Encoding.ASCII.GetBytes("OggS"), new byte[24], [0x01], Encoding.ASCII.GetBytes("vorbis"), new byte[20])));

    [Fact]
    public void An_ogg_vorbis_stream_after_junk_is_still_a_sound() =>
        Assert.True(UploadData.IsTextureOrSound(Join(Enumerable.Repeat((byte)0x55, 20).ToArray(), Encoding.ASCII.GetBytes("OggS"),
            new byte[24], [0x01], Encoding.ASCII.GetBytes("vorbis"), new byte[20])));

    [Fact]
    public void A_notecard_that_mentions_OggS_is_not_a_sound() =>
        Assert.False(UploadData.IsTextureOrSound(Encoding.ASCII.GetBytes("Linden text version 2\n{ OggS }")));

    [Fact]
    public void A_notecard_is_neither() =>
        Assert.False(UploadData.IsTextureOrSound(Encoding.ASCII.GetBytes("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 5\nhello}\n")));

    [Fact]
    public void Empty_short_or_null_data_is_neither()
    {
        Assert.False(UploadData.IsTextureOrSound(null!));
        Assert.False(UploadData.IsTextureOrSound([]));
        Assert.False(UploadData.IsTextureOrSound([0xFF, 0x4F]));
    }
}
