using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;

namespace OpenSim.Framework
{
    /// <summary>
    /// GridGate: recognises texture and sound data by its first bytes, so an upload fee can't be dodged by sending a
    /// texture, a sound or an animation under a type that has no fee. An animation is recognised by its header, which is
    /// stricter than a few marker bytes: see <see cref="IsAnimation"/>.
    /// </summary>
    public static class UploadData
    {
        private static readonly byte[] J2cCodestream = { 0xFF, 0x4F, 0xFF, 0x51 };
        private static readonly byte[] Jp2Signature = { 0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A };
        private static readonly byte[] OggPage = { 0x4F, 0x67, 0x67, 0x53 }; // "OggS"
        private static readonly byte[] VorbisIdentification = { 0x01, 0x76, 0x6F, 0x72, 0x62, 0x69, 0x73 }; // "\x01vorbis"

        /// <summary>
        /// True for a JPEG 2000 codestream or JP2 file (a texture), or an Ogg stream with a Vorbis identification header
        /// (a sound). Players skip junk before the first Ogg page, so the Vorbis check looks anywhere in the data. False
        /// for null, short data or anything else.
        /// </summary>
        public static bool IsTextureOrSound(byte[] data)
        {
            if (data is null || data.Length < J2cCodestream.Length)
                return false;

            ReadOnlySpan<byte> span = data;
            if (span.StartsWith(J2cCodestream) || span.StartsWith(Jp2Signature))
                return true;

            return span.IndexOf(OggPage) >= 0 && span.IndexOf(VorbisIdentification) >= 0;
        }

        private const int MaxAnimationJoints = 216;
        private const int MaxAnimationNameBytes = 64;
        private const float MaxAnimationSeconds = 60f;

        /// <summary>
        /// True for Second Life animation data: version 1, sub-version 0, a base priority of 0 to 7, a finite duration over 0
        /// and up to 60 seconds, a NUL-terminated emote name (at most 64 bytes), the loop points and ease times as finite
        /// numbers, a loop flag of 0 or 1, a hand pose and a joint count of at most 216. Data that ends before all of that
        /// has been read is not an animation.
        /// </summary>
        public static bool IsAnimation(byte[] data)
        {
            if (data is null || data.Length < 16)
                return false;

            ReadOnlySpan<byte> span = data;
            if (BinaryPrimitives.ReadUInt16LittleEndian(span) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(span[2..]) != 0)
                return false;

            int priority = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
            if (priority < 0 || priority > 7)
                return false;

            float duration = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span[8..]));
            if (!float.IsFinite(duration) || duration <= 0f || duration > MaxAnimationSeconds)
                return false;

            ReadOnlySpan<byte> rest = span[12..];
            int nul = rest.IndexOf((byte)0);
            if (nul < 0 || nul > MaxAnimationNameBytes)
                return false;

            rest = rest[(nul + 1)..];
            // loop in, loop out, loop, ease in, ease out, hand pose, joint count
            if (rest.Length < 28)
                return false;

            for (int offset = 0; offset < 20; offset += 4)
            {
                if (offset == 8)
                    continue;
                if (!float.IsFinite(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(rest[offset..]))))
                    return false;
            }

            int loop = BinaryPrimitives.ReadInt32LittleEndian(rest[8..]);
            if (loop != 0 && loop != 1)
                return false;

            return BinaryPrimitives.ReadUInt32LittleEndian(rest[24..]) <= MaxAnimationJoints;
        }

        /// <summary>
        /// True for data an upload fee applies to whatever type it was declared as: a texture, a sound or an animation.
        /// </summary>
        public static bool IsFeeData(byte[] data) => IsTextureOrSound(data) || IsAnimation(data);

        /// <summary>Shown when an item update is refused because its data is a texture or a sound.</summary>
        public const string ItemUpdateRefusedText = "That upload isn't the kind of item it was saved to, so it wasn't saved.";

        /// <summary>
        /// True when an update of an existing inventory item (agent or task) must be refused: an upload fee is in force
        /// and the data is a texture or a sound. Those are made by uploads, which pay the fee; updating a notecard or
        /// script with them would store a free texture or sound. Also true while the money module doesn't know the fee yet
        /// (<see cref="IUploadFeeKnown"/>): it would read as no fee.
        /// </summary>
        public static bool RefusesItemUpdate(IMoneyModule money, byte[] data) =>
            // GridGate: fail closed until the fee is known
            money is not null && (money.UploadCharge > 0 || money is IUploadFeeKnown { UploadFeeKnown: false }) && IsFeeData(data);
    }

    /// <summary>
    /// GridGate: temporary uploads are free (viewer bakes use them), so each avatar may start only a few a minute in a
    /// region. One bake sends at most 11 baked textures (OpenMetaverse's BakeType has 11 entries), so the limit leaves
    /// room for more than two full bakes a minute.
    /// </summary>
    public sealed class TemporaryUploadLimit
    {
        public const int PerMinute = 30;
        private const long WindowMs = 60_000;
        private const int SweepAbove = 256;

        private readonly Dictionary<UUID, Queue<long>> m_starts = new();

        /// <summary>
        /// Counts a temporary upload started by <paramref name="agent"/> at <paramref name="nowMs"/> (milliseconds on any
        /// steady clock). False, and not counted, when the avatar already started <see cref="PerMinute"/> in the last minute.
        /// </summary>
        public bool TryStart(UUID agent, long nowMs)
        {
            lock (m_starts)
            {
                if (m_starts.Count > SweepAbove)
                {
                    foreach (UUID id in m_starts.Where(kv => kv.Value.Count == 0 || nowMs - kv.Value.Peek() >= WindowMs).Select(kv => kv.Key).ToList())
                        m_starts.Remove(id);
                }

                if (!m_starts.TryGetValue(agent, out Queue<long> starts))
                    m_starts[agent] = starts = new Queue<long>();

                while (starts.Count > 0 && nowMs - starts.Peek() >= WindowMs)
                    starts.Dequeue();

                if (starts.Count >= PerMinute)
                    return false;

                starts.Enqueue(nowMs);
                return true;
            }
        }
    }
}
