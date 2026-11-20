using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The test hosts start no file watchers for the web root (see <see cref="NoWebRootWatchers"/>).</summary>
public sealed class NoWebRootWatchersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Test_hosts_serve_the_web_root_without_watching_it()
    {
        await using var f = new LauncherFactory();
        using var client = f.CreateClient();

        var root = f.Services.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider;
        using var r = await client.GetAsync("/js/map-core.js", Ct);

        Assert.IsType<NoWebRootWatchers.Unwatched>(root);
        Assert.Same(NullChangeToken.Singleton, root.Watch("js/map-core.js"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
}
