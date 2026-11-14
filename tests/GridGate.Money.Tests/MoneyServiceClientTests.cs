using System.Net;
using System.Net.Sockets;
using GridGate.Region.Money;
using Xunit;

namespace GridGate.Money.Tests;

/// <summary>What the client reports when a call fails, for the region log (found in the live checks).</summary>
public sealed class MoneyServiceClientTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task A_failed_call_is_reported_without_secrets()
    {
        var failures = new List<string>();
        var session = Guid.NewGuid();
        using var client = new MoneyServiceClient(new Uri($"https://127.0.0.1:{FreePort()}/money/v1/"), "the-secret-key", new string('A', 64), failures.Add);

        var answer = await client.BalanceAsync(new MoneyWho(Guid.NewGuid(), "", session, Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal("unavailable", answer.Code);
        var line = Assert.Single(failures);
        Assert.StartsWith("balance: ", line);
        Assert.DoesNotContain("the-secret-key", line);
        Assert.DoesNotContain(session.ToString(), line);
    }
}
