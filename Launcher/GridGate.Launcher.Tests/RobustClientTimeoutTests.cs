using System.Diagnostics;
using System.Net.Sockets;
using GridGate.Launcher.Core.OpenSim;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// The launcher's client for Robust's account, sign-in and grid services gives up soon when nothing answers, so sign-in on a
/// Region Server whose grid computer is off says so in seconds instead of after HttpClient's default 100.
/// </summary>
public sealed class RobustClientTimeoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_address_that_never_answers_fails_within_seconds_as_unreachable()
    {
        await using var f = new LauncherFactory();
        var handlers = f.Services.GetRequiredService<IHttpMessageHandlerFactory>();
        var primary = Primary(handlers.CreateHandler("GridGate.Robust"));
        // A computer that is off: the connection is never made (and never refused).
        primary.ConnectCallback = async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new SocketException();
        };
        var client = f.Services.GetRequiredService<IHttpClientFactory>().CreateClient("GridGate.Robust");

        var watch = Stopwatch.StartNew();
        var e = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("http://192.0.2.1:8003/accounts", Ct));
        watch.Stop();

        Assert.True(RobustFailure.IsUnreachable(e, Ct), e.ToString());
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(15));
        Assert.Equal(RobustClient.ConnectTimeout, primary.ConnectTimeout);
        // A Robust that is connected but slow still gets longer than a connection does, though not 100 seconds.
        Assert.InRange(client.Timeout, RobustClient.ConnectTimeout, TimeSpan.FromSeconds(30));
    }

    private static SocketsHttpHandler Primary(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler d) handler = d.InnerHandler!;
        return Assert.IsType<SocketsHttpHandler>(handler);
    }
}
