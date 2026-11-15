using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>
/// Which regions Robust lists at an address (money plan 3): a paired Region Server's key covers the regions its programs
/// registered with that address as their <c>serverIP</c> (their ExternalHostName). A database failure is
/// <see cref="MoneyUnavailableException"/>.
/// </summary>
public sealed class RegionHosts(Func<CancellationToken, Task<MySqlConnection>> openRobust)
{
    public async Task<IReadOnlySet<Guid>> RegionsAtAsync(string host, CancellationToken ct)
    {
        try
        {
            await using var conn = await openRobust(ct);
            await using var cmd = new MySqlCommand("SELECT uuid FROM regions WHERE serverIP = @host", conn);
            cmd.Parameters.AddWithValue("@host", host);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var found = new HashSet<Guid>();
            while (await r.ReadAsync(ct))
                if (Guid.TryParse(r.GetValue(0)?.ToString(), out var id))
                    found.Add(id);
            return found;
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
    }
}
