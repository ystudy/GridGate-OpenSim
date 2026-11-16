using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
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
/// "unavailable".
/// </summary>
public sealed record MoneyAnswer(string Code, long? Balance = null, long? ToBalance = null, long? EntryId = null, long? HoldId = null)
{
    public const string UnavailableCode = "unavailable";

    [JsonIgnore]
    public bool Ok => Code == "ok";

    public static MoneyAnswer Unavailable { get; } = new(UnavailableCode);
}

/// <summary>The grid's money settings, as the service gives them to regions.</summary>
public sealed record EconomyInfo(string CurrencySymbol, long UploadFee, long ClassifiedFee, bool VisitorWallets, bool ScriptPayouts, long MaxPayment);

/// <summary>The money service, as the region module calls it (money design §7.2). Implementations never throw for a failed call.</summary>
public interface IMoneyService
{
    Task<MoneyAnswer> BalanceAsync(MoneyWho who, CancellationToken ct);

    Task<MoneyAnswer> TransferAsync(Guid txn, string type, MoneyWho from, MoneyWho to, long amount, Guid regionId, Guid? objectId, string? objectName,
        CancellationToken ct);

    Task<MoneyAnswer> PayoutAsync(Guid txn, Guid ownerId, MoneyWho to, long amount, Guid regionId, Guid objectId, string objectName, CancellationToken ct);

    Task<MoneyAnswer> ChargeAsync(Guid txn, string type, MoneyWho payer, long amount, Guid? payTo, Guid regionId, CancellationToken ct);

    Task<MoneyAnswer> HoldAsync(Guid txn, string kind, MoneyWho buyer, MoneyWho seller, long amount, Guid regionId, Guid? objectId, string? objectName,
        CancellationToken ct);

    Task<MoneyAnswer> CompleteAsync(Guid txn, long holdId, CancellationToken ct);

    Task<MoneyAnswer> ReturnAsync(Guid txn, long holdId, CancellationToken ct);

    /// <summary>The settings, or null when the service can't be reached.</summary>
    Task<EconomyInfo?> EconomyAsync(CancellationToken ct);
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

    public Task<MoneyAnswer> HoldAsync(Guid txn, string kind, MoneyWho buyer, MoneyWho seller, long amount, Guid regionId, Guid? objectId,
        string? objectName, CancellationToken ct) =>
        Post("hold", new HoldBody(txn, kind, buyer, Payee(seller), amount, regionId, objectId, objectName), ct);

    public Task<MoneyAnswer> CompleteAsync(Guid txn, long holdId, CancellationToken ct) => Post("hold/complete", new SettleBody(txn, holdId), ct);

    public Task<MoneyAnswer> ReturnAsync(Guid txn, long holdId, CancellationToken ct) => Post("hold/return", new SettleBody(txn, holdId), ct);

    public async Task<EconomyInfo?> EconomyAsync(CancellationToken ct)
    {
        var wire = await Send<EconomyWire>("economy", new { }, ct).ConfigureAwait(false);
        return wire is { Code: "ok", CurrencySymbol: { } symbol }
            ? new EconomyInfo(symbol, wire.UploadFee, wire.ClassifiedFee, wire.VisitorWallets, wire.ScriptPayouts, wire.MaxPayment)
            : null;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The one being paid only needs an id and a home; a session would only be one more thing to leak.</summary>
    private static MoneyWho Payee(MoneyWho w) => w with { SessionId = null, SecureSessionId = null };

    private async Task<MoneyAnswer> Post(string path, object body, CancellationToken ct) =>
        await Send<MoneyAnswer>(path, body, ct).ConfigureAwait(false) is { Code: { Length: > 0 } } answer ? answer : MoneyAnswer.Unavailable;

    /// <summary>Null for anything but a 200 with a readable JSON body. Only the caller's own cancellation throws.</summary>
    private async Task<T?> Send<T>(string path, object body, CancellationToken ct) where T : class
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
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
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, Json, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or IOException or NotSupportedException)
        {
            _failed?.Invoke($"{path}: {e.GetType().Name}{(e.InnerException is { } inner ? " (" + inner.GetType().Name + ")" : "")}");
            return null;
        }
    }

    private sealed record TransferBody(Guid TxnId, string Type, MoneyWho From, MoneyWho To, long Amount, Guid RegionId, Guid? ObjectId, string? ObjectName);

    private sealed record PayoutBody(Guid TxnId, Guid OwnerId, MoneyWho To, long Amount, Guid RegionId, Guid ObjectId, string ObjectName);

    private sealed record ChargeBody(Guid TxnId, string Type, MoneyWho Payer, long Amount, Guid? PayTo, Guid RegionId);

    private sealed record HoldBody(Guid TxnId, string Kind, MoneyWho Buyer, MoneyWho Seller, long Amount, Guid RegionId, Guid? ObjectId, string? ObjectName);

    private sealed record SettleBody(Guid TxnId, long HoldId);

    private sealed record EconomyWire(string? Code, string? CurrencySymbol, long UploadFee, long ClassifiedFee, bool VisitorWallets, bool ScriptPayouts,
        long MaxPayment);
}
