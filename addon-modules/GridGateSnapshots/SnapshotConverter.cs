using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using CSJ2K;

[assembly: InternalsVisibleTo("GridGate.Snapshots.Tests")]

namespace GridGate.Robust.Snapshots
{
    /// <summary>
    /// JPEG 2000 textures (parcel snapshots) to JPEG for the launcher's Featured page (spec 2026-11-21-featured-places-design,
    /// "Pictures"), with the CSJ2K decoder and System.Drawing that Robust already ships.
    /// </summary>
    public static class SnapshotConverter
    {
        /// <summary>The largest texture taken: a 1024 x 1024 snapshot is well under it.</summary>
        public const int MaxBytes = 2 * 1024 * 1024;

        public const int MaxWidth = 512;

        /// <summary>The largest side taken: the biggest texture a viewer uploads.</summary>
        public const int MaxSide = 2048;

        private const long Quality = 85;

        // Decoding is heavy: at most two at once, so a burst of asks can't take Robust's whole CPU.
        private static readonly SemaphoreSlim Running = new SemaphoreSlim(2, 2);

        private static int _decodes;

        /// <summary>How many textures were handed to the decoder (tests check a refused one never is).</summary>
        internal static int Decodes => Volatile.Read(ref _decodes);

        /// <summary>
        /// The image size a raw JPEG 2000 codestream says it has (its SIZ marker: SOC <c>FF 4F</c>, SIZ <c>FF 51</c>, then
        /// Lsiz, Rsiz and the big-endian Xsiz, Ysiz, XOsiz, YOsiz), read before anything is decoded. Null for anything else,
        /// such as a JP2-wrapped file, which the decoder refuses anyway.
        /// </summary>
        public static (int Width, int Height)? SizeOf(byte[] j2k)
        {
            if (j2k is null || j2k.Length < 24 || j2k[0] != 0xFF || j2k[1] != 0x4F || j2k[2] != 0xFF || j2k[3] != 0x51) return null;
            long Read(int at) => ((long)j2k[at] << 24) | ((long)j2k[at + 1] << 16) | ((long)j2k[at + 2] << 8) | j2k[at + 3];
            long width = Read(8) - Read(16), height = Read(12) - Read(20);
            if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue) return null;
            return ((int)width, (int)height);
        }

        /// <summary>The texture as a JPEG at most <paramref name="maxWidth"/> wide (height kept in proportion), or null when it can't be read.</summary>
        public static byte[]? ToJpeg(byte[] j2k, int maxWidth = MaxWidth)
        {
            // A few hundred bytes can claim a 30000 x 30000 image: refused from the header, before the decoder allocates it.
            // Viewers make textures at most 2048 on a side.
            if (SizeOf(j2k) is not { } size || size.Width > MaxSide || size.Height > MaxSide) return null;
            try
            {
                Interlocked.Increment(ref _decodes);
                using var decoded = J2kImage.FromBytes(j2k);
                if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0) return null;
                var width = Math.Min(decoded.Width, maxWidth);
                var height = Math.Max(1, (int)Math.Round((double)width * decoded.Height / decoded.Width));
                using var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.Clear(Color.Black); // a texture with transparency gets a dark background, like the public pages
                    g.DrawImage(decoded, 0, 0, width, height);
                }
                using var output = new MemoryStream();
                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var options = new EncoderParameters(1);
                options.Param[0] = new EncoderParameter(Encoder.Quality, Quality);
                scaled.Save(output, codec, options);
                return output.ToArray();
            }
            catch (Exception)
            {
                return null; // not a texture CSJ2K can read
            }
        }

        /// <summary>
        /// The answer to <c>POST /gridgate/j2k</c>: 200 and the JPEG, 400 for anything that isn't a readable texture, 413 over
        /// <see cref="MaxBytes"/>, 503 when two conversions are already running.
        /// </summary>
        public static (int Status, byte[] Body) Answer(Stream body)
        {
            var bytes = ReadAtMost(body, MaxBytes + 1);
            if (bytes.Length > MaxBytes) return (413, Array.Empty<byte>());
            if (!Running.Wait(TimeSpan.FromSeconds(10))) return (503, Array.Empty<byte>());
            try
            {
                return ToJpeg(bytes) is { } jpeg ? (200, jpeg) : (400, Array.Empty<byte>());
            }
            finally
            {
                Running.Release();
            }
        }

        private static byte[] ReadAtMost(Stream body, int limit)
        {
            using var copy = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while (copy.Length < limit && (read = body.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - copy.Length))) > 0)
                copy.Write(buffer, 0, read);
            return copy.ToArray();
        }
    }
}
