using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Money;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// The money service over HTTPS on a fresh database, as a Grid Asset Server runs it (so <c>/pair</c> is open), for the tests
/// the money review of 2026-11-15 found missing (L-5, L-8). Program p1 holds region Here and p2 region There; residents A
/// and B and visitor V from <see cref="VisitorHome"/> are logged in. Unless asked not to, both programs' modules have
/// checked in (so Here and There are verified) and the service has placed A and V at Here and B at There, as a start and
/// an arrival do (money design §8.4): Robust's Presence no longer says where anyone is.
/// </summary>
internal sealed class MoneyServiceRig : IAsyncDisposable
{
    public static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    public static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    public static readonly Guid V = Guid.Parse("c0000000-0000-0000-0000-00000000000c");
    public static readonly Guid Here = Guid.Parse("e0000000-0000-0000-0000-00000000000e");
    public static readonly Guid There = Guid.Parse("f0000000-0000-0000-0000-00000000000f");
    public static readonly Guid SessionA = Guid.Parse("5000000a-0000-0000-0000-000000000005");
    public static readonly Guid SecureA = Guid.Parse("6000000a-0000-0000-0000-000000000006");
    public static readonly Guid SessionB = Guid.Parse("5000000b-0000-0000-0000-000000000005");
    public static readonly Guid SecureB = Guid.Parse("6000000b-0000-0000-0000-000000000006");
    public static readonly Guid SessionV = Guid.Parse("5000000c-0000-0000-0000-000000000005");
    public static readonly Guid SecureV = Guid.Parse("6000000c-0000-0000-0000-000000000006");
    public const string VisitorHome = "http://other.example:8002";
    public const string Key = "the-region-key-for-program-one-0000000000000000";
    public const string Key2 = "the-region-key-for-program-two-0000000000000000";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-money-rig-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _logs = new();
    private MoneyServiceHost? _host;
    private HttpClient? _http;

    public MoneyTestDatabase Db { get; private set; } = null!;
    public MoneyLedger Ledger { get; private set; } = null!;
    public MoneyPairing Pairing { get; private set; } = null!;
    public string Fingerprint { get; private set; } = "";
    public LauncherSettings Settings { get; set; } = null!;

    /// <summary>The clock the wrong-key counter reads: a test steps it past the window so its own wrong keys aren't slowed down.</summary>
    public FakeTimeProvider FailureClock { get; } = new(DateTimeOffset.UtcNow);
    /// <summary>The clock the verification reads: a test steps it to age a key's last health call.</summary>
    public FakeTimeProvider VerifyClock { get; } = new(DateTimeOffset.UtcNow);
    public RegionVerification Verification { get; private set; } = null!;
    /// <summary>The clock the location record reads: an arrival nothing vouches for is held until a test moves it on.</summary>
    public FakeTimeProvider LocationClock { get; } = new(DateTimeOffset.UtcNow);
    public MoneyLocations Locations { get; private set; } = null!;
    /// <summary>The owner's alerts (money design §8.8), on the rig's database unless the rig was started with them down.</summary>
    public MoneyAlerts Alerts { get; private set; } = null!;
    /// <summary>The wrong-key counter: a test checks that refused claims never count against a key's address.</summary>
    public MoneyAuthFailures Failures { get; private set; } = null!;
    /// <summary>The money mode the service sees; a test switches coins off while it runs.</summary>
    public MoneyMode Mode { get; set; } = MoneyMode.Play;
    public int Port { get; private set; }
    public IEnumerable<string> Logs => _logs;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A started service; with <paramref name="place"/> false no module has checked in and nobody is placed. With
    /// <paramref name="alertsDown"/> the alerts' database can't be reached, while everything else's can.
    /// </summary>
    public static async Task<MoneyServiceRig> StartAsync(MoneyMariaDb maria, bool place = true, bool alertsDown = false)
    {
        var rig = new MoneyServiceRig();
        await rig.InitAsync(maria, alertsDown);
        if (place)
        {
            await rig.Health(Key);
            await rig.Health(Key2);
            await rig.Place(SessionA, Here);
            await rig.Place(SessionB, There);
            await rig.Place(SessionV, Here, "gatekeeper");
        }
        return rig;
    }

