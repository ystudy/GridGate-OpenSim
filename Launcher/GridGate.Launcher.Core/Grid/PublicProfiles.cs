using System.Globalization;
using System.Text.RegularExpressions;
using GridGate.Launcher.Core.Dashboard;
using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Settings;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace GridGate.Launcher.Core.Grid;

/// <summary>A pick on a resident's profile (the viewer's Picks tab).</summary>
/// <param name="RegionName">The region the pick is in, as the viewer stored it.</param>
/// <param name="X">The pick's spot in grid metres (<c>posglobal</c>), 0 when it can't be read.</param>
public sealed record PublicPick(Guid Id, string Name, string Description, Guid SnapshotId, string RegionName, float X, float Y, float Z);

/// <summary>
/// What the Residents page may show of one resident (spec 2026-11-21-resident-profiles-design): never their email, presence,
/// partner or "first life" details.
/// </summary>
/// <param name="Created">When the account was made; null when unknown.</param>
/// <param name="ImageId">The profile picture texture; empty when none.</param>
/// <param name="Picks">Enabled picks, top picks first then the resident's own order, at most <see cref="PublicProfiles.MaxPicks"/>.</param>
public sealed record PublicProfile(Guid Id, string FirstName, string LastName, DateTimeOffset? Created, Guid ImageId, string About,
    string WantTo, string Skills, string Languages, string WebUrl, IReadOnlyList<PublicPick> Picks)
{
    public string Name => FirstName + " " + LastName;
}

/// <param name="Available">False when the database could not be asked; <see cref="Profiles"/> is then empty.</param>
public sealed record PublicProfileList(IReadOnlyList<PublicProfile> Profiles, bool Available)
{
    public static readonly PublicProfileList Unavailable = new([], false);
}

/// <summary>Reads the profiles of the residents who chose the Residents page; the host can swap in a test double.</summary>
public interface IPublicProfiles
{
    /// <summary>
    /// The profiles of those of <paramref name="principals"/> who are people on this grid with an account that isn't blocked.
    /// Never throws for a database that is down: returns <see cref="PublicProfileList.Unavailable"/>.
    /// </summary>
    Task<PublicProfileList> ReadAsync(IReadOnlyCollection<Guid> principals, CancellationToken ct);
}

/// <summary>
/// Reads <c>UserAccounts</c>, <c>userprofile</c> and <c>userpicks</c> as robustuser (spec 2026-11-21-resident-profiles-design,
/// "Load and safety"), only for the accounts asked about, <see cref="Chunk"/> ids a query. Shaped like <see cref="ListedParcels"/>:
/// settings read per call, a short timeout, any failure but the caller's cancellation gives "unavailable". A grid whose Robust
/// hasn't made the profile tables yet reads as profiles with nothing filled in.
/// </summary>
public sealed partial class PublicProfiles(Func<LauncherSettings> settings, ILogger<PublicProfiles>? log = null) : IPublicProfiles
{
    public const int MaxPicks = 10;
    public const int Chunk = 500;
    private const uint TimeoutSeconds = 3;

    private int _warned;

    public async Task<PublicProfileList> ReadAsync(IReadOnlyCollection<Guid> principals, CancellationToken ct)
    {
        if (principals.Count == 0) return new PublicProfileList([], true);
        try
        {
            await using var conn = await GridDb.OpenRobustAsync(settings(), TimeoutSeconds, ct);
            var accounts = new List<(Guid Id, string First, string Last, DateTimeOffset? Created)>();
            var profiles = new Dictionary<Guid, string[]>();
            var picks = new Dictionary<Guid, List<(bool Top, int Order, PublicPick Pick)>>();
            foreach (var chunk in principals.Distinct().Chunk(Chunk))
            {
                await ReadAccountsAsync(conn, chunk, accounts, ct);
                var found = accounts.Select(a => a.Id).Intersect(chunk).ToArray();
                if (found.Length == 0) continue;
                await ReadProfilesAsync(conn, found, profiles, ct);
                await ReadPicksAsync(conn, found, picks, ct);
            }
            return new PublicProfileList([.. accounts.Select(a =>
            {
                var p = profiles.GetValueOrDefault(a.Id) ?? ["", "", "", "", "", ""];
                var mine = picks.GetValueOrDefault(a.Id) ?? [];
                return new PublicProfile(a.Id, a.First, a.Last, a.Created, IdOrEmpty(p[0]), p[1], p[2], p[3], p[4], p[5],
                    [.. mine.OrderByDescending(x => x.Top).ThenBy(x => x.Order).ThenBy(x => x.Pick.Name, StringComparer.OrdinalIgnoreCase)
                        .Take(MaxPicks).Select(x => x.Pick)]);
            })], true);
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return PublicProfileList.Unavailable; // the database is down or still starting: expected, not logged
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var level = Interlocked.Exchange(ref _warned, 1) == 0 ? LogLevel.Warning : LogLevel.Debug;
            log?.Log(level, "The residents' profiles could not be read ({Error}: {Message}).", e.GetType().Name, e.Message);
            return PublicProfileList.Unavailable;
        }
    }

