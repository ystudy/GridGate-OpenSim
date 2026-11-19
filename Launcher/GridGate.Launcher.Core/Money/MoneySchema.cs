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
    /// <summary>
    /// 6: a session's location says whether the key placed it by a match. 5: a region pin can wait for the owner to confirm it. 4: each handoff keeps the address and port Robust listed its destination at. 3: key ids on entries and holds, and
    /// the tables for locations, handoffs, pins, limits and alerts. 2: homes compared byte for byte.
    /// </summary>
    public const int Version = 6;

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
          reason VARCHAR(500) NULL, payment_ref VARCHAR(128) NULL, reverses BIGINT NULL, hold_id BIGINT NULL, key_id VARCHAR(64) NULL,
          UNIQUE KEY payment_ref (payment_ref), KEY from_at (from_id, at), KEY to_at (to_id, at), KEY type_from_at (type, from_id, at),
          KEY key_type_at (key_id, type, at))
          ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS holds (
          id BIGINT AUTO_INCREMENT PRIMARY KEY, buyer_id BIGINT NOT NULL, seller_id BIGINT NOT NULL, amount BIGINT NOT NULL,
          kind VARCHAR(16) NOT NULL, region_id CHAR(36) NULL, object_id CHAR(36) NULL, object_name VARCHAR(255) NULL,
          created DATETIME(6) NOT NULL, state VARCHAR(16) NOT NULL, settled DATETIME(6) NULL, key_id VARCHAR(64) NULL, KEY state_created (state, created))
          ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS requests (
          txn_id CHAR(36) PRIMARY KEY, op VARCHAR(32) NOT NULL, fingerprint CHAR(64) NOT NULL, response TEXT NOT NULL, at DATETIME(6) NOT NULL)
          ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS state (name VARCHAR(64) PRIMARY KEY, value VARCHAR(500) NOT NULL) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS session_locations (
          session_id CHAR(36) PRIMARY KEY, agent_id CHAR(36) NOT NULL, region_id CHAR(36) NULL, key_id VARCHAR(64) NULL,
          state VARCHAR(16) NOT NULL, last_start_id BIGINT NOT NULL DEFAULT 0, updated DATETIME(6) NOT NULL, matched TINYINT(1) NOT NULL DEFAULT 0, KEY updated (updated),
          KEY agent_updated (agent_id, updated))
          ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS handoffs (
          id CHAR(36) PRIMARY KEY, session_id CHAR(36) NOT NULL, from_key VARCHAR(64) NOT NULL, from_region CHAR(36) NOT NULL,
          to_region CHAR(36) NOT NULL, kind VARCHAR(16) NOT NULL, created DATETIME(6) NOT NULL, to_host VARCHAR(64) NULL, to_port INT NULL,
          KEY session_created (session_id, created))
          ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS region_pins (
          region_id CHAR(36) PRIMARY KEY, server_id CHAR(32) NOT NULL, machine_id VARCHAR(64) NULL, pinned DATETIME(6) NOT NULL,
          waiting TINYINT(1) NOT NULL DEFAULT 0)
          ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS key_heard (
          key_id VARCHAR(64) PRIMARY KEY, protocol INT NOT NULL, at DATETIME(6) NOT NULL)
          ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS region_heard (
          key_id VARCHAR(64) NOT NULL, region_id CHAR(36) NOT NULL, protocol INT NOT NULL, at DATETIME(6) NOT NULL,
          PRIMARY KEY (key_id, region_id))
          ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS limit_overrides (
          kind VARCHAR(8) NOT NULL, target VARCHAR(64) NOT NULL, daily_spend BIGINT NULL, daily_payout BIGINT NULL, until DATETIME(6) NULL,
          reason VARCHAR(255) NOT NULL, staff VARCHAR(100) NOT NULL, at DATETIME(6) NOT NULL, PRIMARY KEY (kind, target))
          ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS alerts (
          id BIGINT AUTO_INCREMENT PRIMARY KEY, kind VARCHAR(32) NOT NULL, level VARCHAR(8) NOT NULL, key_id VARCHAR(64) NOT NULL,
          region_id CHAR(36) NULL, agent_id CHAR(36) NULL, count INT NOT NULL, first_at DATETIME(6) NOT NULL, last_at DATETIME(6) NOT NULL,
          cleared DATETIME(6) NULL, cleared_by VARCHAR(100) NULL, other_key_id VARCHAR(64) NULL, repeated TINYINT NOT NULL DEFAULT 0,
          KEY open_last (cleared, last_at), KEY kind_key_last (kind, key_id, last_at))
          ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS key_locks (key_id VARCHAR(64) PRIMARY KEY) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS key_carry (
          old_key VARCHAR(64) PRIMARY KEY, new_key VARCHAR(64) NOT NULL, at DATETIME(6) NOT NULL)
          ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        INSERT IGNORE INTO accounts (id, agent_id, home, kind, created) VALUES
          (1, '00000000-0000-0000-0000-000000000000', 'system:bank', 'Bank', UTC_TIMESTAMP(6)),
          (2, '00000000-0000-0000-0000-000000000000', 'system:fees', 'Fees', UTC_TIMESTAMP(6)),
          (3, '00000000-0000-0000-0000-000000000000', 'system:holding', 'Holding', UTC_TIMESTAMP(6));
        INSERT IGNORE INTO state (name, value) VALUES ('schema_version', '6'), ('frozen', '0'), ('frozen_reason', '');
        CREATE INDEX IF NOT EXISTS hold_id ON entries (hold_id);
        CREATE INDEX IF NOT EXISTS requests_at ON requests (at);
        CREATE TRIGGER IF NOT EXISTS entries_never_edited BEFORE UPDATE ON entries FOR EACH ROW
          SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Money entries are never edited.';
        CREATE TRIGGER IF NOT EXISTS entries_never_deleted BEFORE DELETE ON entries FOR EACH ROW
          SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Money entries are never deleted.';
        """;

    /// <summary>
    /// What moneyuser may do, table by table (money review 2026-11-15, L-9): add entries and requests but never change or
    /// remove an entry (a request may be removed, which the ledger does once it is old), and change only the account and hold columns the ledger changes. A hold's amount and parties, and an
    /// account's kind, are fixed once written. No DDL: moneyuser can't drop the triggers above. A later migration that must
    /// change an entry drops and recreates them, as root.
    /// </summary>
    private static readonly string[] Grants =
    [
        "SELECT, INSERT, UPDATE (balance, frozen, payouts_allowed, last_allowance) ON accounts",
        "SELECT, INSERT ON entries",
        "SELECT, INSERT, UPDATE (state, settled) ON holds",
        "SELECT, INSERT, DELETE ON requests",
        "SELECT, UPDATE (value) ON state",
        "SELECT, INSERT, UPDATE, DELETE ON session_locations",
        "SELECT, INSERT, UPDATE, DELETE ON handoffs",
        "SELECT, INSERT, UPDATE, DELETE ON region_pins",
        "SELECT, INSERT, UPDATE, DELETE ON limit_overrides",
        "SELECT, INSERT, UPDATE ON key_heard",
        "SELECT, INSERT, UPDATE ON region_heard",
        "SELECT, INSERT, UPDATE ON alerts",
        "SELECT, INSERT, UPDATE ON key_locks",
        "SELECT, INSERT, UPDATE ON key_carry",
    ];

    /// <summary>
    /// Version 1 to 2: <c>accounts.home</c> took utf8mb4's default collation, which ignores case and accents, so a
    /// look-alike home grid address found a real visitor's wallet. A binary collation can't make two rows collide.
    /// </summary>
    private const string To2 = """
        ALTER TABLE accounts MODIFY home VARCHAR(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL DEFAULT '';
        UPDATE state SET value = '2' WHERE name = 'schema_version';
        """;

    /// <summary>
    /// Version 2 to 3: <c>key_id</c> on entries and holds (the new tables come from <see cref="Ddl"/>, which already has the
    /// columns for a new database). Adding a column fires no trigger, so entries stay never-edited.
    /// </summary>
    private const string To3 = """
        ALTER TABLE entries ADD COLUMN IF NOT EXISTS key_id VARCHAR(64) NULL;
        ALTER TABLE holds ADD COLUMN IF NOT EXISTS key_id VARCHAR(64) NULL;
        CREATE INDEX IF NOT EXISTS key_type_at ON entries (key_id, type, at);
        UPDATE state SET value = '3' WHERE name = 'schema_version';
        """;

    /// <summary>
    /// Version 3 to 4: <c>handoffs.to_host</c> and <c>to_port</c>, where Robust listed the destination when the handoff was
    /// written. A paired server's key counts only handoffs that went to its own address. Older handoffs have none, so they
    /// count for no paired key; they expire within minutes anyway.
    /// </summary>
    private const string To4 = """
        ALTER TABLE handoffs ADD COLUMN IF NOT EXISTS to_host VARCHAR(64) NULL, ADD COLUMN IF NOT EXISTS to_port INT NULL;
        UPDATE state SET value = '4' WHERE name = 'schema_version';
        """;

    /// <summary>
    /// Version 4 to 5: <c>region_pins.waiting</c>. A waiting pin belongs to its server but covers nothing until the owner
    /// confirms it. Every pin that exists already was confirmed or seen first, so it keeps 0.
    /// </summary>
    private const string To5 = """
        ALTER TABLE region_pins ADD COLUMN IF NOT EXISTS waiting TINYINT(1) NOT NULL DEFAULT 0;
        UPDATE state SET value = '5' WHERE name = 'schema_version';
        """;

    /// <summary>
    /// Version 5 to 6: <c>session_locations.matched</c>, whether the key placed the session by a handoff or a start, rather
    /// than by a claim nothing vouched for. Every location that exists already keeps 0: until its next placement, the key
    /// that placed it unverified can't read its balance.
    /// </summary>
    private const string To6 = """
        ALTER TABLE session_locations ADD COLUMN IF NOT EXISTS matched TINYINT(1) NOT NULL DEFAULT 0;
        UPDATE state SET value = '6' WHERE name = 'schema_version';
        """;

    /// <summary>Creates or updates the tables over <paramref name="root"/>, a root connection to the money database, and grants moneyuser its rights.</summary>
    public static async Task EnsureAsync(MySqlConnection root, CancellationToken ct)
    {
        await using (var cmd = new MySqlCommand(Ddl, root))
            await cmd.ExecuteNonQueryAsync(ct);
        string? current;
        await using (var version = new MySqlCommand("SELECT value FROM state WHERE name = 'schema_version'", root))
            current = await version.ExecuteScalarAsync(ct) as string;
        if (current == "1")
        {
            await using var cmd = new MySqlCommand(To2, root);
            await cmd.ExecuteNonQueryAsync(ct);
            current = "2";
        }
        if (current == "2")
        {
            await using var cmd = new MySqlCommand(To3, root);
            await cmd.ExecuteNonQueryAsync(ct);
            current = "3";
        }
        if (current == "3")
        {
            await using var cmd = new MySqlCommand(To4, root);
            await cmd.ExecuteNonQueryAsync(ct);
            current = "4";
        }
        if (current == "4")
        {
            await using var cmd = new MySqlCommand(To5, root);
            await cmd.ExecuteNonQueryAsync(ct);
            current = "5";
        }
        if (current == "5")
        {
            await using var cmd = new MySqlCommand(To6, root);
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
