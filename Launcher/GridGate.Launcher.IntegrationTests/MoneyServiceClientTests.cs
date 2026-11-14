using System.Diagnostics;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Money;
using GridGate.Region.Money;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// The region module's client (compiled in from addon-modules/GridGateMoney) against the real money service (money plan 2,
/// task 3): the contract both sides must agree on.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class MoneyServiceClientTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>, IAsyncDisposable
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Here = Guid.Parse("e0000000-0000-0000-0000-00000000000e");
    private static readonly Guid SessionA = Guid.Parse("5000000a-0000-0000-0000-000000000005");
    private static readonly Guid SecureA = Guid.Parse("6000000a-0000-0000-0000-000000000006");
    private const string Key = "the-region-key-for-program-one-0000000000000000";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-money-client-" + Guid.NewGuid().ToString("N"));
    private MoneyServiceHost? _host;
    private MoneyTestDatabase? _db;
    private string _fingerprint = "";
    private int _port;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MoneyWho WhoA => new(A, "", SessionA, SecureA);
    private static MoneyWho WhoB => new(B, "");

    public async ValueTask DisposeAsync()
    {
        if (_host is not null) await _host.StopAsync(CancellationToken.None);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task StartAsync()
    {
        _db = await maria.FreshAsync();
        await _db.EnsureSchemaAsync();
        await _db.AddUser(A);
        await _db.AddUser(B);
        await _db.AddPresence(A, Here, SessionA, SecureA);
        _port = MoneyMariaDb.FreePort();
        var settings = _db.Settings with
        {
            MoneyPort = _port,
            Programs = [new ProgramSettings("p1", 8004, 8011)],
            Regions = [new RegionSettings("p1", "Here", Here, UdpPort: 8004)],
            Money = new MoneySettings(Mode: MoneyMode.Play, ProgramKeys: new() { ["p1"] = Key }),
        };
        var time = TimeProvider.System;
        var presence = new PresenceCheck(MoneyConnections.Robust(() => settings));
        var ledger = new MoneyLedger(MoneyConnections.Money(() => settings), () => settings.Money, () => MoneyMode.Play, presence.IsLocalUserAsync, time);
        Directory.CreateDirectory(_dir);
        var cert = MoneyCertificate.LoadOrCreate(Path.Combine(_dir, MoneyCertificate.FileName), time);
        _fingerprint = MoneyCertificate.Fingerprint(cert);
        _host = new MoneyServiceHost(ledger, presence, () => settings, () => MoneyMode.Play, () => cert,
            new MoneyAuthFailures(time, NullLogger<MoneyAuthFailures>.Instance), NullLoggerFactory.Instance);
        await _host.StartAsync(Ct);
    }

    private MoneyServiceClient Client(string? key = null, string? fingerprint = null, int? port = null) =>
        new(new Uri($"https://127.0.0.1:{port ?? _port}/money/v1/"), key ?? Key, fingerprint ?? _fingerprint);

    private async Task<long> Entries() => await _db!.Scalar("SELECT COUNT(*) FROM `money`.`entries`");

    [Fact]
    public async Task Client_talks_to_the_real_service()
    {
        await StartAsync();
        using var client = Client();

        var answer = await client.BalanceAsync(WhoA, Ct);

        Assert.True(answer.Ok, answer.Code);
        Assert.Equal(100, answer.Balance);
        var economy = await client.EconomyAsync(Ct);
        Assert.Equal("C$", economy!.CurrencySymbol);
        Assert.True(economy.VisitorWallets);
    }

    [Fact]
    public async Task Wrong_fingerprint_is_unavailable()
    {
        await StartAsync();
        var before = await Entries();
        using var client = Client(fingerprint: new string('0', 64));

        var answer = await client.TransferAsync(Guid.NewGuid(), "pay_avatar", WhoA, WhoB, 5, Here, null, null, Ct);

        Assert.Equal("unavailable", answer.Code);
        Assert.Equal(before, await Entries());
    }

    [Fact]
    public async Task Wrong_key_is_unavailable()
    {
        await StartAsync();
        using var client = Client(key: "not-the-key");

        Assert.Equal("unavailable", (await client.BalanceAsync(WhoA, Ct)).Code);
        Assert.Null(await client.EconomyAsync(Ct));
    }

    [Fact]
    public async Task Service_down_is_unavailable_within_timeout()
    {
        using var client = Client(fingerprint: new string('0', 64), port: MoneyMariaDb.FreePort());
        var clock = Stopwatch.StartNew();

        var answer = await client.BalanceAsync(WhoA, Ct);

        Assert.Equal("unavailable", answer.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(11), $"{clock.Elapsed}");
    }

    [Fact]
    public async Task Hold_complete_round_trip()
    {
        await StartAsync();
        using var client = Client();

        var hold = await client.HoldAsync(Guid.NewGuid(), "object", WhoA, WhoB, 25, Here, Guid.NewGuid(), "dress", Ct);
        Assert.True(hold.Ok, hold.Code);
        Assert.Equal(75, hold.Balance);
        Assert.Null(hold.ToBalance);

        var done = await client.CompleteAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct);

        Assert.True(done.Ok, done.Code);
        Assert.Equal(125, done.ToBalance);
        Assert.Null(done.Balance);
    }

    [Fact]
    public async Task Every_body_the_client_sends_is_accepted()
    {
        // The service refuses unknown fields with 400, which the client reads as unavailable: any name the two sides
        // disagree on shows up here as "unavailable" instead of a real code.
        await StartAsync();
        using var client = Client();
        var txn = Guid.NewGuid();

        Assert.NotEqual("unavailable", (await client.TransferAsync(Guid.NewGuid(), "pay_object", WhoA, WhoB, 1, Here, Guid.NewGuid(), "box", Ct)).Code);
        Assert.NotEqual("unavailable", (await client.PayoutAsync(Guid.NewGuid(), A, WhoB, 1, Here, Guid.NewGuid(), "slot", Ct)).Code);
        Assert.NotEqual("unavailable", (await client.ChargeAsync(Guid.NewGuid(), "upload_fee", WhoA, 0, null, Ct)).Code);
        Assert.NotEqual("unavailable", (await client.ReturnAsync(txn, 4242, Ct)).Code);
    }
}
