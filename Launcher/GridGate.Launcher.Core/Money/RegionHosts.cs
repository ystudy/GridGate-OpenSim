using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>
/// Which regions Robust lists at an address (money plan 3): a paired Region Server's key covers the regions its programs
/// registered with that address as their <c>serverIP</c> (their ExternalHostName) and a UDP port (<c>serverPort</c>) in its
/// range. <see cref="IsAt"/> is that one comparison; the money service also uses it to decide whether a start or a handoff
/// went to a paired server, so "this server covers the region" and "the event went to this server" never disagree. A
/// database failure is <see cref="MoneyUnavailableException"/>.
/// </summary>
public sealed class RegionHosts(Func<CancellationToken, Task<MySqlConnection>> openRobust)
{
    public async Task<IReadOnlySet<Guid>> RegionsAtAsync(string host, int fromPort, int toPort, CancellationToken ct)
    {
        try
        {
            await using var conn = await openRobust(ct);
            // The port range narrows the rows; IsAt alone decides, so the database's own text comparison never does.
            await using var cmd = new MySqlCommand(
                "SELECT uuid, serverIP, serverPort FROM regions WHERE serverPort >= @from AND serverPort < @to", conn);
            cmd.Parameters.AddWithValue("@from", fromPort);
            cmd.Parameters.AddWithValue("@to", toPort);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var found = new HashSet<Guid>();
            while (await r.ReadAsync(ct))
                if (Guid.TryParse(r.GetValue(0)?.ToString(), out var id) && IsAt(Text(r, 1), Port(r, 2), host, fromPort, toPort))
                    found.Add(id);
            return found;
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
    }

    /// <summary>
    /// True when a region listed at <paramref name="regionHost"/> and UDP port <paramref name="regionPort"/> is at the paired
    /// server <paramref name="host"/> with ports from <paramref name="fromPort"/> up to, not including,
    /// <paramref name="toPort"/>. Hosts compare as text, ignoring case. A missing host or port is at no server.
    /// </summary>
    public static bool IsAt(string? regionHost, long? regionPort, string host, int fromPort, int toPort) =>
        regionHost is not null && regionPort is { } port && string.Equals(regionHost, host, StringComparison.OrdinalIgnoreCase)
        && port >= fromPort && port < toPort;

    /// <summary>
    /// Where Robust lists <paramref name="regionId"/> now, over <paramref name="robust"/>: its <c>serverIP</c> and
    /// <c>serverPort</c>, or nulls when it isn't listed. A database failure is thrown as is.
    /// </summary>
    public static async Task<(string? Host, long? Port)> AddressOfAsync(MySqlConnection robust, Guid regionId, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("SELECT serverIP, serverPort FROM regions WHERE uuid = @r", robust);
        cmd.Parameters.AddWithValue("@r", regionId.ToString());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (Text(r, 0), Port(r, 1)) : (null, null);
    }

    private static string? Text(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetValue(i).ToString();

    private static long? Port(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i));
}
