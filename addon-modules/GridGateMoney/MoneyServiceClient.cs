using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

// Only BCL types here: the launcher's integration tests compile this file against the real money service.
namespace GridGate.Region.Money;

/// <summary>An avatar as the money service knows it: its id, its home grid ("" for a resident) and, when it acts, its session.</summary>
public sealed record MoneyWho(Guid AgentId, string Home, Guid? SessionId = null, Guid? SecureSessionId = null);

/// <summary>
/// The service's answer. <see cref="Code"/> is "ok" or a refusal code; every failure to reach the service reads as
/// "unavailable", and is <see cref="Lost"/> or <see cref="Final"/> as well when the service's own answer didn't arrive
/// (but neither when the connection was refused: nothing was sent).
/// </summary>
public sealed record MoneyAnswer(string Code, long? Balance = null, long? ToBalance = null, long? EntryId = null, long? HoldId = null)
{
    public const string UnavailableCode = "unavailable";

    [JsonIgnore]
    public bool Ok => Code == "ok";

    /// <summary>
    /// No answer arrived (no response at all, an HTTP 5xx, or a 200 whose body couldn't be read): the call may have
    /// happened. A connection refused before anything was sent is not lost. A service that answers "unavailable" itself
    /// almost always changed nothing: the exception is a database error at the commit, which the service can't tell
    /// apart from a commit that didn't land.
    /// </summary>
    [JsonIgnore]
    public bool Lost { get; init; }

    /// <summary>The call was turned away (an HTTP 4xx or another status without an answer): nothing happened, and sending it again won't help.</summary>
    [JsonIgnore]
    public bool Final { get; init; }

    public static MoneyAnswer Unavailable { get; } = new(UnavailableCode);

    /// <summary>What a call that may have happened, but got no answer, returns.</summary>
    public static MoneyAnswer NoAnswer { get; } = new(UnavailableCode) { Lost = true };

    /// <summary>What a call that was turned away returns.</summary>
    public static MoneyAnswer Rejected { get; } = new(UnavailableCode) { Final = true };
}

/// <summary>
/// The grid's money settings, as the service gives them to regions. <see cref="FeeHolds"/>: the service can hold a fee
/// before its action; one from before that says nothing, read as false.
/// </summary>
public sealed record EconomyInfo(string CurrencySymbol, long UploadFee, long ClassifiedFee, bool VisitorWallets, bool ScriptPayouts, long MaxPayment,
    bool FeeHolds = false);

/// <summary>The money service, as the region module calls it (money design §7.2). Implementations never throw for a failed call.</summary>
public interface IMoneyService
{
    Task<MoneyAnswer> BalanceAsync(MoneyWho who, CancellationToken ct);

    Task<MoneyAnswer> TransferAsync(Guid txn, string type, MoneyWho from, MoneyWho to, long amount, Guid regionId, Guid? objectId, string? objectName,
        CancellationToken ct);

    Task<MoneyAnswer> PayoutAsync(Guid txn, Guid ownerId, MoneyWho to, long amount, Guid regionId, Guid objectId, string objectName, CancellationToken ct);

    Task<MoneyAnswer> ChargeAsync(Guid txn, string type, MoneyWho payer, long amount, Guid? payTo, Guid regionId, CancellationToken ct);

    /// <summary>
    /// A purchase's hold (kind <c>object</c> or <c>land</c>, paying <paramref name="seller"/>), or a fee held before its
    /// action: <c>upload_fee</c> and <c>classified_fee</c> name no seller, <c>group_join_fee</c> the group's founder or none.
    /// </summary>
    Task<MoneyAnswer> HoldAsync(Guid txn, string kind, MoneyWho buyer, MoneyWho? seller, long amount, Guid regionId, Guid? objectId, string? objectName,
        CancellationToken ct);

    Task<MoneyAnswer> CompleteAsync(Guid txn, long holdId, CancellationToken ct);

    Task<MoneyAnswer> ReturnAsync(Guid txn, long holdId, CancellationToken ct);

    /// <summary>The settings, or null when the service can't be reached.</summary>
    Task<EconomyInfo?> EconomyAsync(CancellationToken ct);

    // Where avatars are: the region module's place reports.

    /// <summary>The money protocol this program speaks and the regions it runs.</summary>
    Task<MoneyAnswer> HealthAsync(int protocol, IReadOnlyList<Guid> regions, CancellationToken ct);

    /// <summary>
    /// <paramref name="sessionId"/> leaves <paramref name="fromRegion"/> for <paramref name="toRegion"/>; <paramref name="kind"/>
    /// is teleport, crossing or other_grid. <paramref name="toHost"/> and <paramref name="toPort"/> are the address and UDP
    /// port this program sent the avatar to (the destination's ExternalHostName and port), or null when unknown.
    /// </summary>
    Task<MoneyAnswer> HandoffAsync(Guid id, Guid sessionId, Guid fromRegion, Guid toRegion, string kind, string? toHost, int? toPort, CancellationToken ct);

