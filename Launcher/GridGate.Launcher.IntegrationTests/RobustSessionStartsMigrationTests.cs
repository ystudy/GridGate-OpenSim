using System.Text.RegularExpressions;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// Robust's own migrations for <c>gridgate_session_starts</c> (OpenSim/Data/MySQL/Resources/GridGateSessionStarts.migrations),
/// run step by step on MariaDB the way OpenSim's Migration runs each <c>:VERSION</c>.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class RobustSessionStartsMigrationTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static string MigrationsFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridGate.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("GridGate.sln not found"),
            "OpenSim", "Data", "MySQL", "Resources", "GridGateSessionStarts.migrations");
    }

    /// <summary>Each version's script, by number.</summary>
    private static SortedDictionary<int, string> Versions()
    {
        var versions = new SortedDictionary<int, string>();
        var parts = Regex.Split(File.ReadAllText(MigrationsFile()), @"^:VERSION\s+(\d+).*$", RegexOptions.Multiline);
        for (var i = 1; i + 1 < parts.Length; i += 2) versions[int.Parse(parts[i])] = parts[i + 1];
        return versions;
    }

    private static Task<string?> ColumnType(MoneyTestDatabase db, string column) =>
        db.Text($"SELECT COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'robust' AND TABLE_NAME = 'gridgate_session_starts' AND COLUMN_NAME = '{column}'");

    [Fact]
    public async Task Version_2_adds_the_address_and_keeps_older_rows_without_one()
    {
        var db = await maria.FreshAsync();
        await db.Sql("DROP TABLE `robust`.`gridgate_session_starts`");
        var versions = Versions();
        Assert.Equal([1, 2], versions.Keys);

        await db.Sql("USE `robust`;" + versions[1]);
        var (old, region) = (Guid.NewGuid(), Guid.NewGuid());
        await db.Sql($"INSERT INTO `robust`.`gridgate_session_starts` (SessionID, UserID, RegionID, Source) VALUES ('{old}', '{Guid.NewGuid()}', '{region}', 'login')");
        await db.Sql("USE `robust`;" + versions[2]);

        // The same columns as Robust's regions table, which the money service compares them with.
        Assert.Equal("varchar(64)", await ColumnType(db, "ServerIP"));
        Assert.Equal("int(10) unsigned", await ColumnType(db, "ServerPort"));
        Assert.Equal(1, await db.Scalar($"SELECT COUNT(*) FROM `robust`.`gridgate_session_starts` WHERE SessionID = '{old}' AND ServerIP IS NULL AND ServerPort IS NULL"));
        await db.Sql($"INSERT INTO `robust`.`gridgate_session_starts` (SessionID, UserID, RegionID, ServerIP, ServerPort, Source) VALUES ('{Guid.NewGuid()}', '{Guid.NewGuid()}', '{region}', '192.168.1.20', 9000, 'login')");
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `robust`.`gridgate_session_starts` WHERE ServerIP = '192.168.1.20' AND ServerPort = 9000"));
    }
}
