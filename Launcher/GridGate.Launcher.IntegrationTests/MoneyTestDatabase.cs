using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using MySqlConnector;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// One MariaDB per test class (started once, like <see cref="RegionStoreTests"/> starts one per test). Each test asks
/// <see cref="FreshAsync"/> for empty <c>money</c> and <c>robust</c> databases; <c>robust</c> holds only <c>Presence</c>
/// (OpenSim's columns, Presence.migrations) and <c>UserAccounts</c> and <c>GridUser</c> (the columns the money code reads).
/// </summary>
public sealed class MoneyMariaDb : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gg-money-" + Guid.NewGuid().ToString("N"));
    private Process? _server;
    private MariaDbServer? _maria;

    public LauncherSettings Settings { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (IntegrationEnvironment.MariaDbBin is not { } bin)
            return; // each test calls RequireMariaDbBin() and skips
        var paths = new LauncherPaths(_root, _root, bin);
        paths.EnsureCreated();
        Settings = new LauncherSettings
        {
            Db = new DatabaseSecrets(Password(), Password(), Password(), Password()),
            Console = new ConsoleSecrets("u", Password()),
            MachineId = "test",
            MariaDbPort = FreePort(),
        };
        _maria = new MariaDbServer(MariaDbBinaries.Locate(bin, OperatingSystem.IsWindows()), paths, Settings);
        await _maria.InitializeAsync(CancellationToken.None);
        var psi = _maria.StartInfo();
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        _server = Process.Start(psi)!;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!await _maria.PingAsync(CancellationToken.None))
        {
            if (_server.HasExited) throw new InvalidOperationException("mariadbd exited while waiting for it to answer.");
            if (DateTime.UtcNow > deadline) throw new TimeoutException("MariaDB did not answer within 60 s.");
            await Task.Delay(500);
        }
        await new DatabaseProvisioner(Settings).EnsureAsync(CancellationToken.None);
    }

    /// <summary>Empty money and robust databases for one test (skips the test without MariaDB).</summary>
    public async Task<MoneyTestDatabase> FreshAsync()
    {
        IntegrationEnvironment.RequireMariaDbBin();
        var db = new MoneyTestDatabase(Settings);
        await db.Sql("""
            DROP DATABASE IF EXISTS `money`; CREATE DATABASE `money` CHARACTER SET utf8mb4;
            DROP DATABASE IF EXISTS `robust`; CREATE DATABASE `robust` CHARACTER SET utf8mb4;
            CREATE TABLE `robust`.`Presence` (
              `UserID` varchar(255) NOT NULL,
              `RegionID` char(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
              `SessionID` char(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
              `SecureSessionID` char(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
              `LastSeen` timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
              UNIQUE KEY `SessionID` (`SessionID`), KEY `UserID` (`UserID`), KEY `RegionID` (`RegionID`)
            ) ENGINE=InnoDB DEFAULT CHARSET=latin1;
            CREATE TABLE `robust`.`UserAccounts` (
              `PrincipalID` char(36) NOT NULL, `FirstName` varchar(64) NOT NULL, `LastName` varchar(64) NOT NULL,
              PRIMARY KEY (`PrincipalID`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8;
            CREATE TABLE `robust`.`regions` (
              `uuid` VARCHAR(36) NOT NULL, `regionName` VARCHAR(128) NOT NULL DEFAULT '', `serverIP` VARCHAR(64) NOT NULL DEFAULT '',
              `serverPort` INT(10) UNSIGNED DEFAULT NULL,
              PRIMARY KEY (`uuid`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8;
            CREATE TABLE `robust`.`GridUser` (
              `UserID` VARCHAR(255) NOT NULL,
              `HomeRegionID` CHAR(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
              `LastRegionID` CHAR(36) NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
              `Online` CHAR(5) NOT NULL DEFAULT 'false',
              `Login` CHAR(16) NOT NULL DEFAULT '0',
              `Logout` CHAR(16) NOT NULL DEFAULT '0',
              PRIMARY KEY (`UserID`)
            ) ENGINE=InnoDB DEFAULT CHARSET=latin1;
            CREATE TABLE `robust`.`gridgate_session_starts` (
              `Id` BIGINT NOT NULL AUTO_INCREMENT,
              `SessionID` CHAR(36) NOT NULL,
              `UserID` CHAR(36) NOT NULL,
              `RegionID` CHAR(36) NOT NULL,
              `Source` VARCHAR(16) NOT NULL,
              `At` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
              PRIMARY KEY (`Id`),
              KEY (`SessionID`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """);
        return db;
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            try { if (_maria is not null && !_server.HasExited) await _maria.ShutdownAsync(CancellationToken.None); } catch { }
            if (!_server.WaitForExit(30_000)) _server.Kill(true);
            _server.Dispose();
        }
        for (var i = 0; i < 5; i++)
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); return; }
            catch { Thread.Sleep(500); }
        }
    }

    /// <summary>Stops the server for a test that needs the database gone; the class's later tests get no database.</summary>
    public async Task StopAsync()
    {
        if (_server is null || _maria is null || _server.HasExited) return;
        await _maria.ShutdownAsync(CancellationToken.None);
        _server.WaitForExit(30_000);
    }

    internal static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Password() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
}