    /// <summary>A teleport from <paramref name="fromRegion"/> failed: its open handoff is void.</summary>
    Task<MoneyAnswer> CancelHandoffAsync(Guid sessionId, Guid fromRegion, CancellationToken ct);

    /// <summary>The avatar became a root agent in <paramref name="regionId"/>.</summary>
    Task<MoneyAnswer> ArrivedAsync(Guid id, Guid sessionId, Guid agentId, Guid regionId, CancellationToken ct);
}

/// <summary>
/// <see cref="IMoneyService"/> over HTTPS to the launcher. It trusts exactly one certificate: the one whose SHA-256
/// matches the fingerprint the launcher wrote into the INI, whatever its name or chain. It logs nothing: no key, no
/// session, no body.
/// </summary>
public sealed class MoneyServiceClient : IMoneyService, IDisposable
{
    /// <summary>The longest any call waits, so a synchronous OpenSim caller is never held for long.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The longest an arrival waits. The service holds an arrival up to 10 seconds while its handoff may still be on the way,
    /// so it needs longer than <see cref="Timeout"/> to get its answer; only the region module's
    /// background reporter sends arrivals, never an OpenSim thread.
    /// </summary>
    public static readonly TimeSpan ArrivalTimeout = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _authorization;
    private readonly Action<string>? _failed;

