using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Money;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// A Grid Asset Server's money service pairing Region Servers (money plan 3, task 5): the one-time code, the proofs, and
/// what a paired server's key may do.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class MoneyPairServiceTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>, IAsyncDisposable
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Own = Guid.Parse("e0000000-0000-0000-0000-00000000000e");      // the Grid Asset Server's own region
    private static readonly Guid Theirs = Guid.Parse("f0000000-0000-0000-0000-00000000000f");   // the Region Server's region
    private static readonly Guid Other = Guid.Parse("f1000000-0000-0000-0000-00000000000f");    // another computer's region
    private static readonly Guid Session = Guid.Parse("5000000a-0000-0000-0000-000000000005");
    private static readonly Guid Secure = Guid.Parse("6000000a-0000-0000-0000-000000000006");
    private const string ServerHost = "192.168.1.20";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-money-pair-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _logs = new();
    private MoneyServiceHost? _host;
    private HttpClient? _http;
    private LauncherSettings _settings = null!;
    private MoneyPairing _pairing = null!;
    private string _fingerprint = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_host is not null) await _host.StopAsync(CancellationToken.None);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<MoneyTestDatabase> StartAsync(OwnerPlan plan = OwnerPlan.GridAsset)
    {
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();
        await db.AddUser(A);
        await db.AddRegion(Own, "10.0.0.2");
        await db.AddRegion(Theirs, ServerHost);
        await db.AddRegion(Other, "192.168.1.30");
        var port = MoneyMariaDb.FreePort();
        _settings = db.Settings with
        {
            Plan = plan,
            MoneyPort = port,
            Programs = [new ProgramSettings("p1", 8004, 8011)],
            Regions = [new RegionSettings("p1", "Own", Own, UdpPort: 8004)],
            Money = new MoneySettings(Mode: MoneyMode.Play, ProgramKeys: new() { ["p1"] = "own-program-key-000000000000000000000000000000" }),
        };
        var dbSettings = _settings;
        var time = TimeProvider.System;
        var presence = new PresenceCheck(MoneyConnections.Robust(() => dbSettings));
        var ledger = new MoneyLedger(MoneyConnections.Money(() => dbSettings), () => _settings.Money, () => MoneyMode.Play,
            presence.IsLocalUserAsync, presence.IsKnownVisitorAsync, time);
        var loggers = LoggerFactory.Create(b => b.AddProvider(new MoneyServiceTests.QueueLoggerProvider(_logs)).SetMinimumLevel(LogLevel.Trace));
        Directory.CreateDirectory(_dir);
        var cert = MoneyCertificate.LoadOrCreate(Path.Combine(_dir, MoneyCertificate.FileName), time);
        _fingerprint = MoneyCertificate.Fingerprint(cert);
        _pairing = new MoneyPairing(time);
        _host = new MoneyServiceHost(ledger, presence, () => _settings, () => MoneyMode.Play, () => cert,
            new MoneyAuthFailures(time, loggers.CreateLogger<MoneyAuthFailures>()), loggers, new MoneyStatus(), _pairing,
            change => _settings = change(_settings), new RegionHosts(MoneyConnections.Robust(() => dbSettings)));
        await _host.StartAsync(Ct);
        _http = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, c, _, _) => c is not null && MoneyCertificate.Fingerprint(X509CertificateLoader.LoadCertificate(c.GetRawCertData())) == _fingerprint },
        }) { BaseAddress = new Uri($"https://127.0.0.1:{port}/money/v1/") };
        return db;
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body, string Raw)> Post(string path, object body, string? key = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (key is not null) req.Headers.TryAddWithoutValidation("Authorization", "GridGateRegion " + key);
        using var resp = await _http!.SendAsync(req, Ct);
        var raw = await resp.Content.ReadAsStringAsync(Ct);
        return (resp.StatusCode, raw.TrimStart().StartsWith('{') ? JsonNode.Parse(raw) : null, raw);
    }

    private static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>Pairs as a Region Server at <paramref name="host"/> would, with a fresh code; returns the key.</summary>
    private async Task<string> PairAsync(string host = ServerHost)
    {
        var (code, _) = _pairing.Issue();
        var nonce = NewNonce();
        var (status, body, _) = await Post("pair", new { host, nonce, proof = PairingProof.Client(code, _fingerprint, nonce, host) });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", (string?)body!["code"]);
        var key = (string)body["key"]!;
        Assert.True(PairingProof.Same(PairingProof.Server(code, _fingerprint, nonce, key), (string)body["proof"]!));
        return key;
    }

    private static object Who(Guid agent) => new { agentId = agent, home = "", sessionId = Session, secureSessionId = Secure };

    [Fact]
    public async Task Right_proof_pairs_and_stores_only_a_hash()
    {
        await StartAsync();

        var key = await PairAsync();

        var server = Assert.Single(_settings.Money.PairedServers!);
        Assert.Equal(ServerHost, server.Host);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))), server.KeyHash);
        Assert.DoesNotContain(key, JsonSerializer.Serialize(_settings));
        Assert.Null(_pairing.PendingUntil);
    }

    [Fact]
    public async Task Proof_for_another_certificate_is_refused()
    {
        await StartAsync();
        var (code, _) = _pairing.Issue();
        var nonce = NewNonce();

        var (status, body, _) = await Post("pair", new { host = ServerHost, nonce, proof = PairingProof.Client(code, new string('0', 64), nonce, ServerHost) });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("refused", (string?)body!["code"]);
        Assert.Null(_settings.Money.PairedServers);
        Assert.NotNull(_pairing.PendingUntil); // one wrong proof doesn't cancel the code
    }

    [Fact]
    public async Task Refusals_all_look_the_same()
    {
        await StartAsync();
        var nonce = NewNonce();
        var noCode = (await Post("pair", new { host = ServerHost, nonce, proof = new string('A', 64) })).Raw;
        var (code, _) = _pairing.Issue();
        var wrong = (await Post("pair", new { host = ServerHost, nonce, proof = PairingProof.Client(code, _fingerprint, nonce, "192.168.1.99") })).Raw;
        _pairing.Cancel();
        var cancelled = (await Post("pair", new { host = ServerHost, nonce, proof = PairingProof.Client(code, _fingerprint, nonce, ServerHost) })).Raw;

        Assert.Equal("""{"code":"refused"}""", noCode);
        Assert.Equal(noCode, wrong);
        Assert.Equal(noCode, cancelled);
    }

    [Fact]
    public async Task Malformed_pair_requests_are_refused()
    {
        await StartAsync();
        var (code, _) = _pairing.Issue();

        foreach (var body in new object[]
                 {
                     new { host = "a b", nonce = NewNonce(), proof = new string('A', 64) },
                     new { host = ServerHost, nonce = "short", proof = new string('A', 64) },
                     new { host = ServerHost, nonce = NewNonce(), proof = "nothex" },
                 })
            Assert.Equal("refused", (string?)(await Post("pair", body)).Body!["code"]);
        Assert.Null(_settings.Money.PairedServers);
        Assert.Equal(code, _pairing.Redeem(c => c == code)); // malformed requests never reach the code
    }

    [Fact]
    public async Task Paired_key_acts_for_its_own_regions()
    {
        var db = await StartAsync();
        await db.AddPresence(A, Theirs, Session, Secure);
        var key = await PairAsync();

        var (_, body, _) = await Post("balance", Who(A), key);

        Assert.Equal("ok", (string?)body!["code"]);
        Assert.Equal(100, body["balance"]!.GetValue<long>());
    }

    [Fact]
    public async Task Server_key_never_covers_the_grids_own_regions()
    {
        var db = await StartAsync();
        await db.AddPresence(A, Own, Session, Secure);
        await db.Sql($"UPDATE `robust`.`regions` SET serverIP = '{ServerHost}' WHERE uuid = '{Own}'"); // Robust says it's theirs
        var key = await PairAsync();

        Assert.Equal("wrong_region", (string?)(await Post("balance", Who(A), key)).Body!["code"]);
        Assert.Equal("wrong_region", (string?)(await Post("transfer",
            new { txnId = Guid.NewGuid(), type = "pay_avatar", from = Who(A), to = new { agentId = Guid.NewGuid(), home = "" }, amount = 1, regionId = Own }, key)).Body!["code"]);
    }

    [Fact]
    public async Task Server_key_covers_only_its_address()
    {
        var db = await StartAsync();
        await db.AddPresence(A, Other, Session, Secure);
        var key = await PairAsync();

        Assert.Equal("wrong_region", (string?)(await Post("balance", Who(A), key)).Body!["code"]);
    }

    [Fact]
    public async Task Removed_server_is_refused_at_once()
    {
        var db = await StartAsync();
        await db.AddPresence(A, Theirs, Session, Secure);
        var key = await PairAsync();
        Assert.Equal(HttpStatusCode.OK, (await Post("health", new { }, key)).Status);

        _settings = _settings with { Money = _settings.Money with { PairedServers = [] } };

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("health", new { }, key)).Status);
    }

    [Fact]
    public async Task Pairing_the_same_host_again_replaces_the_key()
    {
        await StartAsync();
        var first = await PairAsync();

        var second = await PairAsync("192.168.1.20");

        Assert.Single(_settings.Money.PairedServers!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("health", new { }, first)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Post("health", new { }, second)).Status);
    }

    [Fact]
    public async Task Pair_refused_off_grid_asset()
    {
        await StartAsync(OwnerPlan.Local);
        var (code, _) = _pairing.Issue();
        var nonce = NewNonce();

        var body = (await Post("pair", new { host = ServerHost, nonce, proof = PairingProof.Client(code, _fingerprint, nonce, ServerHost) })).Body;

        Assert.Equal("refused", (string?)body!["code"]);
        Assert.Null(_settings.Money.PairedServers);
    }

    [Fact]
    public async Task Nothing_secret_is_logged()
    {
        await StartAsync();
        var (code, _) = _pairing.Issue();
        var nonce = NewNonce();
        var proof = PairingProof.Client(code, _fingerprint, nonce, ServerHost);

        var body = (await Post("pair", new { host = ServerHost, nonce, proof })).Body!;
        var key = (string)body["key"]!;
        await Post("pair", new { host = ServerHost, nonce, proof }); // a replay, refused

        var log = string.Join("\n", _logs);
        Assert.Contains("Paired a Region Server at 192.168.1.20.", log);
        foreach (var secret in new[] { code, key, nonce, proof, (string)body["proof"]! })
            Assert.DoesNotContain(secret, log);
    }
}
