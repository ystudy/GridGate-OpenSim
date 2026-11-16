using System.Net;
using GridGate.Launcher.Core.Money;
using Xunit;
using static GridGate.Launcher.IntegrationTests.MoneyServiceRig;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>Which regions the money service counts as verified, and why not when they aren't (money design §8.2).</summary>
[Collection(GridCollection.Name)]
public sealed class RegionVerificationTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>, IAsyncDisposable
{
    private const string HostA = "192.168.1.20";
    private const string HostB = "192.168.1.30";
    private const string ServerKeyA = "the-region-key-for-server-a-000000000000000000";

    private MoneyServiceRig? _rig;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_rig is not null) await _rig.DisposeAsync();
    }

    // No module has checked in yet: each test says which have.
    private async Task<MoneyServiceRig> StartAsync() => _rig = await MoneyServiceRig.StartAsync(maria, place: false);

    private static async Task<RegionStatus> StatusOf(MoneyServiceRig rig, Guid region) =>
        (await rig.Verification.AllAsync(rig.Settings, Ct)).Single(r => r.RegionId == region).Status;

    private static PairedServer Server(string host, string key) =>
        new(Guid.NewGuid(), host, PairedServer.HashOf(key), DateTimeOffset.UtcNow, 9000);

    private static RegionKeyGrant GrantOf(MoneyServiceRig rig, string instance) =>
        RegionKeyRing.FromSettings(rig.Settings).TryAuthenticate(instance == "p1" ? Key : Key2, out var g) ? g! : throw new InvalidOperationException();

    [Fact]
    public async Task Health_with_protocol_2_makes_covered_regions_verified()
    {
        var rig = await StartAsync();

        var answer = await rig.Post("health", new { protocol = 2, regions = new[] { Here } });

        Assert.Equal(HttpStatusCode.OK, answer.Status);
        Assert.True(await rig.Verification.IsVerifiedAsync(GrantOf(rig, "p1"), Here, Ct));
        Assert.False(await rig.Verification.IsVerifiedAsync(GrantOf(rig, "p2"), There, Ct));
        Assert.Equal(RegionStatus.Verified, await StatusOf(rig, Here));
    }

    [Fact]
    public async Task Health_without_protocol_is_needs_update()
    {
        var rig = await StartAsync();

        await rig.Post("health", new { regions = new[] { Here } });

        Assert.False(await rig.Verification.IsVerifiedAsync(GrantOf(rig, "p1"), Here, Ct));
        Assert.Equal(RegionStatus.NeedsUpdate, await StatusOf(rig, Here));
    }

    [Fact]
    public async Task Not_heard_for_11_minutes_is_not_heard_from()
    {
        var rig = await StartAsync();
        await rig.Post("health", new { protocol = 2, regions = new[] { Here } });

        rig.VerifyClock.Advance(TimeSpan.FromMinutes(9));
        Assert.True(await rig.Verification.IsVerifiedAsync(GrantOf(rig, "p1"), Here, Ct));
        rig.VerifyClock.Advance(TimeSpan.FromMinutes(2));

        Assert.False(await rig.Verification.IsVerifiedAsync(GrantOf(rig, "p1"), Here, Ct));
        Assert.Equal(RegionStatus.NotHeardFrom, await StatusOf(rig, Here));
    }

    [Fact]
    public async Task Region_with_no_key_is_no_gridgate_money()
    {
        var rig = await StartAsync();

        Assert.Equal(RegionStatus.NoGridGateMoney, await StatusOf(rig, There));
    }

    [Fact]
    public async Task Unpaired_server_region_is_not_paired()
    {
        var rig = await StartAsync();
        var stranger = Guid.NewGuid();
        await rig.Db.AddRegion(stranger, "10.9.9.9");

        var state = (await rig.Verification.AllAsync(rig.Settings, Ct)).Single(r => r.RegionId == stranger);

        Assert.Equal(RegionStatus.NotPaired, state.Status);
        Assert.Null(state.KeyId);
        Assert.Equal(("10.9.9.9", 9000), (state.ServerIp, state.Port));
    }

    [Fact]
    public async Task A_region_with_no_name_or_address_is_listed_not_a_failure()
    {
        var rig = await StartAsync();
        var odd = Guid.NewGuid();
        await rig.Db.AddRegion(odd, "10.9.9.9");
        // Robust's own table allows both to be NULL.
        await rig.Db.Sql("ALTER TABLE `robust`.`regions` MODIFY `regionName` VARCHAR(128) NULL, MODIFY `serverIP` VARCHAR(64) NULL");
        await rig.Db.Sql($"UPDATE `robust`.`regions` SET regionName = NULL, serverIP = NULL WHERE uuid = '{odd}'");

        var state = (await rig.Verification.AllAsync(rig.Settings, Ct)).Single(r => r.RegionId == odd);

        Assert.Equal(("", ""), (state.Name, state.ServerIp));
        Assert.Equal(RegionStatus.NotPaired, state.Status);
    }

    [Fact]
    public async Task Moved_region_is_moved()
    {
        var rig = await StartAsync();
        var (a, b, region) = (Server(HostA, ServerKeyA), Server(HostB, "the-region-key-for-server-b-000000000000000000"), Guid.NewGuid());
        rig.Settings = rig.Settings with { Money = rig.Settings.Money with { PairedServers = [a, b] } };
        await rig.Db.AddRegion(region, HostA);
        Assert.Equal(RegionStatus.NoGridGateMoney, await StatusOf(rig, region)); // pinned to A

        await rig.Db.Sql($"UPDATE `robust`.`regions` SET serverIP = '{HostB}' WHERE uuid = '{region}'");

        var state = (await rig.Verification.AllAsync(rig.Settings, Ct)).Single(r => r.RegionId == region);
        Assert.Equal(RegionStatus.Moved, state.Status);
        Assert.Equal("server:" + a.Id.ToString("N"), state.PinnedKeyId);
        Assert.Equal("server:" + b.Id.ToString("N"), state.KeyId);
        Assert.Null((await rig.Verification.AllAsync(rig.Settings, Ct)).Single(r => r.RegionId == Here).PinnedKeyId);
    }

    [Fact]
    public async Task Health_cannot_claim_regions_the_key_doesnt_cover()
    {
        var rig = await StartAsync();

        await rig.Post("health", new { protocol = 2, regions = new[] { Here, There, Guid.NewGuid() } });

        Assert.Equal(Here.ToString(), await rig.Db.Text("SELECT GROUP_CONCAT(region_id) FROM `money`.`region_heard` WHERE key_id = 'p1'"));
        Assert.Equal(RegionStatus.NoGridGateMoney, await StatusOf(rig, There));
    }

    [Fact]
    public async Task Programs_sharing_a_key_each_keep_their_regions_verified()
    {
        // A Region Server runs two programs, each with its own region, and both use the server's one key. Each program's
        // health call lists only its own region; neither takes the other's off the verified list.
        var rig = await StartAsync();
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        var server = new RegionKeyGrant("server:" + Guid.NewGuid().ToString("N"), new HashSet<Guid> { first, second }, HostA, 9000, 9100);

        await rig.Verification.HeardAsync(server.KeyId, RegionVerification.HandoffProtocol, [first], Ct);
        await rig.Verification.HeardAsync(server.KeyId, RegionVerification.HandoffProtocol, [second], Ct);
        rig.VerifyClock.Advance(TimeSpan.FromMinutes(3));
        await rig.Verification.HeardAsync(server.KeyId, RegionVerification.HandoffProtocol, [first], Ct);

        Assert.True(await rig.Verification.IsVerifiedAsync(server, first, Ct));
        Assert.True(await rig.Verification.IsVerifiedAsync(server, second, Ct));
        // Each region keeps its own time: the program that stopped calling loses only its own region.
        rig.VerifyClock.Advance(TimeSpan.FromMinutes(8));
        Assert.True(await rig.Verification.IsVerifiedAsync(server, first, Ct));
        Assert.False(await rig.Verification.IsVerifiedAsync(server, second, Ct));
    }

    [Fact]
    public async Task Pairing_check_with_empty_body_still_answers_ok()
    {
        var rig = await StartAsync();

        var answer = await rig.Post("health", new { });

        Assert.Equal(HttpStatusCode.OK, answer.Status);
        Assert.True((bool)answer.Body!["ok"]!);
        Assert.Equal(1, answer.Body["regions"]!.GetValue<int>());
    }

    [Fact]
    public async Task Pairing_check_does_not_overwrite_a_modules_record()
    {
        var rig = await StartAsync();
        await rig.Post("health", new { protocol = 2, regions = new[] { Here } });

        await rig.Post("health", new { });

        Assert.True(await rig.Verification.IsVerifiedAsync(GrantOf(rig, "p1"), Here, Ct));
        Assert.Equal(RegionStatus.Verified, await StatusOf(rig, Here));
        // A server whose only call is the pairing check has no module: stock OpenSim.
        Assert.Equal(RegionStatus.NoGridGateMoney, await StatusOf(rig, There)); // p2 never called
        await rig.Post("health", new { }, Key2);
        Assert.Equal(RegionStatus.NoGridGateMoney, await StatusOf(rig, There));
        Assert.Equal(0, await rig.Db.Scalar("SELECT COUNT(*) FROM `money`.`key_heard` WHERE key_id = 'p2'"));
    }

    [Fact]
    public async Task Each_keys_last_health_call_says_its_protocol_and_status()
    {
        var rig = await StartAsync();
        await rig.Post("health", new { protocol = 2, regions = new[] { Here } });
        await rig.Post("health", new { regions = new[] { There } }, Key2);

        var keys = await rig.Verification.KeysAsync(Ct);
        Assert.Equal((2, RegionStatus.Verified), (keys["p1"].Protocol, keys["p1"].Status));
        Assert.Equal((1, RegionStatus.NeedsUpdate), (keys["p2"].Protocol, keys["p2"].Status));
        Assert.Equal(DateTimeKind.Utc, keys["p1"].At.Kind);

        rig.VerifyClock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(RegionStatus.NotHeardFrom, (await rig.Verification.KeysAsync(Ct))["p1"].Status);
    }
}