/// <summary>One test's view of the money database: the settings to open it with, and raw SQL as root.</summary>
public sealed class MoneyTestDatabase(LauncherSettings settings)
{
    public LauncherSettings Settings { get; } = settings;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Root => new MySqlConnectionStringBuilder
    {
        Server = "127.0.0.1", Port = (uint)Settings.MariaDbPort, UserID = "root", Password = Settings.Db.RootPassword,
        SslMode = MySqlSslMode.Disabled, AllowUserVariables = true, Pooling = false,
    }.ConnectionString;

    /// <summary>Opens <c>money</c> as moneyuser, the way the launcher does.</summary>
    public Task<MySqlConnection> OpenMoney(CancellationToken ct) => GridDb.OpenMoneyAsync(Settings, 10, ct);

    /// <summary>Opens <c>robust</c> as robustuser, the way the launcher does.</summary>
    public Task<MySqlConnection> OpenRobust(CancellationToken ct) => GridDb.OpenRobustAsync(Settings, 10, ct);

    /// <summary>Creates the money schema and moneyuser's grants, as root, the way the launcher does.</summary>
    public async Task EnsureSchemaAsync()
    {
        await using var conn = await GridDb.OpenMoneyAsRootAsync(Settings, 10, Ct);
        await MoneySchema.EnsureAsync(conn, Ct);
    }

    public async Task Sql(string sql)
    {
        await using var conn = new MySqlConnection(Root);
        await conn.OpenAsync(Ct);
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    public async Task<long> Scalar(string sql)
    {
        await using var conn = new MySqlConnection(Root);
        await conn.OpenAsync(Ct);
        await using var cmd = new MySqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct));
    }

    public async Task<string?> Text(string sql)
    {
        await using var conn = new MySqlConnection(Root);
        await conn.OpenAsync(Ct);
        await using var cmd = new MySqlCommand(sql, conn);
        return (await cmd.ExecuteScalarAsync(Ct))?.ToString();
    }

    /// <summary>Adds a local resident to robust.UserAccounts.</summary>
    public Task AddUser(Guid id) =>
        Sql($"INSERT INTO `robust`.`UserAccounts` (`PrincipalID`, `FirstName`, `LastName`) VALUES ('{id}', 'F{id.ToString()[..4]}', 'L')");

    /// <summary>Puts an avatar in a region, as Robust's presence service does at login or the Gatekeeper for a visitor.</summary>
    public Task AddPresence(Guid user, Guid region, Guid session, Guid secure) =>
        Sql($"INSERT INTO `robust`.`Presence` (`UserID`, `RegionID`, `SessionID`, `SecureSessionID`) VALUES ('{user}', '{region}', '{session}', '{secure}')");

    /// <summary>Records a session start the way Robust's presence service does (Task 1's gridgate_session_starts); returns the new Id.</summary>
    public async Task<long> AddSessionStart(Guid session, Guid user, Guid region, string source)
    {
        await Sql($"INSERT INTO `robust`.`gridgate_session_starts` (`SessionID`, `UserID`, `RegionID`, `Source`) VALUES ('{session}', '{user}', '{region}', '{source}')");
        return await Scalar($"SELECT MAX(`Id`) FROM `robust`.`gridgate_session_starts` WHERE `SessionID` = '{session}'");
    }

    /// <summary>Registers a region with Robust the way a region does: its id, the address it reports (serverIP) and its UDP port (serverPort).</summary>
    public Task AddRegion(Guid id, string serverIp, int port = 9000) =>
        Sql($"INSERT INTO `robust`.`regions` (`uuid`, `regionName`, `serverIP`, `serverPort`) VALUES ('{id}', 'R{id.ToString()[..4]}', '{serverIp}', {port})");

    /// <summary>
    /// Records a hypergrid visitor the way the Gatekeeper does: GridUser.UserID = "uuid;HomeURI;First Last". Like OpenSim's
    /// GridUserService, a UUID keeps one row: a later arrival from another home updates the first row and keeps its UserID,
    /// so the first home seen wins (review of plan 3, Important 1).
    /// </summary>
    public Task AddVisitor(Guid id, string home, string name = "Ann Smith", long login = 1000) =>
        Sql($"INSERT INTO `robust`.`GridUser` (`UserID`, `Login`) SELECT '{id};{home};{name}', '{login}' FROM DUAL " +
            $"WHERE NOT EXISTS (SELECT 1 FROM `robust`.`GridUser` WHERE `UserID` LIKE '{id}%')");
}
