using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Money;

/// <summary>
/// The money service's endpoints (money plan 1, task 9): POST, JSON, under <c>/money/v1/</c>, authenticated by
/// <c>Authorization: GridGateRegion &lt;key&gt;</c>. Answers are <c>{ "code": … }</c> with HTTP 200; a bad key is a bare
/// 401, a blocked address a bare 429, and a body that doesn't fit is 400 <c>{ "code": "bad_request" }</c>.
/// </summary>
internal static class MoneyApi
{
    private const string Scheme = "GridGateRegion ";
    private static readonly string[] PaymentTypes = [EntryType.PayAvatar, EntryType.PayObject, EntryType.ParcelPass];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Request bodies. Every field is nullable so a missing one is a 400 from Need, not a default value.
    internal sealed record Who(Guid? AgentId, string? Home, Guid? SessionId, Guid? SecureSessionId);
    internal sealed record TransferBody(Guid? TxnId, string? Type, Who? From, Who? To, long? Amount, Guid? RegionId, Guid? ObjectId, string? ObjectName);
    internal sealed record PayoutBody(Guid? TxnId, Guid? OwnerId, Who? To, long? Amount, Guid? RegionId, Guid? ObjectId, string? ObjectName);
    internal sealed record ChargeBody(Guid? TxnId, string? Type, Who? Payer, long? Amount, Guid? PayTo);
    internal sealed record HoldBody(Guid? TxnId, string? Kind, Who? Buyer, Who? Seller, long? Amount, Guid? RegionId, Guid? ObjectId, string? ObjectName);
    internal sealed record SettleBody(Guid? TxnId, long? HoldId);

    // Answers.
    internal sealed record Answer(string Code, long? Balance = null, long? ToBalance = null, long? EntryId = null, long? HoldId = null);
    internal sealed record EconomyAnswer(string Code, string CurrencySymbol, long UploadFee, long ClassifiedFee, bool VisitorWallets, bool ScriptPayouts, long MaxPayment);
    internal sealed record HealthBody(int? Protocol, Guid[]? Regions);
    internal sealed record HealthAnswer(bool Ok, int Regions);
    internal sealed record PairBody(string? Host, int? FirstPort, string? MachineId, string? Nonce, string? Proof);
    internal sealed record PairAnswer(string Code, string Key, string Proof);

    private sealed class BadRequest : Exception;

    /// <summary>
    /// What the endpoints need. <see cref="Pairing"/>, <see cref="Update"/> and <see cref="Pins"/> are for Region Servers
    /// (money plan 3): without them nobody can pair and a server key gets <c>unavailable</c>. <see cref="Fingerprint"/> is
    /// the certificate this listener serves.
    /// </summary>
    internal sealed record Context(MoneyLedger Ledger, PresenceCheck Presence, Func<LauncherSettings> Settings, MoneyAuthFailures Failures, ILogger Log,
        MoneyPairing? Pairing = null, Func<Func<LauncherSettings, LauncherSettings>, LauncherSettings>? Update = null, RegionPins? Pins = null,
        string Fingerprint = "", Func<MoneyMode>? Mode = null, RegionVerification? Verification = null);

