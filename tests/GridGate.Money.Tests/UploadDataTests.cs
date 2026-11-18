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

    private static byte[] Animation(ushort version = 1, ushort subVersion = 0, int priority = 2, float duration = 3.5f,
        string emote = "", float easeIn = 0.8f, uint joints = 0, int loop = 1, bool truncate = false)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(version));
        bytes.AddRange(BitConverter.GetBytes(subVersion));
        bytes.AddRange(BitConverter.GetBytes(priority));
        bytes.AddRange(BitConverter.GetBytes(duration));
        bytes.AddRange(Encoding.ASCII.GetBytes(emote));
        bytes.Add(0);
        bytes.AddRange(BitConverter.GetBytes(0f));
        bytes.AddRange(BitConverter.GetBytes(duration));
        bytes.AddRange(BitConverter.GetBytes(loop));
        bytes.AddRange(BitConverter.GetBytes(easeIn));
        bytes.AddRange(BitConverter.GetBytes(0.8f));
        bytes.AddRange(BitConverter.GetBytes(0u));
        bytes.AddRange(BitConverter.GetBytes(joints));
        if (truncate) bytes.RemoveRange(bytes.Count - 5, 5);
        return bytes.ToArray();
    }

    [Fact]
    public void An_animation_is_recognised_by_its_header() => Assert.True(UploadData.IsAnimation(Animation(emote: "wave", joints: 12)));

    [Fact]
    public void An_animation_with_no_joints_or_the_most_joints_is_still_one()
    {
        Assert.True(UploadData.IsAnimation(Animation(joints: 0)));
        Assert.True(UploadData.IsAnimation(Animation(joints: 216)));
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(1, 1)]
    public void An_animation_with_another_version_is_not_one(int version, int subVersion) =>
        Assert.False(UploadData.IsAnimation(Animation((ushort)version, (ushort)subVersion)));

    [Fact]
    public void Animation_data_out_of_range_is_not_one()
    {
        Assert.False(UploadData.IsAnimation(Animation(priority: -1)));
        Assert.False(UploadData.IsAnimation(Animation(priority: 99)));
        Assert.False(UploadData.IsAnimation(Animation(duration: 0f)));
        Assert.False(UploadData.IsAnimation(Animation(duration: 61f)));
        Assert.False(UploadData.IsAnimation(Animation(duration: float.NaN)));
        Assert.False(UploadData.IsAnimation(Animation(easeIn: float.PositiveInfinity)));
        Assert.False(UploadData.IsAnimation(Animation(joints: 217)));
        Assert.False(UploadData.IsAnimation(Animation(loop: 5)));
    }

    [Fact]
    public void Animation_data_that_ends_early_or_has_no_name_end_is_not_one()
    {
        Assert.False(UploadData.IsAnimation(Animation(truncate: true)));
        Assert.False(UploadData.IsAnimation(Animation(emote: new string('x', 70))));
        Assert.False(UploadData.IsAnimation(Animation()[..14]));
        Assert.False(UploadData.IsAnimation(null!));
        Assert.False(UploadData.IsAnimation([]));
    }

    [Fact]
    public void Random_data_and_text_are_not_animations()
    {
        var random = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var bytes = new byte[random.Next(0, 200)];
            random.NextBytes(bytes);
            Assert.False(UploadData.IsAnimation(bytes));
        }
        Assert.False(UploadData.IsAnimation(Encoding.ASCII.GetBytes("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 5\nhello}\n")));
    }

    [Fact]
    public void Fee_data_is_a_texture_a_sound_or_an_animation()
    {
        Assert.True(UploadData.IsFeeData(Animation()));
        Assert.True(UploadData.IsFeeData([0xFF, 0x4F, 0xFF, 0x51, 0, 0, 0, 0]));
        Assert.False(UploadData.IsFeeData(Encoding.ASCII.GetBytes("just some text for a note")));
    }

    [Fact]
    public void An_avatar_may_start_thirty_temporary_uploads_a_minute()
    {
        var limit = new TemporaryUploadLimit();
        var agent = OpenMetaverse.UUID.Random();
        for (var i = 0; i < 30; i++)
            Assert.True(limit.TryStart(agent, 1000 + i));
        Assert.False(limit.TryStart(agent, 5000));
        Assert.True(limit.TryStart(OpenMetaverse.UUID.Random(), 5000));
        Assert.True(limit.TryStart(agent, 1000 + 60_000));
    }
}
