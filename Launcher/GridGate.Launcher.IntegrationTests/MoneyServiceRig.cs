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
/// (in Here) and B (in There) are logged in, and visitor V from <see cref="VisitorHome"/> is logged in at Here.
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
    public LauncherSettings Settings { get; private set; } = null!;

    /// <summary>The clock the wrong-key counter reads: a test steps it past the window so its own wrong keys aren't slowed down.</summary>
    public FakeTimeProvider FailureClock { get; } = new(DateTimeOffset.UtcNow);
    public IEnumerable<string> Logs => _logs;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<MoneyServiceRig> StartAsync(MoneyMariaDb maria)
    {
        var rig = new MoneyServiceRig();
        await rig.InitAsync(maria);
        return rig;
    }

    private async Task InitAsync(MoneyMariaDb maria)
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
        var port = MoneyMariaDb.FreePort();
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
        _host = new MoneyServiceHost(Ledger, presence, () => Settings, () => MoneyMode.Play, () => cert,
            new MoneyAuthFailures(FailureClock, loggers.CreateLogger<MoneyAuthFailures>()), loggers, new MoneyStatus(), Pairing,
            change => Settings = change(Settings), new RegionHosts(MoneyConnections.Robust(() => dbSettings)));
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