    /// <param name="failed">Told "path: why" for each failed call (an HTTP status or an exception's type); never a key, session or body.</param>
    public MoneyServiceClient(Uri serviceUri, string regionKey, string fingerprint, Action<string>? failed = null)
    {
        _failed = failed;
        var pin = fingerprint.Trim();
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = Timeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                // The pin is the trust: chain and name errors don't matter, a different certificate does.
                RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && Matches(cert, pin),
            },
        };
        _http = new HttpClient(handler) { BaseAddress = serviceUri, Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _authorization = "GridGateRegion " + regionKey;
    }

    private static bool Matches(X509Certificate cert, string pin) =>
        pin.Length > 0 && string.Equals(Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())), pin, StringComparison.OrdinalIgnoreCase);

    public Task<MoneyAnswer> BalanceAsync(MoneyWho who, CancellationToken ct) => Post("balance", who, ct);

    public Task<MoneyAnswer> TransferAsync(Guid txn, string type, MoneyWho from, MoneyWho to, long amount, Guid regionId, Guid? objectId,
        string? objectName, CancellationToken ct) =>
        Post("transfer", new TransferBody(txn, type, from, Payee(to), amount, regionId, objectId, objectName), ct);

    public Task<MoneyAnswer> PayoutAsync(Guid txn, Guid ownerId, MoneyWho to, long amount, Guid regionId, Guid objectId, string objectName,
        CancellationToken ct) =>
        Post("payout", new PayoutBody(txn, ownerId, Payee(to), amount, regionId, objectId, objectName), ct);

    public Task<MoneyAnswer> ChargeAsync(Guid txn, string type, MoneyWho payer, long amount, Guid? payTo, Guid regionId, CancellationToken ct) =>
        Post("charge", new ChargeBody(txn, type, payer, amount, payTo, regionId), ct);

    /// <summary>No seller is sent as none at all.</summary>
    public Task<MoneyAnswer> HoldAsync(Guid txn, string kind, MoneyWho buyer, MoneyWho? seller, long amount, Guid regionId, Guid? objectId,
        string? objectName, CancellationToken ct) =>
        Post("hold", new HoldBody(txn, kind, buyer, seller is null ? null : Payee(seller), amount, regionId, objectId, objectName), ct);

    public Task<MoneyAnswer> CompleteAsync(Guid txn, long holdId, CancellationToken ct) => Post("hold/complete", new SettleBody(txn, holdId), ct);

    public Task<MoneyAnswer> ReturnAsync(Guid txn, long holdId, CancellationToken ct) => Post("hold/return", new SettleBody(txn, holdId), ct);

    public async Task<EconomyInfo?> EconomyAsync(CancellationToken ct)
    {
        var (wire, _) = await Send<EconomyWire>("economy", new { }, Timeout, ct).ConfigureAwait(false);
        return wire is { Code: "ok", CurrencySymbol: { } symbol }
            ? new EconomyInfo(symbol, wire.UploadFee, wire.ClassifiedFee, wire.VisitorWallets, wire.ScriptPayouts, wire.MaxPayment, wire.FeeHolds)
            : null;
    }

    /// <summary>The service answers health with how many regions the key covers, not a code: "ok" when it says ok.</summary>
    public async Task<MoneyAnswer> HealthAsync(int protocol, IReadOnlyList<Guid> regions, CancellationToken ct) =>
        (await Send<HealthWire>("health", new HealthBody(protocol, regions), Timeout, ct).ConfigureAwait(false)).Body is { Ok: true }
            ? new MoneyAnswer("ok")
            : MoneyAnswer.Unavailable;

    public Task<MoneyAnswer> HandoffAsync(Guid id, Guid sessionId, Guid fromRegion, Guid toRegion, string kind, string? toHost, int? toPort,
        CancellationToken ct) =>
        Post("handoff", new HandoffBody(id, sessionId, fromRegion, toRegion, kind, toHost, toPort), ct);

    public Task<MoneyAnswer> CancelHandoffAsync(Guid sessionId, Guid fromRegion, CancellationToken ct) =>
        Post("handoff/cancel", new CancelBody(sessionId, fromRegion), ct);

    /// <summary>Waits up to <see cref="ArrivalTimeout"/>: the service may hold an arrival while its handoff is on the way.</summary>
    public Task<MoneyAnswer> ArrivedAsync(Guid id, Guid sessionId, Guid agentId, Guid regionId, CancellationToken ct) =>
        Post("arrived", new ArrivedBody(id, sessionId, agentId, regionId), ArrivalTimeout, ct);

    public void Dispose() => _http.Dispose();

    /// <summary>The one being paid only needs an id and a home; a session would only be one more thing to leak.</summary>
    private static MoneyWho Payee(MoneyWho w) => w with { SessionId = null, SecureSessionId = null };

    private Task<MoneyAnswer> Post(string path, object body, CancellationToken ct) => Post(path, body, Timeout, ct);

    /// <summary>
    /// The service's answer; without one, <see cref="MoneyAnswer.NoAnswer"/> when the call may have happened,
    /// <see cref="MoneyAnswer.Rejected"/> when it was turned away, <see cref="MoneyAnswer.Unavailable"/> when it was never sent.
    /// </summary>
    private async Task<MoneyAnswer> Post(string path, object body, TimeSpan timeout, CancellationToken ct)
    {
        var (answer, missing) = await Send<MoneyAnswer>(path, body, timeout, ct).ConfigureAwait(false);
        return answer is { Code: { Length: > 0 } } ? answer : missing;
    }

    /// <summary>
    /// The body: null for anything but a 200 with a readable JSON body within <paramref name="timeout"/>. Without one,
    /// <c>Missing</c> says what that means: <see cref="MoneyAnswer.NoAnswer"/> when the call may still have happened (no
    /// response, an HTTP 5xx, or a 200 whose body couldn't be read); <see cref="MoneyAnswer.Rejected"/> for any other status
    /// (a 4xx), which turned it away before anything happened; <see cref="MoneyAnswer.Unavailable"/> when the connection was
    /// refused, so nothing was sent. Only the caller's own cancellation throws.
    /// </summary>
    private async Task<(T? Body, MoneyAnswer Missing)> Send<T>(string path, object body, TimeSpan timeout, CancellationToken ct) where T : class
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), Json), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization", _authorization);
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                _failed?.Invoke($"{path}: HTTP {(int)response.StatusCode}");
                return (null, (int)response.StatusCode >= 500 ? MoneyAnswer.NoAnswer : MoneyAnswer.Rejected);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            return (await JsonSerializer.DeserializeAsync<T>(stream, Json, cts.Token).ConfigureAwait(false), MoneyAnswer.NoAnswer);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or IOException or NotSupportedException)
        {
            _failed?.Invoke($"{path}: {e.GetType().Name}{(e.InnerException is { } inner ? " (" + inner.GetType().Name + ")" : "")}");
            // A refused connection sent nothing (the service is down or restarting): unavailable, but not lost.
            var refused = e is HttpRequestException { InnerException: SocketException { SocketErrorCode: SocketError.ConnectionRefused } };
            return (null, refused ? MoneyAnswer.Unavailable : MoneyAnswer.NoAnswer);
        }
    }

    private sealed record TransferBody(Guid TxnId, string Type, MoneyWho From, MoneyWho To, long Amount, Guid RegionId, Guid? ObjectId, string? ObjectName);

    private sealed record PayoutBody(Guid TxnId, Guid OwnerId, MoneyWho To, long Amount, Guid RegionId, Guid ObjectId, string ObjectName);

    private sealed record ChargeBody(Guid TxnId, string Type, MoneyWho Payer, long Amount, Guid? PayTo, Guid RegionId);

    private sealed record HoldBody(Guid TxnId, string Kind, MoneyWho Buyer, MoneyWho? Seller, long Amount, Guid RegionId, Guid? ObjectId, string? ObjectName);

    private sealed record SettleBody(Guid TxnId, long HoldId);

    private sealed record HealthBody(int Protocol, IReadOnlyList<Guid> Regions);

    private sealed record HealthWire(bool Ok, int Regions);

    private sealed record HandoffBody(Guid Id, Guid SessionId, Guid FromRegion, Guid ToRegion, string Kind, string? ToHost, int? ToPort);

    private sealed record CancelBody(Guid SessionId, Guid FromRegion);

    private sealed record ArrivedBody(Guid Id, Guid SessionId, Guid AgentId, Guid RegionId);

    private sealed record EconomyWire(string? Code, string? CurrencySymbol, long UploadFee, long ClassifiedFee, bool VisitorWallets, bool ScriptPayouts,
        long MaxPayment, bool FeeHolds);
}
