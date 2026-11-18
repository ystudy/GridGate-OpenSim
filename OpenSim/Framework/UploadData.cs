using System;

namespace OpenSim.Framework
{
    /// <summary>
    /// GridGate: recognises texture and sound data by its first bytes, so an upload fee can't be dodged by sending a
    /// texture or a sound under a type that has no fee. Animations have no reliable marker and are not recognised.
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
            money is not null && (money.UploadCharge > 0 || money is IUploadFeeKnown { UploadFeeKnown: false }) && IsTextureOrSound(data);
    }
}
