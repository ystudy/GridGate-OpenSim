using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace GridGate.Launcher.Core.Dashboard;

/// <summary>The front page's resident numbers; each is null when it couldn't be counted.</summary>
/// <param name="Registered">Residents with an account here.</param>
/// <param name="ActiveSince">Residents who signed in on or after the asked time.</param>
public sealed record ResidentCounts(long? Registered, long? ActiveSince)
{
    public static readonly ResidentCounts Unknown = new(null, null);
}

/// <summary>Counts the front page's resident numbers; the host can swap in a test double.</summary>
public interface IResidentCounter
{
    /// <param name="activeSince">Residents who signed in on or after this time count as active.</param>
    Task<ResidentCounts> CountAsync(DateTimeOffset activeSince, CancellationToken ct);
}

/// <summary>
/// The front page's two resident counts (spec 2026-11-19-front-page-design §2): read-only queries as robustuser, the same
/// "who is a person" rule as <see cref="WorldCounter"/>, a short timeout each, and nothing asked where the accounts live on
/// another grid (no staff pages). Its own class rather than the dashboard's counter, so public visits run two queries, not
/// the whole dashboard.
/// </summary>
public sealed class ResidentCounter(Func<LauncherSettings> settings, ILogger<ResidentCounter>? log = null) : IResidentCounter
{
    private const uint TimeoutSeconds = 3;

    public async Task<ResidentCounts> CountAsync(DateTimeOffset activeSince, CancellationToken ct)
    {
        var s = settings();
        if (!AdminRule.HasStaffPages(s)) return ResidentCounts.Unknown;
        var since = activeSince.ToUnixTimeSeconds();
        var registered = await ScalarAsync(s, $"SELECT COUNT(*) FROM `UserAccounts` u WHERE {WorldCounter.Person}", since, ct);
        // Login is Unix seconds as text; joined to UserAccounts so hypergrid visitors aren't counted.
        var active = await ScalarAsync(s,
            $"SELECT COALESCE(SUM(CAST(g.`Login` AS UNSIGNED) >= @since), 0) FROM `GridUser` g JOIN `UserAccounts` u ON u.`PrincipalID` = g.`UserID` WHERE {WorldCounter.Person}",
            since, ct);
        return new ResidentCounts(registered, active);
    }

    private async Task<long?> ScalarAsync(LauncherSettings s, string sql, long since, CancellationToken ct)
    {
        try
        {
            await using var conn = await GridDb.OpenAsync(MariaDbServer.ConnectionString(s, "robust", "robustuser", s.Db.RobustPassword, TimeoutSeconds), ct);
            await using var cmd = new MySqlCommand(sql, conn) { CommandTimeout = (int)TimeoutSeconds };
            cmd.Parameters.AddWithValue("@since", since);
            var value = await cmd.ExecuteScalarAsync(ct);
            return value is null or DBNull ? 0 : Convert.ToInt64(value);
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            // A database that is down or a table not made yet: expected; the front page hides the number.
            log?.LogDebug("A front page count could not be read ({Error}: {Message}).", e.GetType().Name, e.Message);
            return null;
        }
    }
}
