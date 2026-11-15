using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>What Robust's Presence table says about a session.</summary>
public enum PresenceOutcome { Ok, NoSession, WrongRegion, Unavailable }

/// <summary>The economy helper's check of a viewer's secure session (money design §8.5); a seam so tests can fake it.</summary>
public interface ISecureSessionCheck
{
    Task<PresenceOutcome> HasSecureSessionAsync(Guid agentId, Guid secureSessionId, CancellationToken ct);
}

/// <summary>
/// Checks an avatar's session against Robust's <c>Presence</c> table (money design §7.2). Residents and hypergrid visitors
/// both have a row: Robust's login writes one, and so does the Gatekeeper for a visitor (GatekeeperService.LoginAgent).
/// </summary>
public sealed class PresenceCheck(Func<CancellationToken, Task<MySqlConnection>> openRobust) : ISecureSessionCheck
{
    /// <summary>
    /// Ok when <paramref name="agentId"/> is logged in with both session ids and is now in one of <paramref name="regions"/>.
    /// </summary>
    public async Task<PresenceOutcome> CheckAsync(Guid agentId, Guid sessionId, Guid secureSessionId, IReadOnlySet<Guid> regions, CancellationToken ct)
    {
        if (agentId == Guid.Empty || sessionId == Guid.Empty || secureSessionId == Guid.Empty)
            return PresenceOutcome.NoSession;
        try
        {
            await using var conn = await openRobust(ct);
            await using var cmd = new MySqlCommand(
                "SELECT RegionID FROM Presence WHERE UserID = @u AND SessionID = @s AND SecureSessionID = @ss", conn);
            cmd.Parameters.AddWithValue("@u", agentId.ToString());
            cmd.Parameters.AddWithValue("@s", sessionId.ToString());
            cmd.Parameters.AddWithValue("@ss", secureSessionId.ToString());
            return await cmd.ExecuteScalarAsync(ct) switch
            {
                Guid region => regions.Contains(region) ? PresenceOutcome.Ok : PresenceOutcome.WrongRegion,
                string text when Guid.TryParse(text, out var region) => regions.Contains(region) ? PresenceOutcome.Ok : PresenceOutcome.WrongRegion,
                _ => PresenceOutcome.NoSession,
            };
        }
        catch (MySqlException)
        {
            return PresenceOutcome.Unavailable;
        }
    }

    /// <summary>
    /// Ok when <paramref name="agentId"/> is logged in with <paramref name="secureSessionId"/>, in any region. The economy
    /// helper's check (money design §8.5): the viewer sends only the secure session id, and the helper has no region.
    /// </summary>
    public async Task<PresenceOutcome> HasSecureSessionAsync(Guid agentId, Guid secureSessionId, CancellationToken ct)
    {
        if (agentId == Guid.Empty || secureSessionId == Guid.Empty) return PresenceOutcome.NoSession;
        try
        {
            await using var conn = await openRobust(ct);
            await using var cmd = new MySqlCommand("SELECT 1 FROM Presence WHERE UserID = @u AND SecureSessionID = @ss", conn);
            cmd.Parameters.AddWithValue("@u", agentId.ToString());
            cmd.Parameters.AddWithValue("@ss", secureSessionId.ToString());
            return await cmd.ExecuteScalarAsync(ct) is null ? PresenceOutcome.NoSession : PresenceOutcome.Ok;
        }
        catch (MySqlException)
        {
            return PresenceOutcome.Unavailable;
        }
    }

    /// <summary>
    /// Whether the Gatekeeper let <paramref name="id"/> in from <paramref name="home"/>: it writes a <c>GridUser</c> row
    /// <c>"uuid;HomeURI;First Last"</c> for every visitor (GatekeeperService.LoginAgent). Homes are compared as the ledger
    /// compares them. An empty home is false without a query; a database failure throws.
    /// </summary>
    public async Task<bool> IsKnownVisitorAsync(Guid id, string home, CancellationToken ct)
    {
        var wanted = MoneyHomes.Normal(home);
        if (id == Guid.Empty || string.IsNullOrEmpty(wanted)) return false;
        await using var conn = await openRobust(ct);
        await using var cmd = new MySqlCommand("SELECT UserID FROM GridUser WHERE UserID LIKE @prefix", conn);
        cmd.Parameters.AddWithValue("@prefix", id.ToString() + ";%");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var parts = r.GetString(0).Split(';');
            if (parts.Length >= 2 && MoneyHomes.Normal(parts[1]) == wanted) return true;
        }
        return false;
    }

    /// <summary>Whether <paramref name="id"/> is a local account in Robust's <c>UserAccounts</c>. A database failure throws.</summary>
    public async Task<bool> IsLocalUserAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await openRobust(ct);
        await using var cmd = new MySqlCommand("SELECT 1 FROM UserAccounts WHERE PrincipalID = @id", conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
