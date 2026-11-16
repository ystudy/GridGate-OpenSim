using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>What Robust's Presence table says about a session. Where it is isn't Presence's to say.</summary>
public enum PresenceOutcome { Ok, NoSession, Unavailable }

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
    /// Ok when <paramref name="agentId"/> is logged in with both session ids, in any region. Only Robust's login and the
    /// Gatekeeper write a Presence row, so this part is trustworthy; <c>Presence.RegionID</c> isn't, since any region can
    /// rewrite it, so where the avatar is comes from the money service's own record instead.
    /// </summary>
    public Task<PresenceOutcome> LoggedInAsync(Guid agentId, Guid sessionId, Guid secureSessionId, CancellationToken ct) =>
        secureSessionId == Guid.Empty ? Task.FromResult(PresenceOutcome.NoSession)
            : HasRowAsync(agentId, sessionId, secureSessionId, ct);

    /// <summary>Ok when <paramref name="agentId"/> is logged in with <paramref name="sessionId"/>, in any region: what an arrival needs.</summary>
    public Task<PresenceOutcome> HasSessionAsync(Guid agentId, Guid sessionId, CancellationToken ct) =>
        HasRowAsync(agentId, sessionId, null, ct);

    private async Task<PresenceOutcome> HasRowAsync(Guid agentId, Guid sessionId, Guid? secureSessionId, CancellationToken ct)
    {
        if (agentId == Guid.Empty || sessionId == Guid.Empty) return PresenceOutcome.NoSession;
        try
        {
            await using var conn = await openRobust(ct);
            await using var cmd = new MySqlCommand("SELECT 1 FROM Presence WHERE UserID = @u AND SessionID = @s" +
                (secureSessionId is null ? "" : " AND SecureSessionID = @ss"), conn);
            cmd.Parameters.AddWithValue("@u", agentId.ToString());
            cmd.Parameters.AddWithValue("@s", sessionId.ToString());
            if (secureSessionId is { } secure) cmd.Parameters.AddWithValue("@ss", secure.ToString());
            return await cmd.ExecuteScalarAsync(ct) is null ? PresenceOutcome.NoSession : PresenceOutcome.Ok;
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