    public static void Map(IEndpointRouteBuilder app, Context c)
    {
        app.MapPost("/money/v1/pair", (RequestDelegate)(http => PairAsync(http, c)));
        // How many regions the key covers now: a Region Server's page says when the grid sees none of its regions. The call
        // also records which money protocol the program speaks and which of its covered regions it runs (money design §8.2);
        // {} is protocol 1 with none, which is what a Region Server launcher's pairing check sends.
        Post(app, "/money/v1/health", c, async (http, grant, ct) =>
        {
            var b = await Read<HealthBody>(http, ct);
            var protocol = b.Protocol ?? 1;
            if (protocol < 1) throw new BadRequest();
            if (c.Verification is { } verification)
                await verification.HeardAsync(grant.KeyId, protocol, (b.Regions ?? []).Where(grant.Regions.Contains).ToList(), ct);
            return new HealthAnswer(true, grant.Regions.Count);
        });
        Post(app, "/money/v1/economy", c, (_, _, _) =>
        {
            var m = c.Settings().Money;
            return Task.FromResult<object>(new EconomyAnswer("ok", m.CurrencySymbol, m.UploadFee, m.ClassifiedFee, m.EffectiveVisitors != VisitorMoney.Off, m.ScriptPayouts, m.MaxPayment));
        });
        Post(app, "/money/v1/balance", c, async (http, grant, ct) =>
        {
            var w = await Read<Who>(http, ct);
            return await WithSession(c, w, grant, ct) ?? Of(await c.Ledger.BalanceAsync(Account(w), ct));
        });
        Post(app, "/money/v1/transfer", c, async (http, grant, ct) =>
        {
            var b = await Read<TransferBody>(http, ct);
            var type = Need(b.Type);
            if (!PaymentTypes.Contains(type)) throw new BadRequest();
            var from = Need(b.From);
            var region = Need(b.RegionId);
            if (!grant.Regions.Contains(region)) return Code("wrong_region");
            return await WithSession(c, from, grant, ct) ?? Of(await c.Ledger.TransferAsync(
                new TransferRequest(Scoped(grant, b.TxnId), type, Account(from), Account(Need(b.To)), Need(b.Amount), region, b.ObjectId, b.ObjectName), ct));
        });
        Post(app, "/money/v1/payout", c, async (http, grant, ct) =>
        {
            var b = await Read<PayoutBody>(http, ct);
            var region = Need(b.RegionId);
            var request = new PayoutRequest(Scoped(grant, b.TxnId), Need(b.OwnerId), Account(Need(b.To)), Need(b.Amount), region, Need(b.ObjectId), Need(b.ObjectName));
            if (!grant.Regions.Contains(region)) return Code("wrong_region");
            return Of(await c.Ledger.PayoutAsync(request, ct));
        });
        Post(app, "/money/v1/charge", c, async (http, grant, ct) =>
        {
            var b = await Read<ChargeBody>(http, ct);
            var payer = Need(b.Payer);
            var request = new ChargeRequest(Scoped(grant, b.TxnId), Need(b.Type), Account(payer), Need(b.Amount), b.PayTo);
            return await WithSession(c, payer, grant, ct) ?? Of(await c.Ledger.ChargeAsync(request, ct));
        });
        Post(app, "/money/v1/hold", c, async (http, grant, ct) =>
        {
            var b = await Read<HoldBody>(http, ct);
            var buyer = Need(b.Buyer);
            var region = Need(b.RegionId);
            var request = new HoldRequest(Scoped(grant, b.TxnId), Need(b.Kind), Account(buyer), Account(Need(b.Seller)), Need(b.Amount), region, b.ObjectId, b.ObjectName);
            if (!grant.Regions.Contains(region)) return Code("wrong_region");
            // Holding's balance (ToBalance) is nobody's balance: it never leaves the service (plan 1 deferred finding).
            return await WithSession(c, buyer, grant, ct) ?? Of(await c.Ledger.HoldAsync(request, ct)) with { ToBalance = null };
        });
        // A purchase already paid into Holding still finishes after coins are switched off: the buyer gets the item or the
        // coins back.
        Post(app, "/money/v1/hold/complete", c, (http, grant, ct) => Settle(http, c, grant, complete: true, ct), whileOff: true);
        Post(app, "/money/v1/hold/return", c, (http, grant, ct) => Settle(http, c, grant, complete: false, ct), whileOff: true);
    }

