using GridGate.Launcher.Core.Settings;
using MySqlConnector;

namespace GridGate.Launcher.Core.Database;

/// <summary>Creates the robust and opensim databases and their users, and money's once it has a password; safe to run repeatedly.</summary>
public sealed class DatabaseProvisioner(LauncherSettings s)
{
    /// <summary>The hosts each database user is created for.</summary>
    public static readonly IReadOnlyList<string> Hosts = ["localhost", "127.0.0.1"];

    private const int ErNoSuchGrant = 1141;

    public async Task EnsureAsync(CancellationToken ct)
    {
        await using var conn = new MySqlConnection(MariaDbServer.ConnectionString(s));
        await conn.OpenAsync(ct);

        await Provision(conn, "robust", "robustuser", s.Db.RobustPassword, grantAll: true, ct);
        await Provision(conn, "opensim", "opensimuser", s.Db.OpensimPassword, grantAll: true, ct);
        // Money's database exists only once money has been turned on and given its password (money design §7.1). Its user
        // gets no database-wide grant: MoneySchema grants it only what the ledger does, table by table, once the tables
        // exist (money review 2026-11-15, L-9). An earlier version's ALL is taken back.
        if (s.Db.MoneyPassword != "")
            await Provision(conn, "money", "moneyuser", s.Db.MoneyPassword, grantAll: false, ct);
    }

    private static async Task Provision(MySqlConnection conn, string database, string user, string password, bool grantAll, CancellationToken ct)
    {
        var pw = MySqlHelper.EscapeString(password);
        await Run(conn, $"CREATE DATABASE IF NOT EXISTS `{database}` CHARACTER SET utf8mb4", ct);
        foreach (var host in Hosts)
        {
            await Run(conn, $"CREATE USER IF NOT EXISTS '{user}'@'{host}' IDENTIFIED BY '{pw}'", ct);
            await Run(conn, $"ALTER USER '{user}'@'{host}' IDENTIFIED BY '{pw}'", ct);
            if (grantAll)
                await Run(conn, $"GRANT ALL ON `{database}`.* TO '{user}'@'{host}'", ct);
            else
                try
                {
                    await Run(conn, $"REVOKE ALL PRIVILEGES ON `{database}`.* FROM '{user}'@'{host}'", ct);
                }
                catch (MySqlException e) when (e.Number == ErNoSuchGrant)
                {
                    // Nothing database-wide to take back.
                }
        }
    }

    private static async Task Run(MySqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
