using System.Net;
using System.Security.Cryptography.X509Certificates;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Plans;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Money;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>A Region Server pairing with the real money service (money plan 3, task 6).</summary>
[Collection(GridCollection.Name)]
public sealed class MoneyPairClientTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>, IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-money-pairc-" + Guid.NewGuid().ToString("N"));
    private MoneyServiceHost? _host;
    private WebApplication? _impostor;
    private LauncherSettings _grid = null!;
    private MoneyPairing _pairing = null!;
    private string _fingerprint = "";
    private int _port;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null) await _host.StopAsync(CancellationToken.None);
        if (_impostor is not null) await _impostor.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>The Grid Asset Server's money service on loopback, Play coins on.</summary>
    private async Task<MoneyTestDatabase> StartGridAsync()
    {
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();
        _port = MoneyMariaDb.FreePort();
        _grid = db.Settings with { Plan = OwnerPlan.GridAsset, MoneyPort = _port, Money = new MoneySettings(Mode: MoneyMode.Play) };
        var dbSettings = _grid;
        var presence = new PresenceCheck(MoneyConnections.Robust(() => dbSettings));
        var ledger = new MoneyLedger(MoneyConnections.Money(() => dbSettings), () => _grid.Money, () => MoneyMode.Play,
            presence.IsLocalUserAsync, presence.IsKnownVisitorAsync, TimeProvider.System);
        Directory.CreateDirectory(_dir);
        var cert = MoneyCertificate.LoadOrCreate(Path.Combine(_dir, MoneyCertificate.FileName), TimeProvider.System);
        _fingerprint = MoneyCertificate.Fingerprint(cert);
        _pairing = new MoneyPairing(TimeProvider.System);
        _host = new MoneyServiceHost(ledger, presence, () => _grid, () => MoneyMode.Play, () => cert,
            new MoneyAuthFailures(TimeProvider.System, NullLogger<MoneyAuthFailures>.Instance), NullLoggerFactory.Instance, new MoneyStatus(), _pairing,
            change => _grid = change(_grid), new RegionHosts(MoneyConnections.Robust(() => dbSettings)));
        await _host.StartAsync(Ct);
        return db;
    }

    /// <summary>The Region Server's settings, joined to a grid that publishes <paramref name="fingerprint"/> on <paramref name="port"/>.</summary>
    private static LauncherSettings RegionServer(int port, string fingerprint) => new LauncherSettings
    {
        Db = new DatabaseSecrets("r", "b", "o"),
        Console = new ConsoleSecrets("u", "p"),
        MachineId = "rs",
        Plan = OwnerPlan.RegionServer,
        Hostname = "192.168.1.20",
        Join = Joined(port, fingerprint),
    };

    private static JoinedGrid Joined(int port, string fingerprint) => new("127.0.0.1", 8002, 8003, "Avalon", "http://127.0.0.1:8002/", "1.0", false, null, null,
        MoneyMode.Play, port, fingerprint, "http://127.0.0.1:8001/economy/");

    private sealed class FakeJoin(JoinedGrid answer) : IGridJoinClient
    {
        public Task<JoinedGrid> ReadAsync(string host, int port, CancellationToken ct) => Task.FromResult(answer);
    }

    [Fact]
    public async Task Pairs_with_the_real_service()
    {
        var db = await StartGridAsync();
        await db.AddRegion(Guid.NewGuid(), "192.168.1.20", port: 8004); // the Region Server's region, at its first port
        var (code, _) = _pairing.Issue();
        var rs = RegionServer(_port, _fingerprint);
        var client = new MoneyPairClient(new FakeJoin(rs.Join!), TimeProvider.System);

        var outcome = await client.PairAsync(rs, PairingCode.Show(code).ToLowerInvariant(), Ct);

        Assert.Null(outcome.Error);
        var pairing = Assert.IsType<ServerPairing>(outcome.Pairing);
        Assert.Equal("127.0.0.1", pairing.GridHost);
        Assert.Equal(_fingerprint, pairing.Fingerprint);
        Assert.Equal("192.168.1.20", Assert.Single(_grid.Money.PairedServers!).Host);
        Assert.Equal(PairedServer.HashOf(pairing.Key), _grid.Money.PairedServers![0].KeyHash);

        var paired = rs with { Money = rs.Money with { Pairing = pairing } };
        Assert.Equal(PairingHealth.Ok, await client.CheckAsync(paired, Ct));
        Assert.Equal(8004, _grid.Money.PairedServers![0].FirstPort);
        Assert.Equal(MoneyMode.Play, outcome.Grid?.Money);
    }

    [Fact]
    public async Task Paired_but_no_regions_seen_checks_as_no_regions()
    {
        await StartGridAsync();
        var (code, _) = _pairing.Issue();
        var rs = RegionServer(_port, _fingerprint);
        var client = new MoneyPairClient(new FakeJoin(rs.Join!), TimeProvider.System);
        var pairing = (await client.PairAsync(rs, code, Ct)).Pairing!;

        Assert.Equal(PairingHealth.NoRegions, await client.CheckAsync(rs with { Money = rs.Money with { Pairing = pairing } }, Ct));
    }

    [Fact]
    public async Task Refused_code_saves_nothing()
    {
        await StartGridAsync();
        _pairing.Issue();
        var rs = RegionServer(_port, _fingerprint);
        var client = new MoneyPairClient(new FakeJoin(rs.Join!), TimeProvider.System);

        var outcome = await client.PairAsync(rs, PairingCode.Show(PairingCode.New()), Ct);

        Assert.Null(outcome.Pairing);
        Assert.Contains("didn't accept this code", outcome.Error);
        Assert.Null(_grid.Money.PairedServers);
    }

    [Fact]
    public async Task Server_without_the_code_is_not_trusted()
    {
        // An impostor with its own certificate, named in a grid info someone swapped, answers "ok" without knowing the code.
        Directory.CreateDirectory(_dir);
        var cert = MoneyCertificate.LoadOrCreate(Path.Combine(_dir, "impostor.pfx"), TimeProvider.System);
        var port = MoneyMariaDb.FreePort();
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port, l => l.UseHttps(cert)));
        builder.Services.AddRoutingCore();
        _impostor = builder.Build();
        _impostor.UseRouting();
        _impostor.MapPost("/money/v1/pair", () => Results.Json(new { code = "ok", key = "stolen", proof = new string('0', 64) }));
        await _impostor.StartAsync(Ct);
        var rs = RegionServer(port, MoneyCertificate.Fingerprint(cert));
        var client = new MoneyPairClient(new FakeJoin(rs.Join!), TimeProvider.System);

        var outcome = await client.PairAsync(rs, PairingCode.Show(PairingCode.New()), Ct);

        Assert.Null(outcome.Pairing);
        Assert.Equal("The Grid Asset Server didn't prove it made this code. Nothing was saved.", outcome.Error);
    }

    [Fact]
    public async Task Swapped_fingerprint_reaches_nobody()
    {
        await StartGridAsync();
        var (code, _) = _pairing.Issue();
        var rs = RegionServer(_port, new string('0', 64));
        var client = new MoneyPairClient(new FakeJoin(rs.Join!), TimeProvider.System);

        var outcome = await client.PairAsync(rs, code, Ct);

        Assert.Null(outcome.Pairing);
        Assert.Contains("Couldn't reach the grid's money service", outcome.Error);
        Assert.Equal(code, _pairing.Redeem(c => c == code)); // nothing reached the service: the code is untouched
    }

    [Fact]
    public async Task Removed_pairing_checks_as_refused()
    {
        await StartGridAsync();
        var (code, _) = _pairing.Issue();
        var rs = RegionServer(_port, _fingerprint);
        var client = new MoneyPairClient(new FakeJoin(rs.Join!), TimeProvider.System);
        var pairing = (await client.PairAsync(rs, code, Ct)).Pairing!;
        var paired = rs with { Money = rs.Money with { Pairing = pairing } };

        _grid = _grid with { Money = _grid.Money with { PairedServers = [] } };

        Assert.Equal(PairingHealth.Refused, await client.CheckAsync(paired, Ct));
    }

    [Fact]
    public async Task A_new_certificate_checks_as_changed()
    {
        await StartGridAsync();
        var (code, _) = _pairing.Issue();
        var rs = RegionServer(_port, _fingerprint);
        var pairing = (await new MoneyPairClient(new FakeJoin(rs.Join!), TimeProvider.System).PairAsync(rs, code, Ct)).Pairing!;
        var paired = rs with { Money = rs.Money with { Pairing = pairing with { Fingerprint = new string('0', 64) } } };

        Assert.Equal(PairingHealth.CertificateChanged, await new MoneyPairClient(new FakeJoin(rs.Join!), TimeProvider.System).CheckAsync(paired, Ct));
    }

    [Fact]
    public async Task Grid_money_off_has_nothing_to_pair()
    {
        var rs = RegionServer(8000, new string('A', 64));
        var client = new MoneyPairClient(new FakeJoin(rs.Join! with { Money = MoneyMode.Off }), TimeProvider.System);

        var outcome = await client.PairAsync(rs, PairingCode.New(), Ct);

        Assert.Equal("The grid's money is off, so there's nothing to pair.", outcome.Error);
    }

    [Fact]
    public async Task Typo_is_caught_before_anything_is_sent()
    {
        var rs = RegionServer(1, new string('A', 64));
        var client = new MoneyPairClient(new FakeJoin(rs.Join!), TimeProvider.System);

        var outcome = await client.PairAsync(rs, "ABCD-EFGH", Ct);

        Assert.StartsWith("That isn't a pairing code.", outcome.Error);
    }
}
