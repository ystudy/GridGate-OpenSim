using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using CSJ2K;

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

        private const long Quality = 85;

        // Decoding is heavy: at most two at once, so a burst of asks can't take Robust's whole CPU.
        private static readonly SemaphoreSlim Running = new SemaphoreSlim(2, 2);

        /// <summary>The texture as a JPEG at most <paramref name="maxWidth"/> wide (height kept in proportion), or null when it can't be read.</summary>
        public static byte[]? ToJpeg(byte[] j2k, int maxWidth = MaxWidth)
        {
            if (j2k is null || j2k.Length == 0) return null;
            try
            {
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
