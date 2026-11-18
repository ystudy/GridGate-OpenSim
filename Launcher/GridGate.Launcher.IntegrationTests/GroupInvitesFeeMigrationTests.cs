using System.Text;
using System.Text.RegularExpressions;
using MySqlConnector;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Groups;
using OpenSim.Server.Base;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// The join fee kept with a group invitation: the <c>GridGateGroupInvites</c> migration
/// (OpenSim/Data/MySQL/Resources/GridGateGroupInvites.migrations) run on MariaDB the way OpenSim's Migration runs each
/// <c>:VERSION</c>, and Robust's groups service storing and reading the fee through the real tables.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class GroupInvitesFeeMigrationTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static string Resource(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridGate.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("GridGate.sln not found"),
            "OpenSim", "Data", "MySQL", "Resources", file);
    }

    /// <summary>Each version's script, by number.</summary>
    private static SortedDictionary<int, string> Versions(string file)
    {
        var versions = new SortedDictionary<int, string>();
        var parts = Regex.Split(File.ReadAllText(Resource(file)), @"^:VERSION\s+(\d+).*$", RegexOptions.Multiline);
        for (var i = 1; i + 1 < parts.Length; i += 2) versions[int.Parse(parts[i])] = parts[i + 1];
        return versions;
    }

    private string Connection => new MySqlConnectionStringBuilder
    {
        Server = "127.0.0.1", Port = (uint)maria.Settings.MariaDbPort, UserID = "root", Password = maria.Settings.Db.RootPassword,
        Database = "robust", SslMode = MySqlSslMode.Disabled, Pooling = false,
    }.ConnectionString;

    private GroupsService NewService()
    {
        var config = new IniConfigSource();
        var groups = config.AddConfig("Groups");
                groups.Set("StorageProvider", "OpenSim.Data.MySQL.dll");
        // OpenSim's MySql.Data reads char(36) columns as text only with Old Guids, as in Robust's own connection string.
        groups.Set("ConnectionString", $"Server=127.0.0.1;Port={maria.Settings.MariaDbPort};Database=robust;User ID=root;Password={maria.Settings.Db.RootPassword};Old Guids=true;SslMode=None;Pooling=false");
        return new GroupsService(config);
    }

    [Fact]
    public async Task Version_1_adds_a_fee_to_invitations_and_keeps_older_ones_without_one()
    {
        var db = await maria.FreshAsync();
        // Version 1 makes the groups tables; version 2 only copies from tables of an older name, so it is left out.
        var groups = Versions("os_groups_Store.migrations");
        await db.Sql("USE `robust`;" + groups[1]);
        await db.Sql("USE `robust`;" + groups[3]);
        var old = Guid.NewGuid();
        await db.Sql($"INSERT INTO `robust`.`os_groups_invites` (InviteID, GroupID, RoleID, PrincipalID) VALUES ('{old}', '{Guid.NewGuid()}', '{Guid.Empty}', '{Guid.NewGuid()}')");

        var versions = Versions("GridGateGroupInvites.migrations");
        Assert.Equal([1], versions.Keys);
        await db.Sql("USE `robust`;" + versions[1]);

        var type = await db.Text("SELECT COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'robust' AND TABLE_NAME = 'os_groups_invites' AND COLUMN_NAME = 'Fee'");
        Assert.StartsWith("int", type);
        Assert.Equal("YES", await db.Text("SELECT IS_NULLABLE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'robust' AND TABLE_NAME = 'os_groups_invites' AND COLUMN_NAME = 'Fee'"));
        Assert.Equal(1, await db.Scalar($"SELECT COUNT(*) FROM `robust`.`os_groups_invites` WHERE InviteID = '{old}' AND Fee IS NULL"));
    }

    [Fact]
    public async Task An_invitation_stored_with_a_fee_reads_back_with_it()
    {
        var db = await maria.FreshAsync();
        var service = NewService();
        var owner = UUID.Random();
        var group = service.CreateGroup(owner.ToString(), "Fee Group", "", true, UUID.Zero, 50, true, true, false, owner, out _);
        Assert.NotEqual(UUID.Zero, group);

        var (withFee, withoutFee) = (UUID.Random(), UUID.Random());
        Assert.True(service.AddAgentToGroupInvite(owner.ToString(), withFee, group, UUID.Zero, UUID.Random().ToString(), 25));
        Assert.Equal(25, service.GetAgentToGroupInvite(owner.ToString(), withFee).Fee);
        Assert.Equal(1, await db.Scalar($"SELECT COUNT(*) FROM `robust`.`os_groups_invites` WHERE InviteID = '{withFee}' AND Fee = 25"));

        Assert.True(service.AddAgentToGroupInvite(owner.ToString(), withoutFee, group, UUID.Zero, UUID.Random().ToString(), null));
        Assert.Null(service.GetAgentToGroupInvite(owner.ToString(), withoutFee).Fee);
        Assert.Equal(1, await db.Scalar($"SELECT COUNT(*) FROM `robust`.`os_groups_invites` WHERE InviteID = '{withoutFee}' AND Fee IS NULL"));

        // A free invitation stores no fee either: it reads back as not known.
        var free = UUID.Random();
        Assert.True(service.AddAgentToGroupInvite(owner.ToString(), free, group, UUID.Zero, UUID.Random().ToString(), 0));
        Assert.Null(service.GetAgentToGroupInvite(owner.ToString(), free).Fee);
        Assert.Equal(1, await db.Scalar($"SELECT COUNT(*) FROM `robust`.`os_groups_invites` WHERE InviteID = '{free}' AND Fee IS NULL"));
    }

    [Fact]
    public async Task The_fee_travels_through_Robust_s_invite_request_and_a_request_without_one_stores_none()
    {
        await maria.FreshAsync();
        var service = NewService();
        var owner = UUID.Random();
        var group = service.CreateGroup(owner.ToString(), "Wire Group", "", true, UUID.Zero, 50, true, true, false, owner, out _);
        var handler = new GroupsServicePostHandler(service, null);

        string Post(Dictionary<string, object> request)
        {
            request["METHOD"] = "INVITE";
            request["RequestingAgentID"] = owner.ToString();
            var body = Encoding.UTF8.GetBytes(ServerUtils.BuildQueryString(request));
            return Encoding.UTF8.GetString(handler.Handle("/groups", new MemoryStream(body), null!, null!));
        }

        var (withFee, withoutFee) = (UUID.Random(), UUID.Random());
        Assert.Contains("true", Post(new() { ["OP"] = "ADD", ["InviteID"] = withFee.ToString(), ["GroupID"] = group.ToString(), ["RoleID"] = UUID.Zero.ToString(), ["AgentID"] = UUID.Random().ToString(), ["Fee"] = "25" }), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("true", Post(new() { ["OP"] = "ADD", ["InviteID"] = withoutFee.ToString(), ["GroupID"] = group.ToString(), ["RoleID"] = UUID.Zero.ToString(), ["AgentID"] = UUID.Random().ToString() }), StringComparison.OrdinalIgnoreCase);

        Assert.Contains("<Fee>25</Fee>", Post(new() { ["OP"] = "GET", ["InviteID"] = withFee.ToString() }));
        Assert.DoesNotContain("<Fee>", Post(new() { ["OP"] = "GET", ["InviteID"] = withoutFee.ToString() }));
    }
}
