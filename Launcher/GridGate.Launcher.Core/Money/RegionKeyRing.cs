using System.Security.Cryptography;
using System.Text;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Core.Money;

/// <summary>
/// What a valid region key may act for: the regions of one program (money design §7.2). A paired Region Server's grant
/// names its <paramref name="ServerHost"/> instead; the service fills in its regions from Robust on each request (money
/// plan 3).
/// </summary>
public sealed record RegionKeyGrant(string KeyId, IReadOnlySet<Guid> Regions, string? ServerHost = null);

/// <summary>
/// The region keys the money service accepts. Keys are compared by their SHA-256 hashes in constant time. Local programs'
/// keys live in the owner-only settings file (the launcher writes them into each program's INI on every start); Region
/// Server keys (plan 3) will be stored as hashes only.
/// </summary>
public sealed class RegionKeyRing
{
    private readonly List<(byte[] Hash, RegionKeyGrant Grant)> _keys;

    private RegionKeyRing(List<(byte[] Hash, RegionKeyGrant Grant)> keys) => _keys = keys;

    private static readonly IReadOnlySet<Guid> NoRegions = new HashSet<Guid>();

    /// <summary>
    /// One grant per program key, covering that program's regions that aren't deleted; and one per paired Region Server,
    /// from its stored hash.
    /// </summary>
    public static RegionKeyRing FromSettings(LauncherSettings s) =>
        new(s.Money.Keys
            .Where(k => k.Value.Length > 0)
            .Select(k => (Hash(k.Value), new RegionKeyGrant(k.Key,
                s.Regions.Where(r => r.Instance == k.Key && r.DeletedAt is null).Select(r => r.Uuid).ToHashSet())))
            .Concat((s.Money.PairedServers ?? [])
                .Where(p => p.KeyHash.Length == 64)
                .Select(p => (Convert.FromHexString(p.KeyHash), new RegionKeyGrant("server:" + p.Id.ToString("N"), NoRegions, p.Host))))
            .ToList());

    public bool TryAuthenticate(string? presented, out RegionKeyGrant? grant)
    {
        grant = null;
        if (string.IsNullOrEmpty(presented)) return false;
        var hash = Hash(presented);
        // Every key is compared, so the time taken doesn't say which one matched.
        foreach (var (stored, g) in _keys)
            if (CryptographicOperations.FixedTimeEquals(stored, hash))
                grant ??= g;
        return grant is not null;
    }

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}

/// <summary>Makes sure every region program has a region key (plan 2 calls this before writing INIs).</summary>
public static class MoneyKeys
{
    public const int KeyLength = 48;

    /// <summary>
    /// Adds a new key for every program without one and drops keys of programs that no longer exist. Returns
    /// <paramref name="s"/> itself when nothing changes; never changes it in place.
    /// </summary>
    public static LauncherSettings EnsureProgramKeys(LauncherSettings s)
    {
        var programs = s.Programs.Select(p => p.Instance).ToHashSet();
        var current = s.Money.Keys;
        if (programs.All(current.ContainsKey) && current.Keys.All(programs.Contains))
            return s;
        var keys = programs.ToDictionary(p => p, p => current.TryGetValue(p, out var k) && k.Length > 0 ? k : Secrets.NewPassword(KeyLength));
        return s with { Money = s.Money with { ProgramKeys = keys } };
    }
}