    private async Task InitAsync(MoneyMariaDb maria, bool alertsDown)
    {
        Db = await maria.FreshAsync();
        await Db.EnsureSchemaAsync();
        await Db.AddUser(A);
        await Db.AddUser(B);
        await Db.AddPresence(A, Here, SessionA, SecureA);
        await Db.AddPresence(B, There, SessionB, SecureB);
        await Db.AddPresence(V, Here, SessionV, SecureV);
        await Db.AddVisitor(V, VisitorHome + "/");
        await Db.AddRegion(Here, "127.0.0.1", 8004);
        await Db.AddRegion(There, "127.0.0.1", 8005);
        var port = Port = MoneyMariaDb.FreePort();
        Settings = Db.Settings with
        {
            Plan = OwnerPlan.GridAsset,
            MoneyPort = port,
            Programs = [new ProgramSettings("p1", 8004, 8011), new ProgramSettings("p2", 8005, 8012)],
            Regions = [new RegionSettings("p1", "Here", Here, UdpPort: 8004), new RegionSettings("p2", "There", There, UdpPort: 8005)],
            Money = new MoneySettings(Mode: MoneyMode.Play, UploadFee: 3, Visitors: VisitorMoney.All, ScriptPayouts: true,
                ProgramKeys: new() { ["p1"] = Key, ["p2"] = Key2 }),
        };
        var dbSettings = Settings;
        var time = TimeProvider.System;
        var presence = new PresenceCheck(MoneyConnections.Robust(() => dbSettings));
        Ledger = new MoneyLedger(MoneyConnections.Money(() => dbSettings), () => Settings.Money, () => MoneyMode.Play,
            presence.IsLocalUserAsync, presence.IsKnownVisitorAsync, time);
        var loggers = LoggerFactory.Create(b => b.AddProvider(new MoneyServiceTests.QueueLoggerProvider(_logs)).SetMinimumLevel(LogLevel.Trace));
        Directory.CreateDirectory(_dir);
        var cert = MoneyCertificate.LoadOrCreate(Path.Combine(_dir, MoneyCertificate.FileName), time);
        Fingerprint = MoneyCertificate.Fingerprint(cert);
        Pairing = new MoneyPairing(time);
        var alertsDb = alertsDown ? dbSettings with { MariaDbPort = MoneyMariaDb.FreePort() } : dbSettings;
        Alerts = new MoneyAlerts(MoneyConnections.Money(() => alertsDb), time, loggers.CreateLogger<MoneyAlerts>());
        Failures = new MoneyAuthFailures(FailureClock, loggers.CreateLogger<MoneyAuthFailures>());
        var pins = new RegionPins(MoneyConnections.Money(() => dbSettings), new RegionHosts(MoneyConnections.Robust(() => dbSettings)), TimeProvider.System, Alerts);
        Verification = new RegionVerification(MoneyConnections.Money(() => dbSettings), MoneyConnections.Robust(() => dbSettings), pins, VerifyClock);
        Locations = new MoneyLocations(MoneyConnections.Money(() => dbSettings), MoneyConnections.Robust(() => dbSettings), Verification, presence, LocationClock);
        _host = new MoneyServiceHost(Ledger, presence, () => Settings, () => Mode, () => cert,
            Failures, loggers, new MoneyStatus(), Pairing, change => Settings = change(Settings), pins, Verification, Locations, Alerts);
        await _host.StartAsync(Ct);
        _http = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, c, _, _) => c is not null && MoneyCertificate.Fingerprint(X509CertificateLoader.LoadCertificate(c.GetRawCertData())) == Fingerprint },
        }) { BaseAddress = new Uri($"https://127.0.0.1:{port}/money/v1/") };
    }

    /// <summary>
    /// Posts <paramref name="body"/> (a string is sent as is) to <paramref name="path"/> with <paramref name="key"/>, or with
    /// <paramref name="authorization"/> as the whole header when it is given.
    /// </summary>
    public async Task<(HttpStatusCode Status, JsonNode? Body, string Raw)> Post(string path, object? body, string? key = Key, string? authorization = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body is string s ? s : JsonSerializer.Serialize(body ?? new { }), Encoding.UTF8, "application/json"),
        };
        if (authorization is not null) req.Headers.TryAddWithoutValidation("Authorization", authorization);
        else if (key is not null) req.Headers.TryAddWithoutValidation("Authorization", "GridGateRegion " + key);
        var length = Encoding.UTF8.GetByteCount(body as string ?? "");
        HttpResponseMessage resp;
        try { resp = await _http!.SendAsync(req, Ct); }
        catch (HttpRequestException) when (length > MoneyServiceHost.MaxBodyBytes)
        {
            // The service answered 413 and closed the connection before the whole body was sent: refused, as it should be.
            return (HttpStatusCode.RequestEntityTooLarge, null, "");
        }
        using var _ = resp;
        var raw = await resp.Content.ReadAsStringAsync(Ct);
        JsonNode? json = null;
        if (raw.TrimStart().StartsWith('{'))
            try { json = JsonNode.Parse(raw); }
            catch (JsonException) { }
        return (resp.StatusCode, json, raw);
    }

    /// <summary>A module's health call for <paramref name="key"/>'s program at the handoff protocol, listing its region: that region is verified.</summary>
    public async Task Health(string key)
    {
        var answer = await Post("health", new { protocol = RegionVerification.HandoffProtocol, regions = new[] { RegionOf(key) } }, key);
        Assert.Equal(HttpStatusCode.OK, answer.Status);
    }

    /// <summary>
    /// Places <paramref name="session"/> at <paramref name="region"/> as the service learns it (money design §8.4): Robust
    /// writes a start, and the region's module reports the arrival with its key. Verified once that module has checked in.
    /// For a region or session that isn't the rig's own, pass the <paramref name="key"/> that covers the region and the
    /// session's <paramref name="agent"/>.
    /// </summary>
    public async Task<string> Place(Guid session, Guid region, string source = "login", string? key = null, Guid? agent = null)
    {
        var who = agent ?? AgentOf(session);
        await Db.AddSessionStart(session, who, region, source);
        var code = CodeOf(await Arrived(key ?? KeyOf(region), session, who, region));
        Assert.True(code is "ok" or "unverified_avatar", $"placing at {region}: {code}");
        return code;
    }

    /// <summary>
    /// A module's arrival report. One nothing vouches for is held (money design §8.4): the location clock moves on while it
    /// waits, so it gets its answer.
    /// </summary>
    public async Task<(HttpStatusCode Status, JsonNode? Body, string Raw)> Arrived(string key, Guid session, Guid agent, Guid region)
    {
        var call = Post("arrived", new { id = Guid.NewGuid(), sessionId = session, agentId = agent, regionId = region }, key);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!call.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "the arrival never answered");
            await Task.WhenAny(call, Task.Delay(20, Ct));
            if (!call.IsCompleted) LocationClock.Advance(MoneyLocations.ArrivalRetry);
        }
        return await call;
    }

    /// <summary>A module's handoff: <paramref name="session"/> leaves <paramref name="from"/> for <paramref name="to"/>.</summary>
    public async Task<string> Handoff(string key, Guid session, Guid from, Guid to, string kind = "teleport") =>
        CodeOf(await Post("handoff", new { id = Guid.NewGuid(), sessionId = session, fromRegion = from, toRegion = to, kind }, key));

    /// <summary>A teleport from Here to There: p1 hands <paramref name="session"/> off and p2 reports the arrival.</summary>
    public async Task MoveToThere(Guid session)
    {
        Assert.Equal("ok", await Handoff(Key, session, Here, There));
        Assert.Equal("ok", CodeOf(await Arrived(Key2, session, AgentOf(session), There)));
    }

    public static Guid AgentOf(Guid session) =>
        session == SessionA ? A : session == SessionB ? B : session == SessionV ? V : throw new ArgumentException("not a rig session", nameof(session));

    public static string KeyOf(Guid region) => region == Here ? Key : region == There ? Key2 : throw new ArgumentException("not a rig region", nameof(region));

    public static Guid RegionOf(string key) => key == Key ? Here : key == Key2 ? There : throw new ArgumentException("not a rig key", nameof(key));

    /// <summary>The answer's code, or "" when there is none.</summary>
    public static string CodeOf((HttpStatusCode Status, JsonNode? Body, string Raw) answer) => (string?)answer.Body?["code"] ?? "";

    public static object WhoA(Guid? session = null, Guid? secure = null) =>
        new { agentId = A, home = "", sessionId = session ?? SessionA, secureSessionId = secure ?? SecureA };

    public static object WhoB() => new { agentId = B, home = "", sessionId = SessionB, secureSessionId = SecureB };

    public static object WhoV(string home = VisitorHome, Guid? secure = null) =>
        new { agentId = V, home, sessionId = SessionV, secureSessionId = secure ?? SecureV };

    /// <summary>Asserts the books are sound: the self-check passes, and no account but the Grid bank is below zero.</summary>
    public async Task AssertBooksSoundAsync(string context = "")
    {
        var check = await Ledger.SelfCheckAsync(Ct);
        Assert.True(check.Ok, $"{context} self-check: sum {check.Sum}; {string.Join(" ", check.Problems)} " +
            string.Join(", ", check.Mismatches.Select(m => $"{m.AccountId} {m.Stored}/{m.FromEntries}")));
        Assert.Equal(0, await Db.Scalar($"SELECT COUNT(*) FROM `money`.`accounts` WHERE id <> {MoneySchema.BankId} AND balance < 0"));
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_host is not null) await _host.StopAsync(CancellationToken.None);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
