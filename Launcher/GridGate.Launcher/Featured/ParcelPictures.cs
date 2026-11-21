using GridGate.Launcher.Core.Plans;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Featured;

/// <summary>
/// Parcel snapshots as JPEGs for the Featured cards (spec 2026-11-21-featured-places-design, "Pictures"): the texture from
/// Robust's asset service (<c>GET /assets/{id}/data</c>, at most <see cref="MaxBytes"/>), converted by GridGate's Robust
/// add-on (<c>POST /gridgate/j2k</c>), both on Robust's private port. A snapshot never changes under the same id, so each is
/// kept for <see cref="Lifetime"/> (a failure for <see cref="FailureLifetime"/>), at most <see cref="MaxKept"/>; visitors who
/// ask at once share one fetch. Only the Featured picture route asks, and only for a place the asker may see.
/// </summary>
public sealed class ParcelPictures(IHttpClientFactory http, Func<LauncherSettings> settings, TimeProvider time)
{
    public const string ClientName = "GridGate.ParcelPictures";
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxKept = 200;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(5);

    private sealed record Kept(Task<byte[]> Jpeg, DateTimeOffset Asked);

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Kept> _kept = [];

    /// <summary>The snapshot as a JPEG; empty when there is none, it can't be fetched, or Robust can't convert it.</summary>
    public Task<byte[]> JpegAsync(Guid snapshot, CancellationToken ct)
    {
        if (snapshot == Guid.Empty) return Task.FromResult(Array.Empty<byte>());
        var now = time.GetUtcNow();
        Kept kept;
        lock (_gate)
        {
            if (!_kept.TryGetValue(snapshot, out kept!) || !Fresh(kept, now))
            {
                // Not tied to this visitor's request: one who leaves mustn't cancel the fetch the others wait on. The
                // client's own timeout ends it.
                kept = new Kept(FetchAsync(snapshot), now);
                _kept[snapshot] = kept;
                Trim(now);
            }
        }
        return kept.Jpeg.WaitAsync(ct);
    }

    private bool Fresh(Kept kept, DateTimeOffset now)
    {
        if (!kept.Jpeg.IsCompleted) return true; // still being fetched: share it
        var failed = !kept.Jpeg.IsCompletedSuccessfully || kept.Jpeg.Result.Length == 0;
        return now < kept.Asked + (failed ? FailureLifetime : Lifetime);
    }

    private void Trim(DateTimeOffset now)
    {
        if (_kept.Count <= MaxKept) return;
        foreach (var (id, old) in _kept.ToList())
            if (!Fresh(old, now)) _kept.Remove(id);
        foreach (var (id, _) in _kept.OrderBy(k => k.Value.Asked).Take(_kept.Count - MaxKept).ToList())
            _kept.Remove(id);
    }

    private async Task<byte[]> FetchAsync(Guid snapshot)
    {
        try
        {
            using var client = http.CreateClient(ClientName);
            var robust = GridEndpoints.PrivateBase(settings());
            byte[] texture;
            using (var asset = await client.GetAsync(new Uri(robust, $"/assets/{snapshot}/data"), HttpCompletionOption.ResponseHeadersRead))
            {
                if (!asset.IsSuccessStatusCode || asset.Content.Headers.ContentLength > MaxBytes) return [];
                texture = await ReadAtMostAsync(asset.Content, MaxBytes + 1);
                if (texture.Length is 0 or > MaxBytes) return [];
            }
            using var body = new ByteArrayContent(texture);
            body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var converted = await client.PostAsync(new Uri(robust, "/gridgate/j2k"), body);
            if (!converted.IsSuccessStatusCode || converted.Content.Headers.ContentType?.MediaType != "image/jpeg") return [];
            return await converted.Content.ReadAsByteArrayAsync();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or UriFormatException)
        {
            return []; // Robust is down, slow, or has no converter: the card shows the map tile
        }
    }

    private static async Task<byte[]> ReadAtMostAsync(HttpContent content, int limit)
    {
        await using var stream = await content.ReadAsStreamAsync();
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while (copy.Length < limit && (read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - copy.Length)))) > 0)
            copy.Write(buffer, 0, read);
        return copy.ToArray();
    }
}
