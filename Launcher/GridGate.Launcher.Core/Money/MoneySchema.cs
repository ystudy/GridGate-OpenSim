using GridGate.Launcher.Core.Database;
using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>What an account is (money design §7.1); stored by name.</summary>
public enum AccountKind { Person, Visitor, Bank, Fees, Holding }

/// <summary>
/// The money database's tables and system accounts (money design §7.1). Safe to run on every start, as root. Columns the
/// plan pins: accounts, entries (never edited or deleted), holds, requests (one per transaction id, for retries) and state.
/// Root creates them; moneyuser gets only what the ledger does (money review 2026-11-15, L-9).
/// </summary>
public static class MoneySchema
{
    /// <summary>2: homes compared byte for byte (money review 2026-11-15, L-2).</summary>
    public const int Version = 2;

    /// <summary>The system accounts' fixed ids.</summary>
    public const long BankId = 1, FeesId = 2, HoldingId = 3;

    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS accounts (
          id BIGINT AUTO_INCREMENT PRIMARY KEY, agent_id CHAR(36) NOT NULL,
          home VARCHAR(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL DEFAULT '',
          kind VARCHAR(16) NOT NULL, balance BIGINT NOT NULL DEFAULT 0, frozen TINYINT NOT NULL DEFAULT 0,
          payouts_allowed TINYINT NOT NULL DEFAULT 0, last_allowance DATETIME(6) NULL, created DATETIME(6) NOT NULL,
          UNIQUE KEY agent_home (agent_id, home)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS entries (
          id BIGINT AUTO_INCREMENT PRIMARY KEY, at DATETIME(6) NOT NULL, type VARCHAR(32) NOT NULL,
          from_id BIGINT NOT NULL, to_id BIGINT NOT NULL, amount BIGINT NOT NULL, from_after BIGINT NOT NULL, to_after BIGINT NOT NULL,
          region_id CHAR(36) NULL, object_id CHAR(36) NULL, object_name VARCHAR(255) NULL, staff VARCHAR(128) NULL,
          reason VARCHAR(500) NULL, payment_ref VARCHAR(128) NULL, reverses BIGINT NULL, hold_id BIGINT NULL,
          UNIQUE KEY payment_ref (payment_ref), KEY from_at (from_id, at), KEY to_at (to_id, at), KEY type_from_at (type, from_id, at))
          ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS holds (
          id BIGINT AUTO_INCREMENT PRIMARY KEY, buyer_id BIGINT NOT NULL, seller_id BIGINT NOT NULL, amount BIGINT NOT NULL,
          kind VARCHAR(16) NOT NULL, region_id CHAR(36) NULL, object_id CHAR(36) NULL, object_name VARCHAR(255) NULL,
          created DATETIME(6) NOT NULL, state VARCHAR(16) NOT NULL, settled DATETIME(6) NULL, KEY state_created (state, created))
          ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS requests (
          txn_id CHAR(36) PRIMARY KEY, op VARCHAR(32) NOT NULL, fingerprint CHAR(64) NOT NULL, response TEXT NOT NULL, at DATETIME(6) NOT NULL)
          ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS state (name VARCHAR(64) PRIMARY KEY, value VARCHAR(500) NOT NULL) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        INSERT IGNORE INTO accounts (id, agent_id, home, kind, created) VALUES
          (1, '00000000-0000-0000-0000-000000000000', 'system:bank', 'Bank', UTC_TIMESTAMP(6)),
          (2, '00000000-0000-0000-0000-000000000000', 'system:fees', 'Fees', UTC_TIMESTAMP(6)),
          (3, '00000000-0000-0000-0000-000000000000', 'system:holding', 'Holding', UTC_TIMESTAMP(6));
        INSERT IGNORE INTO state (name, value) VALUES ('schema_version', '2'), ('frozen', '0'), ('frozen_reason', '');
        CREATE INDEX IF NOT EXISTS hold_id ON entries (hold_id);
        CREATE TRIGGER IF NOT EXISTS entries_never_edited BEFORE UPDATE ON entries FOR EACH ROW
          SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Money entries are never edited.';
        CREATE TRIGGER IF NOT EXISTS entries_never_deleted BEFORE DELETE ON entries FOR EACH ROW
          SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Money entries are never deleted.';
        """;

    /// <summary>
    /// What moneyuser may do, table by table (money review 2026-11-15, L-9): add entries and requests but never change or
    /// remove them, and change only the account and hold columns the ledger changes. A hold's amount and parties, and an
    /// account's kind, are fixed once written. No DDL: moneyuser can't drop the triggers above. A later migration that must
    /// change an entry drops and recreates them, as root.
    /// </summary>
    private static readonly string[] Grants =
    [
        "SELECT, INSERT, UPDATE (balance, frozen, payouts_allowed, last_allowance) ON accounts",
        "SELECT, INSERT ON entries",
        "SELECT, INSERT, UPDATE (state, settled) ON holds",
        "SELECT, INSERT ON requests",
        "SELECT, UPDATE (value) ON state",
    ];

    /// <summary>
    /// Version 1 to 2: <c>accounts.home</c> took utf8mb4's default collation, which ignores case and accents, so a
    /// look-alike home grid address found a real visitor's wallet. A binary collation can't make two rows collide.
    /// </summary>
    private const string To2 = """
        ALTER TABLE accounts MODIFY home VARCHAR(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL DEFAULT '';
        UPDATE state SET value = '2' WHERE name = 'schema_version';
        """;

    /// <summary>Creates or updates the tables over <paramref name="root"/>, a root connection to the money database, and grants moneyuser its rights.</summary>
    public static async Task EnsureAsync(MySqlConnection root, CancellationToken ct)
    {
        await using (var cmd = new MySqlCommand(Ddl, root))
            await cmd.ExecuteNonQueryAsync(ct);
        await using (var version = new MySqlCommand("SELECT value FROM state WHERE name = 'schema_version'", root))
            if (await version.ExecuteScalarAsync(ct) is "1")
            {
                await using var cmd = new MySqlCommand(To2, root);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        foreach (var host in DatabaseProvisioner.Hosts)
            foreach (var grant in Grants)
            {
                await using var cmd = new MySqlCommand($"GRANT {grant} TO 'moneyuser'@'{host}'", root);
                await cmd.ExecuteNonQueryAsync(ct);
            }
    }
}
