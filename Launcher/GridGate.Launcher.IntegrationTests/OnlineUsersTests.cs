using System.Diagnostics;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Settings;
using MySqlConnector;
using Xunit;
using static GridGate.Launcher.IntegrationTests.GridTestSupport;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>Who's online against the real Robust tables (created by Robust's own migrations).</summary>
[Collection(GridCollection.Name)]
public sealed class OnlineUsersTests : IDisposable
{
    private readonly string _root = NewDataDir();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(100) };

    public void Dispose()
    {
        KillEverythingUnder(_root);
        _http.Dispose();
        DeleteDataDir(_root);
    }

    [Fact]
    public async Task OnlineUsers_lists_presence_rows()
    {
        var paths = RequirePaths(_root);
        var store = new SettingsStore(paths.SettingsFile);
        var s = SeedSettings(store);
        var robust = NewRobustClient(_http, s);
        var grid = NewController(paths, store, robust);
        var ct = TestContext.Current.CancellationToken;
        var online = new OnlineUsers(store.LoadOrCreate);
        try
        {
            await grid.StartAsync(ct); // MariaDB, then Robust, which creates its tables
            var owner = await grid.CreateOwnerAsync("Test", "Owner", "pass-word1", null, ct);
            var nobody = await online.ListAsync(ct);
            Assert.True(nobody.Available);
            Assert.Empty(nobody.Avatars);

            var region = Guid.NewGuid();
            await using (var conn = new MySqlConnection(new MySqlConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = (uint)s.MariaDbPort, Database = "robust",
                UserID = "robustuser", Password = s.Db.RobustPassword, SslMode = MySqlSslMode.Disabled, Pooling = false,
            }.ConnectionString))
            {
                await conn.OpenAsync(ct);
                await using var cmd = new MySqlCommand(
                    "INSERT INTO Presence (UserID, RegionID, SessionID, SecureSessionID) VALUES (@u, @r, @s, @s2)", conn);
                cmd.Parameters.AddWithValue("@u", owner.PrincipalId.ToString());
                cmd.Parameters.AddWithValue("@r", region.ToString());
                cmd.Parameters.AddWithValue("@s", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@s2", Guid.NewGuid().ToString());
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var list = await online.ListAsync(ct);
            Assert.True(list.Available);
            Assert.Equal([new OnlineAvatar("Test Owner", region, owner.PrincipalId)], list.Avatars);

            // The dashboard's counts against the real robust schema (design 2026-11-05-dashboard): the owner is the one
            // resident, joined this month and never signed in; no region is registered, so no land.
            var now = DateTimeOffset.UtcNow;
            var counts = await new GridGate.Launcher.Core.Dashboard.WorldCounter(store.LoadOrCreate).CountAsync(
                new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero), now.Date, ct);
            Assert.Equal(1, counts.Residents);
            Assert.Equal(1, counts.JoinedThisMonth);
            Assert.Equal(1, counts.NeverSignedIn);
            Assert.Equal(0, counts.SignedInToday);
            Assert.Equal(0, counts.LandSquareMetres);
            Assert.Equal(0, counts.MapRegions);
            Assert.Equal(0, counts.HypergridLinks);

            // The front page's resident counts (spec 2026-11-19-front-page-design §2): the owner is registered and has never
            // signed in; after a sign-in five days ago they are active in the last 30 days, and not for a "since" of tomorrow.
            var residents = new GridGate.Launcher.Core.Dashboard.ResidentCounter(store.LoadOrCreate);
            Assert.Equal(new GridGate.Launcher.Core.Dashboard.ResidentCounts(1, 0), await residents.CountAsync(now.AddDays(-30), ct));
            await using (var conn = new MySqlConnection(new MySqlConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = (uint)s.MariaDbPort, Database = "robust",
                UserID = "robustuser", Password = s.Db.RobustPassword, SslMode = MySqlSslMode.Disabled, Pooling = false,
            }.ConnectionString))
            {
                await conn.OpenAsync(ct);
                await using var cmd = new MySqlCommand("INSERT INTO GridUser (UserID, Login) VALUES (@u, @l) ON DUPLICATE KEY UPDATE Login = @l", conn);
                cmd.Parameters.AddWithValue("@u", owner.PrincipalId.ToString());
                cmd.Parameters.AddWithValue("@l", now.AddDays(-5).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            Assert.Equal(new GridGate.Launcher.Core.Dashboard.ResidentCounts(1, 1), await residents.CountAsync(now.AddDays(-30), ct));
            Assert.Equal(new GridGate.Launcher.Core.Dashboard.ResidentCounts(1, 0), await residents.CountAsync(now.AddDays(1), ct));

            // The Residents pages' reader against Robust's own profile tables (spec 2026-11-21-resident-profiles-design): only
            // the accounts asked about, the profile's public parts, enabled picks top first; an unknown id is no one.
            var profiles = new PublicProfiles(store.LoadOrCreate);
            var bare = await profiles.ReadAsync([owner.PrincipalId, Guid.NewGuid()], ct);
            Assert.True(bare.Available);
            var noProfile = Assert.Single(bare.Profiles);
            Assert.Equal("Test Owner", noProfile.Name);
            Assert.Equal("", noProfile.About);
            Assert.Empty(noProfile.Picks);
            var photo = Guid.NewGuid();
            var (topPick, otherPick) = (Guid.NewGuid(), Guid.NewGuid());
            await using (var conn = new MySqlConnection(new MySqlConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = (uint)s.MariaDbPort, Database = "robust",
                UserID = "robustuser", Password = s.Db.RobustPassword, SslMode = MySqlSslMode.Disabled, Pooling = false,
            }.ConnectionString))
            {
                await conn.OpenAsync(ct);
                await using (var cmd = new MySqlCommand(
                    "INSERT INTO userprofile (useruuid, profilePartner, profileAllowPublish, profileMaturePublish, profileURL, " +
                    "profileWantToMask, profileWantToText, profileSkillsMask, profileSkillsText, profileLanguages, profileImage, " +
                    "profileAboutText, profileFirstImage, profileFirstText) VALUES (@u, @z, 0, 0, 'example.com', 0, 'Sail', 0, " +
                    "'Build', 'English', @img, 'Hello there.', @z, 'My real life')", conn))
                {
                    cmd.Parameters.AddWithValue("@u", owner.PrincipalId.ToString());
                    cmd.Parameters.AddWithValue("@z", Guid.Empty.ToString());
                    cmd.Parameters.AddWithValue("@img", photo.ToString());
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                foreach (var (pick, top, enabled, name) in new[] { (otherPick, "false", "true", "Second"), (topPick, "true", "true", "First"),
                             (Guid.NewGuid(), "true", "false", "Hidden") })
                {
                    await using var cmd = new MySqlCommand(
                        "INSERT INTO userpicks (pickuuid, creatoruuid, toppick, parceluuid, name, description, snapshotuuid, user, " +
                        "originalname, simname, posglobal, sortorder, enabled) VALUES (@p, @u, @top, @z, @name, 'A pick.', @z, '', '', " +
                        "'Sandbox', '<256100.5, 256050, 22>', 0, @on)", conn);
                    cmd.Parameters.AddWithValue("@p", pick.ToString());
                    cmd.Parameters.AddWithValue("@u", owner.PrincipalId.ToString());
                    cmd.Parameters.AddWithValue("@top", top);
                    cmd.Parameters.AddWithValue("@z", Guid.Empty.ToString());
                    cmd.Parameters.AddWithValue("@name", name);
                    cmd.Parameters.AddWithValue("@on", enabled);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }
            var filled = Assert.Single((await profiles.ReadAsync([owner.PrincipalId], ct)).Profiles);
            Assert.Equal(photo, filled.ImageId);
            Assert.Equal(("Hello there.", "Sail", "Build", "English", "example.com"), (filled.About, filled.WantTo, filled.Skills, filled.Languages, filled.WebUrl));
            Assert.Equal(["First", "Second"], filled.Picks.Select(p => p.Name));
            Assert.Equal((256100.5f, 256050f, 22f, "Sandbox"), (filled.Picks[0].X, filled.Picks[0].Y, filled.Picks[0].Z, filled.Picks[0].RegionName));
            Assert.Equal(Guid.Empty, filled.Picks[0].SnapshotId);

            // A database that is down: no exception, no long wait.
            await grid.StopAllAsync(ct);
            var watch = Stopwatch.StartNew();
            var down = await online.ListAsync(ct);
            Assert.False(down.Available);
            Assert.Empty(down.Avatars);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            throw new Exception(e.Message + Environment.NewLine + LogTail(paths), e);
        }
        finally
        {
            await grid.DetachAsync();
        }
    }
}
