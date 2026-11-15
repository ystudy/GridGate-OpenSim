using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Money;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

[Collection(GridCollection.Name)]
public sealed class MoneySchemaTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ensure_twice_is_harmless()
    {
        var db = await maria.FreshAsync();

        await db.EnsureSchemaAsync();
        await db.EnsureSchemaAsync();

        Assert.Equal(3, await db.Scalar("SELECT COUNT(*) FROM `money`.`accounts`"));
        Assert.Equal("2", await db.Text("SELECT value FROM `money`.`state` WHERE name = 'schema_version'"));
        Assert.Equal("0", await db.Text("SELECT value FROM `money`.`state` WHERE name = 'frozen'"));
        Assert.Equal("system:bank", await db.Text("SELECT home FROM `money`.`accounts` WHERE id = 1"));
        Assert.Equal("system:fees", await db.Text("SELECT home FROM `money`.`accounts` WHERE id = 2"));
        Assert.Equal("system:holding", await db.Text("SELECT home FROM `money`.`accounts` WHERE id = 3"));
        Assert.Equal("Bank", await db.Text("SELECT kind FROM `money`.`accounts` WHERE id = 1"));
    }

    private static Task<string?> HomeCollation(MoneyTestDatabase db) =>
        db.Text("SELECT COLLATION_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'money' AND TABLE_NAME = 'accounts' AND COLUMN_NAME = 'home'");

    [Fact]
    public async Task Homes_are_compared_byte_for_byte()
    {
        // utf8mb4's default collation ignores case and accents, so othér.example found other.example's wallet (L-2).
        var db = await maria.FreshAsync();

        await db.EnsureSchemaAsync();

        Assert.Equal("utf8mb4_bin", await HomeCollation(db));
    }

    [Fact]
    public async Task A_version_1_database_moves_to_byte_for_byte_homes()
    {
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();
        await db.Sql("ALTER TABLE `money`.`accounts` MODIFY home VARCHAR(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci NOT NULL DEFAULT ''");
        await db.Sql("UPDATE `money`.`state` SET value = '1' WHERE name = 'schema_version'");
        Assert.Equal("utf8mb4_general_ci", await HomeCollation(db));

        await db.EnsureSchemaAsync();

        Assert.Equal("utf8mb4_bin", await HomeCollation(db));
        Assert.Equal("2", await db.Text("SELECT value FROM `money`.`state` WHERE name = 'schema_version'"));
        Assert.Equal(3, await db.Scalar("SELECT COUNT(*) FROM `money`.`accounts`"));
    }

    [Fact]
    public async Task Moneyuser_can_open_the_money_database()
    {
        var db = await maria.FreshAsync();

        await using var conn = await db.OpenMoney(Ct);

        Assert.Equal("money", conn.Database);
    }

    [Fact]
    public async Task Provisioner_creates_money_only_with_a_password()
    {
        var db = await maria.FreshAsync();
        await db.Sql("DROP DATABASE `money`");

        await new DatabaseProvisioner(db.Settings with { Db = db.Settings.Db with { MoneyPassword = "" } }).EnsureAsync(Ct);
        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = 'money'"));

        await new DatabaseProvisioner(db.Settings).EnsureAsync(Ct);
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = 'money'"));
    }
}
