using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Money;
using MySqlConnector;
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
        Assert.Equal("3", await db.Text("SELECT value FROM `money`.`state` WHERE name = 'schema_version'"));
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
        Assert.Equal("3", await db.Text("SELECT value FROM `money`.`state` WHERE name = 'schema_version'"));
        Assert.Equal(3, await db.Scalar("SELECT COUNT(*) FROM `money`.`accounts`"));
    }

    private static readonly string[] V3Tables =
        ["session_locations", "handoffs", "region_pins", "key_heard", "limit_overrides", "alerts", "key_locks"];

    [Fact]
    public async Task Fresh_schema_is_version_3_with_the_new_tables()
    {
        var db = await maria.FreshAsync();

        await db.EnsureSchemaAsync();

        Assert.Equal("3", await db.Text("SELECT value FROM `money`.`state` WHERE name = 'schema_version'"));
        foreach (var table in V3Tables)
            Assert.Equal(1, await db.Scalar($"SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'money' AND TABLE_NAME = '{table}'"));
        foreach (var table in new[] { "entries", "holds" })
            Assert.Equal("varchar(64)", await db.Text($"SELECT COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'money' AND TABLE_NAME = '{table}' AND COLUMN_NAME = 'key_id'"));
        Assert.Equal(3, await db.Scalar("SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = 'money' AND TABLE_NAME = 'entries' AND INDEX_NAME = 'key_type_at'"));
    }

    [Fact]
    public async Task Version_2_database_moves_to_3_and_keeps_entries()
    {
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();
        await Seed(db);
        // Make it a version 2 database: no key_id, no new tables.
        await db.Sql("""
            ALTER TABLE `money`.`entries` DROP INDEX key_type_at;
            ALTER TABLE `money`.`entries` DROP COLUMN key_id;
            ALTER TABLE `money`.`holds` DROP COLUMN key_id;
            DROP TABLE `money`.`session_locations`, `money`.`handoffs`, `money`.`region_pins`, `money`.`key_heard`,
              `money`.`limit_overrides`, `money`.`alerts`, `money`.`key_locks`;
            UPDATE `money`.`state` SET value = '2' WHERE name = 'schema_version';
            """);

        await db.EnsureSchemaAsync();

        Assert.Equal("3", await db.Text("SELECT value FROM `money`.`state` WHERE name = 'schema_version'"));
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries`"));
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries` WHERE key_id IS NULL"));
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `money`.`holds` WHERE key_id IS NULL"));
        foreach (var table in V3Tables)
            Assert.Equal(1, await db.Scalar($"SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'money' AND TABLE_NAME = '{table}'"));
        Assert.Equal(3, await db.Scalar("SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = 'money' AND TABLE_NAME = 'entries' AND INDEX_NAME = 'key_type_at'"));
    }

    [Fact]
    public async Task Moneyuser_can_delete_old_locations_but_not_entries()
    {
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();
        await Seed(db);
        await AsMoneyuser(db, $"INSERT INTO `money`.`session_locations` (session_id, agent_id, state, updated) VALUES ('{Guid.NewGuid()}', '{Guid.NewGuid()}', 'in', UTC_TIMESTAMP(6))");

        await AsMoneyuser(db, "DELETE FROM `money`.`session_locations` WHERE updated < UTC_TIMESTAMP(6)");

        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`session_locations`"));
        var e = await Assert.ThrowsAsync<MySqlException>(() => AsMoneyuser(db, "DELETE FROM `money`.`entries`"));
        Assert.Contains("denied", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries`"));
    }

    [Fact]
    public async Task Moneyuser_can_open_the_money_database()
    {
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();

        await using var conn = await db.OpenMoney(Ct);

        Assert.Equal("money", conn.Database);
    }

    private static async Task AsMoneyuser(MoneyTestDatabase db, string sql)
    {
        await using var conn = await db.OpenMoney(Ct);
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static Task Seed(MoneyTestDatabase db) => db.Sql("""
        INSERT INTO `money`.`entries` (at, type, from_id, to_id, amount, from_after, to_after) VALUES (UTC_TIMESTAMP(6), 'fee_moved', 2, 1, 1, -1, 1);
        INSERT INTO `money`.`holds` (buyer_id, seller_id, amount, kind, created, state) VALUES (1, 2, 1, 'object', UTC_TIMESTAMP(6), 'open');
        """);

    [Theory]
    [InlineData("UPDATE `money`.`entries` SET amount = 5")]
    [InlineData("DELETE FROM `money`.`entries`")]
    public async Task Entries_are_never_edited_or_deleted_even_by_root(string sql)
    {
        // Money design §7.1; money review 2026-11-15, L-9.
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();
        await Seed(db);

        await Assert.ThrowsAsync<MySqlException>(() => db.Sql(sql));
        Assert.Equal(1, await db.Scalar("SELECT amount FROM `money`.`entries`"));
    }

    [Theory]
    [InlineData("DELETE FROM `money`.`accounts` WHERE id = 3")]
    [InlineData("UPDATE `money`.`accounts` SET kind = 'Bank' WHERE id = 2")]
    [InlineData("UPDATE `money`.`holds` SET amount = 1000")]
    [InlineData("UPDATE `money`.`holds` SET seller_id = 3")]
    [InlineData("DELETE FROM `money`.`holds`")]
    [InlineData("DELETE FROM `money`.`requests`")]
    [InlineData("DROP TRIGGER `money`.`entries_never_edited`")]
    [InlineData("ALTER TABLE `money`.`entries` DROP COLUMN reason")]
    [InlineData("DROP TABLE `money`.`entries`")]
    [InlineData("CREATE TABLE `money`.`extra` (id INT)")]
    public async Task Moneyuser_may_not_rewrite_history(string sql)
    {
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();
        await Seed(db);

        var e = await Assert.ThrowsAsync<MySqlException>(() => AsMoneyuser(db, sql));
        Assert.Contains("denied", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("UPDATE `money`.`accounts` SET balance = balance, frozen = 0, payouts_allowed = 0, last_allowance = NULL WHERE id = 3")]
    [InlineData("UPDATE `money`.`holds` SET state = 'returned', settled = UTC_TIMESTAMP(6)")]
    [InlineData("UPDATE `money`.`state` SET value = '0' WHERE name = 'frozen'")]
    [InlineData("INSERT INTO `money`.`requests` (txn_id, op, fingerprint, response, at) VALUES (UUID(), 'x', 'f', 'r', UTC_TIMESTAMP(6))")]
    [InlineData("SELECT COUNT(*) FROM `money`.`entries` WHERE hold_id IS NULL")]
    public async Task Moneyuser_may_still_do_what_the_ledger_does(string sql)
    {
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();
        await Seed(db);

        await AsMoneyuser(db, sql);
    }

    [Fact]
    public async Task An_earlier_grant_of_everything_is_taken_back()
    {
        // Before L-9 the provisioner granted moneyuser ALL on money.*.
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();
        await db.Sql("GRANT ALL ON `money`.* TO 'moneyuser'@'localhost'; GRANT ALL ON `money`.* TO 'moneyuser'@'127.0.0.1';");
        await Seed(db);

        await new DatabaseProvisioner(db.Settings).EnsureAsync(Ct);
        await db.EnsureSchemaAsync();

        await Assert.ThrowsAsync<MySqlException>(() => AsMoneyuser(db, "DELETE FROM `money`.`holds`"));
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `money`.`holds`"));
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
