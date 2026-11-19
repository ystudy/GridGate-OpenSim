using System.Diagnostics;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
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
    public async Task An_unavailable_answer_from_the_service_is_not_lost()
    {
        await StartAsync();
        _rig!.Mode = MoneyMode.Off; // coins off: the service itself answers unavailable
        using var client = Client();

        var answer = await client.TransferAsync(Guid.NewGuid(), "pay_avatar", ClientA, ClientB, 5, Here, null, null, Ct);

        Assert.Equal("unavailable", answer.Code);
        Assert.False(answer.Lost);
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
        Assert.Null(done.ToBalance); // B is in program two's region, which this key doesn't cover
        Assert.Null(done.Balance);
    }

    [Fact]
    public async Task Client_reads_fee_holds_from_economy()
    {
        await StartAsync();
        using var client = Client();

        Assert.True((await client.EconomyAsync(Ct))!.FeeHolds);
    }

    [Fact]
    public async Task A_fee_hold_without_a_seller_round_trip()
    {
        // The client sends no seller at all, which the service takes only for a fee.
        await StartAsync();
        using var client = Client();

        var hold = await client.HoldAsync(Guid.NewGuid(), "upload_fee", ClientA, null, 3, Here, null, null, Ct);
        Assert.True(hold.Ok, hold.Code);
        Assert.Equal(97, hold.Balance);
        var done = await client.CompleteAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct);
        Assert.True(done.Ok, done.Code);
        Assert.Null(done.ToBalance);

        var join = await client.HoldAsync(Guid.NewGuid(), "group_join_fee", ClientA, ClientB, 5, Here, null, null, Ct);
        Assert.True(join.Ok, join.Code);
        Assert.Null((await client.CompleteAsync(Guid.NewGuid(), join.HoldId!.Value, Ct)).ToBalance); // the founder is in program two's region
        // A purchase with no seller is turned away (a 400), which the client reads as a final unavailable.
        var purchase = await client.HoldAsync(Guid.NewGuid(), "object", ClientA, null, 3, Here, null, null, Ct);
        Assert.Equal("unavailable", purchase.Code);
        Assert.True(purchase.Final);
        await _rig!.AssertBooksSoundAsync();
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
        Assert.Equal("ok", (await p1.HandoffAsync(Guid.NewGuid(), SessionA, Here, There, "teleport", null, null, Ct)).Code);
        Assert.Equal("ok", (await p2.ArrivedAsync(Guid.NewGuid(), SessionA, A, There, Ct)).Code);

        Assert.Equal("ok", (await p2.ChargeAsync(Guid.NewGuid(), "upload_fee", ClientA, 3, null, There, Ct)).Code);
        Assert.Equal("wrong_region", (await p1.ChargeAsync(Guid.NewGuid(), "upload_fee", ClientA, 3, null, Here, Ct)).Code);

        // Time passes between one move and the next, as it does in the world.
        _rig!.LocationClock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("ok", (await p2.HandoffAsync(Guid.NewGuid(), SessionA, There, Here, "crossing", null, null, Ct)).Code);
        Assert.Equal("ok", (await p1.ArrivedAsync(Guid.NewGuid(), SessionA, A, Here, Ct)).Code);
        // A teleport to another grid that fails is taken back; one that goes through leaves nothing to charge.
        _rig.LocationClock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("ok", (await p1.HandoffAsync(Guid.NewGuid(), SessionA, Here, Guid.NewGuid(), "other_grid", null, null, Ct)).Code);
        Assert.Equal("ok", (await p1.CancelHandoffAsync(SessionA, Here, Ct)).Code);
        Assert.Equal("ok", (await p1.ChargeAsync(Guid.NewGuid(), "upload_fee", ClientA, 3, null, Here, Ct)).Code);
        _rig.LocationClock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("ok", (await p1.HandoffAsync(Guid.NewGuid(), SessionA, Here, Guid.NewGuid(), "other_grid", null, null, Ct)).Code);
        Assert.Equal("unverified_avatar", (await p1.ChargeAsync(Guid.NewGuid(), "upload_fee", ClientA, 3, null, Here, Ct)).Code);
    }

    private static readonly Guid HereToo = Guid.Parse("e1000000-0000-0000-0000-00000000000e"); // p1's second region

    /// <summary>Program p1 runs a second region, Here too, and its module checks in with both.</summary>
    private async Task SecondRegionForP1(MoneyServiceClient p1)
    {
        _rig!.Settings = _rig.Settings with { Regions = [.. _rig.Settings.Regions, new RegionSettings("p1", "Here too", HereToo, UdpPort: 8006)] };
        Assert.Equal("ok", (await p1.HealthAsync(2, [Here, HereToo], Ct)).Code);
    }

    /// <summary>Waits for a held arrival's answer, moving the location clock on in its retry steps, never past its 10-second wait.</summary>
    private async Task<MoneyAnswer> Settle(Task<MoneyAnswer> arrival)
    {
        var moved = TimeSpan.Zero;
        while (!arrival.IsCompleted)
        {
            Assert.True(moved < MoneyLocations.ArrivalWait - MoneyLocations.ArrivalRetry, "the held arrival didn't find its handoff in time");
            await Task.WhenAny(arrival, Task.Delay(50, Ct));
            if (arrival.IsCompleted) break;
            _rig!.LocationClock.Advance(MoneyLocations.ArrivalRetry);
            moved += MoneyLocations.ArrivalRetry;
        }
        return await arrival;
    }

    /// <summary>A is verified at <paramref name="region"/> under <paramref name="key"/>, and no session or handoff is left handed and waiting.</summary>
    private async Task AssertVerifiedAndNothingHanded(Guid region, string key)
    {
        var at = await _rig!.Locations.WhereAsync(SessionA, Ct);
        Assert.Equal((region, key, LocationState.Verified), (at?.RegionId, at?.KeyId, at?.State));
        Assert.Equal(0, await _rig.Db.Scalar("SELECT COUNT(*) FROM `money`.`session_locations` WHERE state = 'Handed'"));
        Assert.Equal(0, await _rig.Db.Scalar("SELECT COUNT(*) FROM `money`.`handoffs` WHERE kind IN ('Teleport', 'Crossing')"));
    }

    /// <summary>
    /// A teleport between two regions of one program, reported as its module sends it: the handoff as soon as the avatar is
    /// root in the region reached, then the arrival there.
    /// </summary>
    [Fact]
    public async Task A_teleport_within_one_program_ends_verified_where_it_went()
    {
        await StartAsync();
        using var p1 = Client();
        await SecondRegionForP1(p1);

        Assert.Equal("ok", (await p1.HandoffAsync(Guid.NewGuid(), SessionA, Here, HereToo, "teleport", null, null, Ct)).Code);
        Assert.Equal("ok", (await p1.ArrivedAsync(Guid.NewGuid(), SessionA, A, HereToo, Ct)).Code);

        await AssertVerifiedAndNothingHanded(HereToo, "p1");
        Assert.Equal("ok", (await p1.ChargeAsync(Guid.NewGuid(), "upload_fee", ClientA, 3, null, HereToo, Ct)).Code);
    }

    /// <summary>
    /// A teleport between two programs, when the region reached reports the arrival before the region left has let the
    /// avatar go: the service holds the arrival, and the handoff that comes within the wait places A there, verified.
    /// </summary>
    [Fact]
    public async Task A_teleport_between_programs_whose_arrival_comes_first_ends_verified_where_it_went()
    {
        await StartAsync();
        using var p1 = Client();
        using var p2 = Client(key: Key2);

        var arrival = p2.ArrivedAsync(Guid.NewGuid(), SessionA, A, There, Ct);
        await Task.Delay(300, Ct);
        _rig!.LocationClock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(100, Ct);
        Assert.False(arrival.IsCompleted, "the arrival should be held for its handoff");
        Assert.Equal("ok", (await p1.HandoffAsync(Guid.NewGuid(), SessionA, Here, There, "teleport", null, null, Ct)).Code);

        Assert.Equal("ok", (await Settle(arrival)).Code);
        await AssertVerifiedAndNothingHanded(There, "p2");
        Assert.Equal("ok", (await p2.ChargeAsync(Guid.NewGuid(), "upload_fee", ClientA, 3, null, There, Ct)).Code);
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
