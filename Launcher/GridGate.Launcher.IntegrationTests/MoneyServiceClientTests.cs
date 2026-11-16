using System.Diagnostics;
using GridGate.Region.Money;
using Xunit;
using static GridGate.Launcher.IntegrationTests.MoneyServiceRig;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// The region module's client (compiled in from addon-modules/GridGateMoney) against the real money service (money plan 2,
/// task 3): the contract both sides must agree on. The service has A verified at Here, key p1's region.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class MoneyServiceClientTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>, IAsyncDisposable
{
    private MoneyServiceRig? _rig;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MoneyWho ClientA => new(A, "", SessionA, SecureA);
    private static MoneyWho ClientB => new(B, "");

    public async ValueTask DisposeAsync()
    {
        if (_rig is not null) await _rig.DisposeAsync();
    }

    private async Task StartAsync() => _rig = await MoneyServiceRig.StartAsync(maria);

    private MoneyServiceClient Client(string? key = null, string? fingerprint = null) =>
        new(new Uri($"https://127.0.0.1:{_rig!.Port}/money/v1/"), key ?? Key, fingerprint ?? _rig.Fingerprint);

    private async Task<long> Entries() => await _rig!.Db.Scalar("SELECT COUNT(*) FROM `money`.`entries`");

    [Fact]
    public async Task Client_talks_to_the_real_service()
    {
        await StartAsync();
        using var client = Client();

        var answer = await client.BalanceAsync(ClientA, Ct);

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

        var answer = await client.TransferAsync(Guid.NewGuid(), "pay_avatar", ClientA, ClientB, 5, Here, null, null, Ct);

        Assert.Equal("unavailable", answer.Code);
        Assert.Equal(before, await Entries());
    }

    [Fact]
    public async Task Wrong_key_is_unavailable()
    {
        await StartAsync();
        using var client = Client(key: "not-the-key");

        Assert.Equal("unavailable", (await client.BalanceAsync(ClientA, Ct)).Code);
        Assert.Null(await client.EconomyAsync(Ct));
    }

    [Fact]
    public async Task Service_down_is_unavailable_within_timeout()
    {
        using var client = new MoneyServiceClient(new Uri($"https://127.0.0.1:{MoneyMariaDb.FreePort()}/money/v1/"), Key, new string('0', 64));
        var clock = Stopwatch.StartNew();

        var answer = await client.BalanceAsync(ClientA, Ct);

        Assert.Equal("unavailable", answer.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(11), $"{clock.Elapsed}");
    }

    [Fact]
    public async Task Hold_complete_round_trip()
    {
        await StartAsync();
        using var client = Client();

        var hold = await client.HoldAsync(Guid.NewGuid(), "object", ClientA, ClientB, 25, Here, Guid.NewGuid(), "dress", Ct);
        Assert.True(hold.Ok, hold.Code);
        Assert.Equal(75, hold.Balance);
        Assert.Null(hold.ToBalance);

        var done = await client.CompleteAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct);

        Assert.True(done.Ok, done.Code);
        Assert.Equal(125, done.ToBalance);
        Assert.Null(done.Balance);
    }

    [Fact]
    public async Task A_fee_is_charged_in_the_region_the_client_names()
    {
        await StartAsync();
        using var client = Client();

        Assert.Equal("ok", (await client.ChargeAsync(Guid.NewGuid(), "upload_fee", ClientA, 3, null, Here, Ct)).Code);
        Assert.Equal("wrong_region", (await client.ChargeAsync(Guid.NewGuid(), "upload_fee", ClientA, 3, null, There, Ct)).Code);
    }

    /// <summary>
    /// The module's health, handoff and arrival, as the client sends them, move A from Here to There, so
    /// a fee is charged There and no longer Here. A crossing back, a handoff to another grid and its cancel are accepted too.
    /// </summary>
    [Fact]
    public async Task Client_handoff_and_arrival_place_an_avatar_on_the_real_service()
    {
        await StartAsync();
        using var p1 = Client();
        using var p2 = Client(key: Key2);

        Assert.Equal("ok", (await p1.HealthAsync(2, [Here], Ct)).Code);
        Assert.Equal("ok", (await p2.HealthAsync(2, [There], Ct)).Code);
        Assert.Equal("ok", (await p1.HandoffAsync(Guid.NewGuid(), SessionA, Here, There, "teleport", Ct)).Code);
        Assert.Equal("ok", (await p2.ArrivedAsync(Guid.NewGuid(), SessionA, A, There, Ct)).Code);

        Assert.Equal("ok", (await p2.ChargeAsync(Guid.NewGuid(), "upload_fee", ClientA, 3, null, There, Ct)).Code);
        Assert.Equal("wrong_region", (await p1.ChargeAsync(Guid.NewGuid(), "upload_fee", ClientA, 3, null, Here, Ct)).Code);

        // Time passes between one move and the next, as it does in the world.
        _rig!.LocationClock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("ok", (await p2.HandoffAsync(Guid.NewGuid(), SessionA, There, Here, "crossing", Ct)).Code);
        Assert.Equal("ok", (await p1.ArrivedAsync(Guid.NewGuid(), SessionA, A, Here, Ct)).Code);
        // A teleport to another grid that fails is taken back; one that goes through leaves nothing to charge.
        _rig.LocationClock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("ok", (await p1.HandoffAsync(Guid.NewGuid(), SessionA, Here, Guid.NewGuid(), "other_grid", Ct)).Code);
        Assert.Equal("ok", (await p1.CancelHandoffAsync(SessionA, Here, Ct)).Code);
        Assert.Equal("ok", (await p1.ChargeAsync(Guid.NewGuid(), "upload_fee", ClientA, 3, null, Here, Ct)).Code);
        _rig.LocationClock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("ok", (await p1.HandoffAsync(Guid.NewGuid(), SessionA, Here, Guid.NewGuid(), "other_grid", Ct)).Code);
        Assert.Equal("unverified_avatar", (await p1.ChargeAsync(Guid.NewGuid(), "upload_fee", ClientA, 3, null, Here, Ct)).Code);
    }

    [Fact]
    public async Task Every_body_the_client_sends_is_accepted()
    {
        // The service refuses unknown fields with 400, which the client reads as unavailable: any name the two sides
        // disagree on shows up here as "unavailable" instead of a real code.
        await StartAsync();
        using var client = Client();
        var txn = Guid.NewGuid();

        Assert.NotEqual("unavailable", (await client.TransferAsync(Guid.NewGuid(), "pay_object", ClientA, ClientB, 1, Here, Guid.NewGuid(), "box", Ct)).Code);
        Assert.NotEqual("unavailable", (await client.PayoutAsync(Guid.NewGuid(), A, ClientB, 1, Here, Guid.NewGuid(), "slot", Ct)).Code);
        Assert.NotEqual("unavailable", (await client.ChargeAsync(Guid.NewGuid(), "upload_fee", ClientA, 0, null, Here, Ct)).Code);
        Assert.NotEqual("unavailable", (await client.ReturnAsync(txn, 4242, Ct)).Code);
    }
}
