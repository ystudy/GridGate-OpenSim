using System.Net;
using System.Net.Http.Headers;
using GridGate.Launcher.Front;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// The Automatic places' map tiles: visitors who come at once while a tile is missing or expired share one ask to Robust
/// (spec 2026-11-19-front-page-design, Constraints).
/// </summary>
public sealed class FrontMapTilesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Robust's map service, answering a JPEG only once the test lets it.</summary>
    private sealed class SlowMap : HttpMessageHandler
    {
        private int _asked;
        public int Asked => Volatile.Read(ref _asked);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _asked);
            await Release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(TestPictures.Jpeg(64, 64)) { Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") } },
            };
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static FrontMapTiles Tiles(SlowMap map, TimeProvider time) =>
        new(new FakeGridController { SettingsSource = TestSettings.Create }, new Clients(map), time);

    private static readonly FrontAutoPlace Harbour = new(Guid.NewGuid(), "Harbour", 1000, 1000, 3);

    [Fact]
    public async Task Visitors_at_once_share_one_ask()
    {
        var map = new SlowMap();
        var tiles = Tiles(map, new FakeTimeProvider());

        var all = Enumerable.Range(0, 5).Select(_ => tiles.TileAsync(Harbour, Ct)).ToArray();
        map.Release.SetResult();
        var got = await Task.WhenAll(all);

        Assert.Equal(1, map.Asked);
        Assert.All(got, g => Assert.NotEmpty(g.Bytes));
    }

    [Fact]
    public async Task An_expired_tile_is_asked_again_once()
    {
        var map = new SlowMap();
        var time = new FakeTimeProvider();
        var tiles = Tiles(map, time);
        map.Release.SetResult();
        await tiles.TileAsync(Harbour, Ct);

        time.Advance(FrontPageSnapshot.Lifetime + TimeSpan.FromSeconds(1));
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => tiles.TileAsync(Harbour, Ct)));

        Assert.Equal(2, map.Asked);
    }

    [Fact]
    public async Task A_visitor_who_leaves_does_not_cancel_the_others()
    {
        var map = new SlowMap();
        var tiles = Tiles(map, new FakeTimeProvider());
        using var leaves = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var first = tiles.TileAsync(Harbour, leaves.Token);
        var second = tiles.TileAsync(Harbour, Ct);
        await leaves.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        map.Release.SetResult();

        Assert.NotEmpty((await second).Bytes);
        Assert.Equal(1, map.Asked);
    }
}