    /// <summary>
    /// A Region Server pairing (money plan 3): <c>{ host, nonce, proof }</c>, where the proof is
    /// <see cref="PairingProof.Client"/> over this listener's own fingerprint. Needs no region key. Every failure, whatever
    /// its cause, is the same <c>{"code":"refused"}</c> and counts against the caller's address. Nothing sent is logged.
    /// </summary>
    private static async Task PairAsync(HttpContext http, Context c)
    {
        var ct = http.RequestAborted;
        object answer = Code("refused");
        try
        {
            var b = await Read<PairBody>(http, ct);
            var (host, nonce, proof) = ((b.Host ?? "").Trim(), b.Nonce ?? "", b.Proof ?? "");
            // Money still on (it may have been turned off since the service started), on a Grid Asset Server.
            if (c.Pairing is { } pairing && c.Update is { } update && c.Mode?.Invoke() is MoneyMode.Play or MoneyMode.Real
                && c.Settings().Plan == OwnerPlan.GridAsset && c.Fingerprint.Length > 0
                && host.Length is > 0 and <= 255 && SettingsValidator.HostnameError(host) is null && IsNonce(nonce) && proof.Length == 64
                && b.FirstPort is { } firstPort and >= 1 and <= 65535 && (b.MachineId ?? "").Length <= 64)
            {
                if (pairing.Redeem(code => PairingProof.Same(PairingProof.Client(code, c.Fingerprint, nonce, host), proof)) is { } code)
                {
                    var key = Secrets.NewPassword(MoneyKeys.KeyLength);
                    var machine = string.IsNullOrWhiteSpace(b.MachineId) ? null : b.MachineId.Trim();
                    var server = new PairedServer(Guid.NewGuid(), host, PairedServer.HashOf(key), DateTimeOffset.UtcNow, firstPort, machine);
                    // The same Region Server pairing again (its machine id), or another one on the same address and first
                    // port, replaces the old key: one key per server, and no two keys over the same ports.
                    bool Replaced(PairedServer p) =>
                        (machine is not null && string.Equals(p.MachineId, machine, StringComparison.Ordinal))
                        || (string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase) && p.FirstPort == firstPort);
                    var replaced = (c.Settings().Money.PairedServers ?? []).Where(Replaced).ToList();
                    var replacing = replaced.Count > 0;
                    try
                    {
                        update(s => s with
                        {
                            Money = s.Money with
                            {
                                // One address and first port, one server: pairing it again replaces its key. Servers behind
                                // one router share the address and differ by port (final review, Important 2).
                                PairedServers = [.. (s.Money.PairedServers ?? []).Where(p => !Replaced(p)), server],
                            },
                        });
                        c.Log.LogInformation("Paired a Region Server at {Host}, region ports from {FirstPort}{Replacing}.", LogText.Clean(host), firstPort,
                            replacing ? ", replacing its earlier pairing" : "");
                        answer = new PairAnswer("ok", key, PairingProof.Server(code, c.Fingerprint, nonce, key));
                        // Pins follow the same machine (money design §8.3); a different machine at that address gets none. Only
                        // after the settings hold the new key: if this fails the pairing stands, and the regions stay pinned to
                        // the old server id, so they show as moves until the owner approves them (money stays off there).
                        if (c.Pins is { } pins)
                            foreach (var old in replaced)
                            {
                                try
                                {
                                    await pins.CarryOverAsync(old, server, ct);
                                }
                                catch (MoneyUnavailableException e)
                                {
                                    c.Log.LogError("A Region Server's pins couldn't be carried over to its new pairing ({Error}); its regions wait for approval.",
                                        e.GetType().Name);
                                }
                            }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        // The code is used up; the owner makes a new one. Never a 500 with the key nowhere saved.
                        c.Log.LogError("A Region Server's pairing couldn't be saved ({Error}); make a new pairing code.", e.GetType().Name);
                    }
                }
            }
        }
        catch (Exception e) when (e is BadRequest or JsonException or NotSupportedException)
        {
            // Refused like any other failure.
        }
        if (answer is not PairAnswer)
        {
            var wait = c.Failures.RecordFailure(http.Connection.RemoteIpAddress ?? IPAddress.None);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        }
        http.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(http.Response.Body, answer, answer.GetType(), Json, ct);
    }

    /// <summary>Base64 of exactly 32 bytes.</summary>
    private static bool IsNonce(string nonce)
    {
        Span<byte> buffer = stackalloc byte[48];
        return nonce.Length <= 64 && Convert.TryFromBase64String(nonce, buffer, out var n) && n == 32;
    }

    /// <summary>One endpoint's work, given its authenticated key's grant; returns the answer to write.</summary>
    private delegate Task<object> Endpoint(HttpContext http, RegionKeyGrant grant, CancellationToken ct);

    private static void Post(IEndpointRouteBuilder app, string path, Context c, Endpoint endpoint, bool whileOff = false) =>
        app.MapPost(path, (RequestDelegate)(http => Handle(http, c, endpoint, whileOff)));

    /// <summary>
    /// Whether coins are on now. The service starts only in Play or Real, but the owner may switch to Off or Gloebit while it
    /// runs, for instance because a region looks compromised; payments stop at once, not at the next restart (money design
    /// §10; money review 2026-11-15, L-6). Without a mode to ask (tests), coins are on.
    /// </summary>
    private static bool CoinsOn(Context c) => c.Mode is null || c.Mode() is MoneyMode.Play or MoneyMode.Real;

    private static async Task<object> Settle(HttpContext http, Context c, RegionKeyGrant grant, bool complete, CancellationToken ct)
    {
        var b = await Read<SettleBody>(http, ct);
        var (txn, hold) = (Scoped(grant, b.TxnId), Need(b.HoldId));
        // Only the region that made a hold may settle it.
        if (await c.Ledger.HoldRegionAsync(hold, ct) is not { } region) return Code("unknown_hold");
        if (!grant.Regions.Contains(region)) return Code("wrong_region");
        // Balance would be Holding's; ToBalance is the seller's on complete and the buyer's on return.
        return Of(complete ? await c.Ledger.CompleteHoldAsync(txn, hold, ct) : await c.Ledger.ReturnHoldAsync(txn, hold, ct)) with { Balance = null };
    }

    /// <summary>
    /// Authenticates, runs the endpoint and writes its answer. Nothing here logs what was sent: no key, no session id, no body.
    /// </summary>
    private static async Task Handle(HttpContext http, Context c, Endpoint endpoint, bool whileOff)
    {
        var ct = http.RequestAborted;
        var header = http.Request.Headers.Authorization.ToString();
        var key = header.StartsWith(Scheme, StringComparison.Ordinal) ? header[Scheme.Length..] : null;
        // The key first: a valid key is never slowed down or refused because of someone else's wrong ones.
        if (!RegionKeyRing.FromSettings(c.Settings()).TryAuthenticate(key, out var grant) || grant is null)
        {
            var wait = c.Failures.RecordFailure(http.Connection.RemoteIpAddress ?? IPAddress.None);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        object answer;
        if (!whileOff && !CoinsOn(c))
        {
            http.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(http.Response.Body, Code("unavailable"), Json, ct);
            return;
        }
        try
        {
            // A paired Region Server's key covers the regions Robust lists at its address that are pinned to it (money design
            // §8.3), never this computer's own.
            if (grant.ServerHost is not null)
            {
                if (c.Pins is null) throw new MoneyUnavailableException(new InvalidOperationException("no region pins"));
                var own = c.Settings().Regions.Select(r => r.Uuid).ToHashSet();
                var server = c.Settings().Money.PairedServers?.FirstOrDefault(p => "server:" + p.Id.ToString("N") == grant.KeyId);
                grant = grant with
                {
                    Regions = server is null ? new HashSet<Guid>() : await c.Pins.CoveredAsync(server, grant.FromPort, grant.ToPort, own, ct),
                };
            }
            answer = await endpoint(http, grant, ct);
        }
        catch (Exception e) when (e is BadRequest or JsonException or NotSupportedException)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            answer = Code("bad_request");
        }
        catch (MoneyUnavailableException)
        {
            answer = Code("unavailable");
        }
        catch (BadHttpRequestException e)
        {
            // Kestrel's own refusal while the body is read: a body over MaxBodyBytes is a 413, not a failure of ours (money
            // review 2026-11-15, found by the L-5 fuzz test).
            http.Response.StatusCode = e.StatusCode;
            return;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.Log.LogError("A money request failed with {Error}.", e.GetType().Name);
            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }
        http.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(http.Response.Body, answer, answer.GetType(), Json, ct);
    }

    private static async Task<T> Read<T>(HttpContext http, CancellationToken ct) where T : class =>
        await JsonSerializer.DeserializeAsync<T>(http.Request.Body, Json, ct) ?? throw new BadRequest();

    /// <summary>Null when <paramref name="w"/>'s session is live in one of the key's regions; otherwise the refusal.</summary>
    private static async Task<Answer?> WithSession(Context c, Who w, RegionKeyGrant grant, CancellationToken ct) =>
        await c.Presence.CheckAsync(Need(w.AgentId), Need(w.SessionId), Need(w.SecureSessionId), grant.Regions, ct) switch
        {
            PresenceOutcome.Ok => null,
            PresenceOutcome.WrongRegion => Code("wrong_region"),
            PresenceOutcome.Unavailable => Code("unavailable"),
            _ => Code("no_session"),
        };

    /// <summary>
    /// The region's transaction id, made unique to its key: two programs that happen to use the same id never see each
    /// other's answers (review M2).
    /// </summary>
    private static Guid Scoped(RegionKeyGrant grant, Guid? txn) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(grant.KeyId + "\n" + Need(txn).ToString("D"))).AsSpan(0, 16));

    private static AccountRef Account(Who w) => new(Need(w.AgentId), w.Home ?? "");

    private static Answer Of(MoneyResult r) =>
        new(JsonNamingPolicy.SnakeCaseLower.ConvertName(r.Outcome.ToString()), r.FromBalance, r.ToBalance, r.EntryId, r.HoldId);

    private static Answer Code(string code) => new(code);

    private static T Need<T>(T? value) where T : class => value ?? throw new BadRequest();

    private static T Need<T>(T? value) where T : struct => value ?? throw new BadRequest();
}
