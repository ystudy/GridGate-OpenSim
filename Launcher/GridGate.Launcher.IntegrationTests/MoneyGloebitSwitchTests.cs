using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Money;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// GridGate coins and Gloebit side by side (money design §10, money plan 4, task 4): while the grid runs Gloebit, the coin
/// books are kept and nothing touches them; switching back finds every balance and entry as it was.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class MoneyGloebitSwitchTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly StaffAction Staff = new("Test Owner", "testing");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<string?> Books(MoneyTestDatabase db) => db.Text(
        "SELECT CONCAT((SELECT GROUP_CONCAT(CONCAT(id, ':', balance, ':', frozen) ORDER BY id) FROM `money`.`accounts`), '|', " +
        "(SELECT GROUP_CONCAT(CONCAT(id, ':', type, ':', amount) ORDER BY id) FROM `money`.`entries`), '|', " +
        "(SELECT GROUP_CONCAT(CONCAT(id, ':', state) ORDER BY id) FROM `money`.`holds`))");

    [Fact]
    public async Task Switching_to_gloebit_and_back_keeps_the_books()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.TransferAsync(new TransferRequest(Guid.NewGuid(), EntryType.PayAvatar, AccountRef.Resident(A), AccountRef.Resident(B), 30), Ct);
        await ledger.GiveAsync(AccountRef.Resident(B), 5, Staff, Ct);
        // A purchase still waiting for delivery when the owner switches: in Gloebit mode nothing may settle it.
        var hold = await ledger.HoldAsync(new HoldRequest(Guid.NewGuid(), "object", AccountRef.Resident(A), AccountRef.Resident(B), 10, null, null, null), Ct);
        var before = await Books(db);

        var settings = TestSettingsFor(MoneyMode.Gloebit);
        var gate = new MoneyGate(() => settings, MoneyGate.Open);
        Assert.Equal(MoneyMode.Off, gate.ServiceMode());

        // Gloebit mode: the start-up, the service and the maintenance all see Off.
        await MoneyStartup.TryPrepareAsync(db.Settings, f => f(db.Settings), gate.ServiceMode(), NullLogger.Instance, Ct);
        using (var maintenance = new MoneyMaintenance(ledger, gate.ServiceMode, time, NullLogger<MoneyMaintenance>.Instance))
        {
            await maintenance.StartAsync(Ct);
            for (var i = 0; i < 20; i++)
            {
                time.Advance(MoneyMaintenance.SweepEvery);
                await Task.Delay(20, Ct);
            }
            await maintenance.StopAsync(Ct);
        }
        Assert.Equal(before, await Books(db));

        // Back to Play: everything is where it was, the books add up, and the waiting purchase goes back to the buyer.
        settings = TestSettingsFor(MoneyMode.Play);
        Assert.Equal(MoneyMode.Play, gate.ServiceMode());
        Assert.True((await ledger.SelfCheckAsync(Ct)).Ok);
        Assert.Equal(1, await ledger.ReturnExpiredHoldsAsync(Ct));
        Assert.Equal("returned", await db.Text($"SELECT state FROM `money`.`holds` WHERE id = {hold.HoldId}"));
    }

    private static LauncherSettings TestSettingsFor(MoneyMode mode) => new()
    {
        Db = new DatabaseSecrets("r", "b", "o"),
        Console = new ConsoleSecrets("u", "p"),
        MachineId = "gas",
        Plan = OwnerPlan.GridAsset,
        Money = new MoneySettings(Mode: mode, Gloebit: new GloebitSettings("sandbox", "0b9e3c2a-1111-4c5d-8e9f-0123456789ab", "7d3f9a10-2222-4b6c-9d8e-fedcba987654", "Owner")),
    };
}
