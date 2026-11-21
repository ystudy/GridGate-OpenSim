using System.Net;
using System.Net.Http.Headers;
using GridGate.Launcher.Core.Plans;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Featured;

/// <summary>
/// Parcel snapshots as JPEGs for the Featured cards (spec 2026-11-21-featured-places-design, "Pictures"): the texture from
/// Robust's asset service (<c>GET /assets/{id}/data</c>, at most <see cref="MaxBytes"/>), converted by GridGate's Robust
/// add-on (<c>POST /gridgate/j2k</c>), both on Robust's private port. A snapshot never changes under the same id, so each is
/// kept for <see cref="Lifetime"/> (a failure for <see cref="FailureLifetime"/>; a busy converter is asked again at once), at
/// most <see cref="MaxKept"/>; visitors who ask at once share one fetch. A whole fetch gives up after <see cref="FetchTimeout"/>,
/// and at most two conversions are sent at once, so a page of 24 cards queues here rather than inside Robust. Only the
/// Featured picture route asks, and only for a place the asker may see.
/// </summary>
public sealed class ParcelPictures(IHttpClientFactory http, Func<LauncherSettings> settings, TimeProvider time)
{
    public const string ClientName = "GridGate.ParcelPictures";
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxKept = 200;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    /// <param name="Busy">Robust's converter was busy (503): not kept, so the next ask tries again.</param>
    private sealed record Fetched(byte[] Jpeg, bool Busy = false);

    private sealed record Kept(Task<Fetched> Result, DateTimeOffset Asked);

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Kept> _kept = [];
    private readonly SemaphoreSlim _converting = new(2, 2);

    /// <summary>The snapshot as a JPEG; empty when there is none, it can't be fetched, or Robust can't convert it.</summary>
    public async Task<byte[]> JpegAsync(Guid snapshot, CancellationToken ct)
    {
        if (snapshot == Guid.Empty) return [];
        var now = time.GetUtcNow();
        Kept kept;
        lock (_gate)
        {
            if (!_kept.TryGetValue(snapshot, out kept!) || !Fresh(kept, now))
            {
                // Not tied to this visitor's request: one who leaves mustn't cancel the fetch the others wait on. The
                // fetch's own time limit ends it.
                kept = new Kept(FetchAsync(snapshot), now);
                _kept[snapshot] = kept;
                Trim(now);
            }
        }
        return (await kept.Result.WaitAsync(ct)).Jpeg;
    }

    private static bool Fresh(Kept kept, DateTimeOffset now)
    {
        if (!kept.Result.IsCompleted) return true; // still being fetched: share it
        if (!kept.Result.IsCompletedSuccessfully || kept.Result.Result.Busy) return false;
        return now < kept.Asked + (kept.Result.Result.Jpeg.Length == 0 ? FailureLifetime : Lifetime);
    }

    private void Trim(DateTimeOffset now)
    {
        if (_kept.Count <= MaxKept) return;
        foreach (var (id, old) in _kept.ToList())
            if (!Fresh(old, now)) _kept.Remove(id);
        foreach (var (id, _) in _kept.OrderBy(k => k.Value.Asked).Take(_kept.Count - MaxKept).ToList())
            _kept.Remove(id);
    }

    private async Task<Fetched> FetchAsync(Guid snapshot)
    {
        // The client's own timeout covers only the headers; this one covers the bodies too, so a Robust that freezes
        // mid-answer can't keep every visitor's picture waiting.
        using var cts = new CancellationTokenSource(FetchTimeout, time);
        var ct = cts.Token;
        try
        {
            using var client = http.CreateClient(ClientName);
            var robust = GridEndpoints.PrivateBase(settings());
            byte[] texture;
            using (var asset = await client.GetAsync(new Uri(robust, $"/assets/{snapshot}/data"), HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!asset.IsSuccessStatusCode || asset.Content.Headers.ContentLength > MaxBytes) return new([]);
                texture = await ReadAtMostAsync(asset.Content, MaxBytes + 1, ct);
                if (texture.Length is 0 or > MaxBytes) return new([]);
            }
            await _converting.WaitAsync(ct);
            try
            {
                using var body = new ByteArrayContent(texture);
                body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var converted = await client.PostAsync(new Uri(robust, "/gridgate/j2k"), body, ct);
                if (converted.StatusCode == HttpStatusCode.ServiceUnavailable) return new([], Busy: true);
                if (!converted.IsSuccessStatusCode || converted.Content.Headers.ContentType?.MediaType != "image/jpeg") return new([]);
                return new(await converted.Content.ReadAsByteArrayAsync(ct));
            }
            finally
            {
                _converting.Release();
            }
        }
        catch (Exception)
        {
            // Robust is down, slow or frozen, has no converter, or something unexpected: the card shows the map tile,
            // never an error page.
            return new([]);
        }
    }

    private static async Task<byte[]> ReadAtMostAsync(HttpContent content, int limit, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while (copy.Length < limit && (read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - copy.Length)), ct)) > 0)
            copy.Write(buffer, 0, read);
        return copy.ToArray();
    }
}
