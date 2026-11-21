using System.Net;
using System.Net.Http.Headers;
using GridGate.Launcher.Featured;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// Parcel snapshots for the Featured cards (spec 2026-11-21-featured-places-design, "Pictures"): the texture from Robust's
/// asset service, converted by Robust's <c>/gridgate/j2k</c>, kept an hour, shared by visitors who ask at once.
/// </summary>
public sealed class ParcelPicturesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Snapshot = Guid.Parse("0d000000-0000-0000-0000-0000000000a1");
    private static readonly byte[] J2k = [0xFF, 0x4F, 0xFF, 0x51, 1, 2, 3];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 9, 9];

    /// <summary>Robust's private services: the asset's data and the converter.</summary>
    private sealed class FakeRobust : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];
        public byte[] Texture { get; set; } = J2k;
        public HttpStatusCode AssetStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode ConvertStatus { get; set; } = HttpStatusCode.OK;
        public byte[]? Sent { get; private set; }
        public TaskCompletionSource? Gate { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Asked) Asked.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            if (Gate is { } gate) await gate.Task;
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == $"/assets/{Snapshot}/data")
                return new HttpResponseMessage(AssetStatus) { Content = new ByteArrayContent(Texture) };
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/gridgate/j2k")
            {
                Sent = await request.Content!.ReadAsByteArrayAsync(ct);
                var answer = new HttpResponseMessage(ConvertStatus) { Content = new ByteArrayContent(ConvertStatus == HttpStatusCode.OK ? Jpeg : []) };
                answer.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                return answer;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static ParcelPictures Pictures(FakeRobust robust, TimeProvider time) =>
        new(new Clients(robust), TestSettings.Create, time);

    [Fact]
    public async Task Snapshot_fetched_converted_and_kept()
    {
        var robust = new FakeRobust();
        var time = new FakeTimeProvider();
        var pictures = Pictures(robust, time);

        Assert.Equal(Jpeg, await pictures.JpegAsync(Snapshot, Ct));
        Assert.Equal(J2k, robust.Sent);
        Assert.Equal([$"GET /assets/{Snapshot}/data", "POST /gridgate/j2k"], robust.Asked);

        time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(Jpeg, await pictures.JpegAsync(Snapshot, Ct));
        Assert.Equal(2, robust.Asked.Count); // kept

        time.Advance(TimeSpan.FromMinutes(2));
        await pictures.JpegAsync(Snapshot, Ct);
        Assert.Equal(4, robust.Asked.Count); // an hour on, asked again
    }

    [Fact]
    public async Task Visitors_asking_at_once_share_one_fetch()
    {
        var robust = new FakeRobust { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var pictures = Pictures(robust, new FakeTimeProvider());

        var asks = Enumerable.Range(0, 5).Select(_ => pictures.JpegAsync(Snapshot, Ct)).ToList();
        robust.Gate.SetResult();
        var all = await Task.WhenAll(asks);

        Assert.All(all, a => Assert.Equal(Jpeg, a));
        Assert.Equal(2, robust.Asked.Count);
    }

    [Fact]
    public async Task Over_2_MB_texture_is_not_sent()
    {
        var robust = new FakeRobust { Texture = new byte[ParcelPictures.MaxBytes + 1] };

        Assert.Empty(await Pictures(robust, new FakeTimeProvider()).JpegAsync(Snapshot, Ct));
        Assert.Null(robust.Sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.OK, HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.OK, HttpStatusCode.NotFound)] // a Robust without the converter
    public async Task Failure_gives_empty(HttpStatusCode asset, HttpStatusCode convert)
    {
        var robust = new FakeRobust { AssetStatus = asset, ConvertStatus = convert };

        Assert.Empty(await Pictures(robust, new FakeTimeProvider()).JpegAsync(Snapshot, Ct));
    }

    [Fact]
    public async Task No_snapshot_asks_nothing()
    {
        var robust = new FakeRobust();

        Assert.Empty(await Pictures(robust, new FakeTimeProvider()).JpegAsync(Guid.Empty, Ct));
        Assert.Empty(robust.Asked);
    }
}
