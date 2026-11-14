using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>How the ledger compares a visitor's home grid address: trimmed, without a trailing slash, lower case.</summary>
public static class MoneyHomes
{
    public static string Normal(string? home) => (home ?? "").Trim().TrimEnd('/').ToLowerInvariant();
}

/// <summary>A hypergrid visitor as the Gatekeeper recorded them: their id, home grid address and name.</summary>
public sealed record VisitorInfo(Guid Id, string Home, string Name);

/// <summary>The hypergrid visitors this grid has let in, for the Money pages (money plan 3). A seam so pages can be tested.</summary>
public interface IVisitorDirectory
{
    /// <summary>Up to <paramref name="max"/> (1 to 100) visitors whose name contains <paramref name="text"/>, newest first.</summary>
    Task<IReadOnlyList<VisitorInfo>> SearchAsync(string text, int max, CancellationToken ct);

    /// <summary>The visitor with this id and home (compared as the ledger compares homes), or null.</summary>
    Task<VisitorInfo?> FindAsync(Guid id, string home, CancellationToken ct);
}

/// <summary>
/// Reads Robust's <c>GridUser</c> table, where the Gatekeeper writes <c>"uuid;HomeURI;First Last"</c> for every visitor
/// (GatekeeperService.LoginAgent). Residents' rows (a plain uuid) are never visitors. A database failure is
/// <see cref="MoneyUnavailableException"/>.
/// </summary>
public sealed class VisitorDirectory(Func<CancellationToken, Task<MySqlConnection>> openRobust) : IVisitorDirectory
{
    public async Task<IReadOnlyList<VisitorInfo>> SearchAsync(string text, int max, CancellationToken ct)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return [];
        var escaped = t.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        // The name is the third part; ';' never appears in a uuid or a home address, so '%;%;%name%' finds it there.
        return await QueryAsync(
            "SELECT UserID FROM GridUser WHERE UserID LIKE CONCAT('%;%;%', @t, '%') ORDER BY CAST(Login AS UNSIGNED) DESC LIMIT @max",
            [("@t", escaped), ("@max", Math.Clamp(max, 1, 100))], v => v.Name.Contains(t, StringComparison.OrdinalIgnoreCase), ct);
    }

    public async Task<VisitorInfo?> FindAsync(Guid id, string home, CancellationToken ct)
    {
        var wanted = MoneyHomes.Normal(home);
        if (id == Guid.Empty || wanted.Length == 0) return null;
        var rows = await QueryAsync("SELECT UserID FROM GridUser WHERE UserID LIKE @prefix", [("@prefix", id.ToString() + ";%")],
            v => MoneyHomes.Normal(v.Home) == wanted, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    private async Task<IReadOnlyList<VisitorInfo>> QueryAsync(string sql, (string Name, object Value)[] args, Func<VisitorInfo, bool> keep, CancellationToken ct)
    {
        try
        {
            await using var conn = await openRobust(ct);
            await using var cmd = new MySqlCommand(sql, conn);
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            List<VisitorInfo> found = [];
            while (await r.ReadAsync(ct))
                if (Parse(r.GetString(0)) is { } v && keep(v))
                    found.Add(v);
            return found;
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
    }

    /// <summary>A Gatekeeper row "uuid;home;First Last", or null for anything else (a resident's plain uuid).</summary>
    internal static VisitorInfo? Parse(string userId)
    {
        var parts = userId.Split(';');
        if (parts.Length < 3 || !Guid.TryParse(parts[0], out var id) || parts[1].Trim().Length == 0) return null;
        return new VisitorInfo(id, parts[1].Trim(), parts[2].Trim());
    }
}