    private static async Task ReadAccountsAsync(MySqlConnection conn, Guid[] ids, List<(Guid, string, string, DateTimeOffset?)> into,
        CancellationToken ct)
    {
        await using var cmd = Command(conn,
            "SELECT u.`PrincipalID`, u.`FirstName`, u.`LastName`, u.`Created` FROM `UserAccounts` u " +
            $"WHERE u.`PrincipalID` IN ({Names(ids.Length)}) AND u.`UserLevel` >= 0 AND {WorldCounter.Person}", ids);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var created = r.IsDBNull(3) ? 0L : Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture);
            into.Add((InventoryStore.IdOf(r.GetValue(0)), Text(r, 1), Text(r, 2),
                created > 0 ? DateTimeOffset.FromUnixTimeSeconds(created) : null));
        }
    }

    private static async Task ReadProfilesAsync(MySqlConnection conn, Guid[] ids, Dictionary<Guid, string[]> into, CancellationToken ct)
    {
        try
        {
            await using var cmd = Command(conn,
                "SELECT `useruuid`, `profileImage`, `profileAboutText`, `profileWantToText`, `profileSkillsText`, `profileLanguages`, " +
                $"`profileURL` FROM `userprofile` WHERE `useruuid` IN ({Names(ids.Length)})", ids);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                into[InventoryStore.IdOf(r.GetValue(0))] = [Text(r, 1), Text(r, 2), Text(r, 3), Text(r, 4), Text(r, 5), Text(r, 6)];
        }
        catch (MySqlException e) when (e.ErrorCode == MySqlErrorCode.NoSuchTable)
        {
            // Robust makes the profile tables when its profile service first starts: until then nobody has filled one in.
        }
    }

    private static async Task ReadPicksAsync(MySqlConnection conn, Guid[] ids, Dictionary<Guid, List<(bool, int, PublicPick)>> into,
        CancellationToken ct)
    {
        try
        {
            await using var cmd = Command(conn,
                "SELECT `creatoruuid`, `pickuuid`, `toppick`, `sortorder`, `name`, `description`, `snapshotuuid`, `simname`, `posglobal` " +
                $"FROM `userpicks` WHERE `enabled` = 'true' AND `creatoruuid` IN ({Names(ids.Length)})", ids);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var (x, y, z) = Position(Text(r, 8));
                var pick = new PublicPick(InventoryStore.IdOf(r.GetValue(1)), Text(r, 4), Text(r, 5), IdOrEmpty(Text(r, 6)), Text(r, 7), x, y, z);
                var owner = InventoryStore.IdOf(r.GetValue(0));
                if (!into.TryGetValue(owner, out var list)) into[owner] = list = [];
                list.Add((Text(r, 2) == "true", r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3), CultureInfo.InvariantCulture), pick));
            }
        }
        catch (MySqlException e) when (e.ErrorCode == MySqlErrorCode.NoSuchTable)
        {
        }
    }

    /// <summary>OpenSim's <c>Vector3.ToString()</c>, <c>&lt;x, y, z&gt;</c>, as numbers; zeros when it isn't one.</summary>
    public static (float X, float Y, float Z) Position(string text)
    {
        var m = Vector().Match(text);
        if (!m.Success) return (0, 0, 0);
        static float F(Group g) => float.TryParse(g.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) ? v : 0;
        return (F(m.Groups[1]), F(m.Groups[2]), F(m.Groups[3]));
    }

    private static MySqlCommand Command(MySqlConnection conn, string sql, Guid[] ids)
    {
        var cmd = new MySqlCommand(sql, conn) { CommandTimeout = (int)TimeoutSeconds };
        for (var i = 0; i < ids.Length; i++) cmd.Parameters.AddWithValue("@p" + i, ids[i].ToString());
        return cmd;
    }

    private static string Names(int count) => string.Join(", ", Enumerable.Range(0, count).Select(i => "@p" + i));

    private static Guid IdOrEmpty(string text) => Guid.TryParse(text, out var id) ? id : Guid.Empty;

    private static string Text(MySqlDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetValue(i).ToString()!.Trim();

    [GeneratedRegex(@"^\s*<\s*([-+0-9.eE]+)\s*,\s*([-+0-9.eE]+)\s*,\s*([-+0-9.eE]+)\s*>\s*$")]
    private static partial Regex Vector();
}
