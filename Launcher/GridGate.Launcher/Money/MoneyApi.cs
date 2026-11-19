using System.Collections.Concurrent;
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
    internal sealed record ChargeBody(Guid? TxnId, string? Type, Who? Payer, long? Amount, Guid? PayTo, Guid? RegionId);
    internal sealed record HoldBody(Guid? TxnId, string? Kind, Who? Buyer, Who? Seller, long? Amount, Guid? RegionId, Guid? ObjectId, string? ObjectName);
    internal sealed record SettleBody(Guid? TxnId, long? HoldId);
    /// <summary>
    /// A module's handoff; <c>Kind</c> is <c>teleport</c>, <c>crossing</c> or <c>other_grid</c>. <c>ToHost</c> and
    /// <c>ToPort</c> are the address and UDP port the sending region sent the avatar to; an older module sends neither.
    /// </summary>
    internal sealed record HandoffBody(Guid? Id, Guid? SessionId, Guid? FromRegion, Guid? ToRegion, string? Kind, string? ToHost = null, int? ToPort = null);
    internal sealed record CancelBody(Guid? SessionId, Guid? FromRegion);
    internal sealed record ArrivedBody(Guid? Id, Guid? SessionId, Guid? AgentId, Guid? RegionId);

    // Answers.
    internal sealed record Answer(string Code, long? Balance = null, long? ToBalance = null, long? EntryId = null, long? HoldId = null);
    /// <summary><see cref="FeeHolds"/>: <c>/hold</c> takes the fee kinds, so a module can hold a fee before its action.</summary>
    internal sealed record EconomyAnswer(string Code, string CurrencySymbol, long UploadFee, long ClassifiedFee, bool VisitorWallets, bool ScriptPayouts, long MaxPayment,
        bool FeeHolds);
    internal sealed record HealthBody(int? Protocol, Guid[]? Regions);
    internal sealed record HealthAnswer(bool Ok, int Regions);
    internal sealed record PairBody(string? Host, int? FirstPort, string? MachineId, string? Nonce, string? Proof);
    internal sealed record PairAnswer(string Code, string Key, string Proof);

    private sealed class BadRequest : Exception;

    /// <summary>
    /// What the endpoints need. <see cref="Pairing"/>, <see cref="Update"/> and <see cref="Pins"/> are for Region Servers
    /// (money plan 3): without them nobody can pair and a server key gets <c>unavailable</c>. <see cref="Fingerprint"/> is
    /// the certificate this listener serves. Without <see cref="Verification"/> and <see cref="Locations"/> nobody can pay:
    /// every payment, balance, handoff and arrival is <c>unavailable</c>.
    /// </summary>
    internal sealed record Context(MoneyLedger Ledger, PresenceCheck Presence, Func<LauncherSettings> Settings, MoneyAuthFailures Failures, ILogger Log,
        MoneyPairing? Pairing = null, Func<Func<LauncherSettings, LauncherSettings>, LauncherSettings>? Update = null, RegionPins? Pins = null,
        string Fingerprint = "", Func<MoneyMode>? Mode = null, RegionVerification? Verification = null, MoneyLocations? Locations = null,
        MoneyAlerts? Alerts = null)
    {
        /// <summary>How many <c>/arrived</c> calls each key (by key id) has open now; see <see cref="MaxOpenArrivals"/>.</summary>
        public ConcurrentDictionary<string, int> OpenArrivals { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// An arrival nothing vouches for yet is held up to <see cref="MoneyLocations.ArrivalWait"/>, looking
    /// again every <see cref="MoneyLocations.ArrivalRetry"/>. A key may have this many open at once; past that it is refused
    /// before anything is looked up, so a key spamming arrivals can't take the database's connections.
    /// </summary>
    internal const int MaxOpenArrivals = 20;

    public static void Map(IEndpointRouteBuilder app, Context c)
    {
        app.MapPost("/money/v1/pair", (RequestDelegate)(http => PairAsync(http, c)));
        // How many regions the key covers now: a Region Server's page says when the grid sees none of its regions. The call
        // also records which money protocol the program speaks and which of its covered regions it runs. A body without
        // regions is a Region Server launcher's pairing check (it shares the server's key): answered, not recorded, so it
        // never overwrites what a module reported. Regions without a protocol are protocol 1. Heard while coins are off
        // too, so regions are verified the moment coins come back on.
        Post(app, "/money/v1/health", c, async (http, grant, ct) =>
        {
            var b = await Read<HealthBody>(http, ct);
            var protocol = b.Protocol ?? 1;
            if (protocol < 1) throw new BadRequest();
            if (c.Verification is { } verification && b.Regions is not null)
                await verification.HeardAsync(grant.KeyId, protocol, b.Regions.Where(grant.Regions.Contains).ToList(), ct);
            return new HealthAnswer(true, grant.Regions.Count);
        }, whileOff: true);
        Post(app, "/money/v1/economy", c, (_, _, _) =>
        {
            var m = c.Settings().Money;
            return Task.FromResult<object>(new EconomyAnswer("ok", m.CurrencySymbol, m.UploadFee, m.ClassifiedFee, m.EffectiveVisitors != VisitorMoney.Off, m.ScriptPayouts, m.MaxPayment,
                FeeHolds: true));
        });
        // Reading a balance moves nothing: the avatar must be logged in and placed in one of the key's regions, verified or
        // not, so a region can show someone their balance while money is paused for them.
        Post(app, "/money/v1/balance", c, async (http, grant, ct) =>
        {
            var w = await Read<Who>(http, ct);
            return await WithViewer(c, w, grant, ct) is { } refusal ? Code(refusal) : Of(await c.Ledger.BalanceAsync(Account(w), ct));
        });
        // Every payment checks its payer. Receivers get no location check: they lose nothing by it and are
        // often offline; the ledger's visitor rule still applies to them. A repeat of a payment that went through (the same
        // key, transaction id and values: a module retrying a lost answer) gets its first answer before any other check,
        // coins being on included: the payer may have moved on, the region lost its verification or the key its coverage in
        // between, and the repeat moves nothing. The payee's balance is in a new answer only when the payee is placed
        // verified in a region this key covers.
        PostEarly(app, "/money/v1/transfer", c, async (http, grant, open, ct) =>
        {
            var b = await Read<TransferBody>(http, ct);
            var type = Need(b.Type);
            if (!PaymentTypes.Contains(type)) throw new BadRequest();
            var (from, region) = (Need(b.From), Need(b.RegionId));
            var request = new TransferRequest(Scoped(c, grant, b.TxnId), type, Account(from), Account(Need(b.To)), Need(b.Amount), region, b.ObjectId, b.ObjectName, grant.KeyId);
            if (await c.Ledger.EarlierAsync(request, ct) is { } earlier) return await Repeat(c, open, request.To.AgentId, earlier, ct);
            if (await open.OpenAsync(ct) is not { } covered) return Code("unavailable");
            return await WithPayer(c, from, covered, region, ct) is { } refusal
                ? Code(refusal)
                : await PayeeSeen(c, covered, request.To.AgentId, Of(await Watched(c, covered, request.From.AgentId, await c.Ledger.TransferAsync(request, ct))), ct);
        });
        // A script payout has no payer in the region (the object's owner is usually offline): it needs a verified region.
        PostEarly(app, "/money/v1/payout", c, async (http, grant, open, ct) =>
        {
            var b = await Read<PayoutBody>(http, ct);
            var region = Need(b.RegionId);
            var request = new PayoutRequest(Scoped(c, grant, b.TxnId), Need(b.OwnerId), Account(Need(b.To)), Need(b.Amount), region, Need(b.ObjectId), Need(b.ObjectName), grant.KeyId);
            if (await c.Ledger.EarlierAsync(request, ct) is { } earlier) return await Repeat(c, open, request.To.AgentId, earlier, ct);
            if (await open.OpenAsync(ct) is not { } covered) return Code("unavailable");
            if (!covered.Regions.Contains(region)) return Code("wrong_region");
            if (!await Verification(c).IsVerifiedAsync(covered, region, ct)) return Code("unverified_region");
            // The owner's payout limit isn't their spending limit: only the key's limit raises an alert here.
            var paid = await Watched(c, covered, null, await c.Ledger.PayoutAsync(request, ct));
            if (paid.Outcome == MoneyOutcome.Ok) await WatchPayoutOwners(c, grant, ct);
            return await PayeeSeen(c, covered, request.To.AgentId, Of(paid), ct);
        });
        PostEarly(app, "/money/v1/charge", c, async (http, grant, open, ct) =>
        {
            var b = await Read<ChargeBody>(http, ct);
            var (payer, region) = (Need(b.Payer), Need(b.RegionId));
            // A charge carries no region, so neither does its repeat check: the key is the unit of trust, and a same-key
            // repeat sent from another of its regions is answered from the store.
            var request = new ChargeRequest(Scoped(c, grant, b.TxnId), Need(b.Type), Account(payer), Need(b.Amount), b.PayTo, grant.KeyId);
            if (await c.Ledger.EarlierAsync(request, ct) is { } earlier) return await Repeat(c, open, request.PayTo, earlier, ct);
            if (await open.OpenAsync(ct) is not { } covered) return Code("unavailable");
            // A fee paid to the grid has no payee to show: Fees' balance is nobody's.
            return await WithPayer(c, payer, covered, region, ct) is { } refusal
                ? Code(refusal)
                : await PayeeSeen(c, covered, request.PayTo, Of(await Watched(c, covered, request.Payer.AgentId, await c.Ledger.ChargeAsync(request, ct))), ct);
        });
        // A purchase names its seller. A fee held before its action (an upload, a classified, a group join) gets the same
        // checks as a charge of it; only a join fee may name someone, the group's founder.
        PostEarly(app, "/money/v1/hold", c, async (http, grant, open, ct) =>
        {
            var b = await Read<HoldBody>(http, ct);
            var (buyer, region, kind) = (Need(b.Buyer), Need(b.RegionId), Need(b.Kind));
            AccountRef? seller = b.Seller is { } s ? Account(s) : MoneyLedger.IsFeeKind(kind) ? null : throw new BadRequest();
            var request = new HoldRequest(Scoped(c, grant, b.TxnId), kind, Account(buyer), seller, Need(b.Amount), region, b.ObjectId, b.ObjectName, grant.KeyId);
            // Holding's balance (ToBalance) is nobody's balance: it never leaves the service (plan 1 deferred finding).
            if (await c.Ledger.EarlierAsync(request, ct) is { } earlier) return Of(earlier) with { ToBalance = null };
            if (await open.OpenAsync(ct) is not { } covered) return Code("unavailable");
            return await WithPayer(c, buyer, covered, region, ct) is { } refusal
                ? Code(refusal)
                : Of(await Watched(c, covered, request.Buyer.AgentId, await c.Ledger.HoldAsync(request, ct))) with { ToBalance = null };
        });
        // Where avatars are. These keep working while coins are off, so the record still follows everyone
        // when they come back on.
        Post(app, "/money/v1/handoff", c, async (http, grant, ct) =>
        {
            var b = await Read<HandoffBody>(http, ct);
            var kind = Need(b.Kind) switch
            {
                "teleport" => HandoffKind.Teleport,
                "crossing" => HandoffKind.Crossing,
                "other_grid" => HandoffKind.OtherGrid,
                _ => throw new BadRequest(),
            };
            var (session, from) = (Need(b.SessionId), Need(b.FromRegion));
            var outcome = await Locations(c).HandoffAsync(grant, Need(b.Id), session, from, Need(b.ToRegion), kind, b.ToHost, b.ToPort, ct);
            // A handoff for a session this key doesn't hold: counted for the owner, never against the key.
            if (outcome == PlaceOutcome.NotHeld && c.Alerts is { } refused)
                await refused.UnhandedArrivalAsync(grant.KeyId, from, await AgentOf(c, session, ct), null, AlertToken);
            return Code(PlaceCode(outcome));
        }, whileOff: true);
        Post(app, "/money/v1/handoff/cancel", c, async (http, grant, ct) =>
        {
            var b = await Read<CancelBody>(http, ct);
            await Locations(c).CancelHandoffAsync(grant, Need(b.SessionId), Need(b.FromRegion), ct);
            return Code("ok");
        }, whileOff: true);
        Post(app, "/money/v1/arrived", c, async (http, grant, ct) =>
        {
            var b = await Read<ArrivedBody>(http, ct);
            _ = Need(b.Id); // the module's id for this report: required on the wire, not used by the service yet
            var (agent, region) = (Need(b.AgentId), Need(b.RegionId));
            var result = await Locations(c).ArrivedAsync(grant, Need(b.SessionId), agent, region, ct);
            // A claim nothing vouches for, against someone verified elsewhere: counted for the owner, never against the key,
            // so an honest server isn't slowed down for a lost handoff. A conflict names the other key too: either may be the
            // one that is wrong.
            // An arrival in a region the key doesn't cover is counted the same way.
            if (result.Outcome is PlaceOutcome.Refused or PlaceOutcome.Conflict or PlaceOutcome.NotCovered && c.Alerts is { } alerts)
                await alerts.UnhandedArrivalAsync(grant.KeyId, region, agent, result.VerifiedElsewhere?.KeyId, AlertToken);
            return Code(PlaceCode(result.Outcome));
        }, whileOff: true, arrival: true);
        // A purchase already paid into Holding still finishes after coins are switched off: the buyer gets the item or the
        // coins back.
        PostEarly(app, "/money/v1/hold/complete", c, (http, grant, open, ct) => Settle(http, c, grant, open, complete: true, ct), whileOff: true);
        PostEarly(app, "/money/v1/hold/return", c, (http, grant, open, ct) => Settle(http, c, grant, open, complete: false, ct), whileOff: true);
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
                        // Pins follow the same machine; a different machine at that address gets none. Only
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
                        // Regions Robust already lists at the new server wait for the owner to confirm them: pairing never
                        // hands a region over. The same machine's carried pins were confirmed already. If this fails the
                        // pairing stands and first sight pins those regions confirmed later.
                        if (c.Pins is { } waitingPins)
                        {
                            try
                            {
                                var after = (c.Settings().Money.PairedServers ?? []).ToList();
                                await waitingPins.PinWaitingAsync(server, server.FirstPort, RegionKeyRing.NextFirstPort(after, server), ct);
                            }
                            catch (MoneyUnavailableException e)
                            {
                                c.Log.LogWarning("A Region Server's listed regions couldn't be set to wait for confirmation at {Host} ({Error}); they are pinned when first seen.",
                                    LogText.Clean(host), e.GetType().Name);
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

    /// <summary>
    /// An endpoint that answers a stored repeat before anything else: it gets <see cref="Opening"/> to ask for the coins-on
    /// check and the key's coverage once the repeat check found nothing.
    /// </summary>
    private delegate Task<object> EarlyEndpoint(HttpContext http, RegionKeyGrant grant, Opening open, CancellationToken ct);

    /// <summary>
    /// What comes after a stored repeat's check: whether coins are on (unless the endpoint works while they are off) and the
    /// regions the key covers, which for a paired Region Server's key is a read of Robust's regions and the pins.
    /// </summary>
    private sealed class Opening(Context c, RegionKeyGrant grant, bool coinsNeeded)
    {
        /// <summary>The grant with the regions it covers now, whether coins are on or not.</summary>
        public Task<RegionKeyGrant> CoveredAsync(CancellationToken ct) => Covering(c, grant, ct);

        /// <summary>The grant with the regions it covers now; null when coins are off and this endpoint needs them on.</summary>
        public async Task<RegionKeyGrant?> OpenAsync(CancellationToken ct) =>
            coinsNeeded && !CoinsOn(c) ? null : await Covering(c, grant, ct);
    }

    private static void PostEarly(IEndpointRouteBuilder app, string path, Context c, EarlyEndpoint endpoint, bool whileOff = false)
    {
        Task<object> Run(HttpContext http, RegionKeyGrant grant, CancellationToken ct) => endpoint(http, grant, new Opening(c, grant, !whileOff), ct);
        app.MapPost(path, (RequestDelegate)(http => Handle(http, c, Run, whileOff, arrival: false, early: true)));
    }

    /// <summary>Maps <paramref name="endpoint"/>; an <paramref name="arrival"/> counts against its key's <see cref="MaxOpenArrivals"/>.</summary>
    private static void Post(IEndpointRouteBuilder app, string path, Context c, Endpoint endpoint, bool whileOff = false, bool arrival = false) =>
        app.MapPost(path, (RequestDelegate)(http => Handle(http, c, endpoint, whileOff, arrival)));

    /// <summary>
    /// Whether coins are on now. The service starts only in Play or Real, but the owner may switch to Off or Gloebit while it
    /// runs, for instance because a region looks compromised; payments stop at once, not at the next restart (money design
    /// §10; money review 2026-11-15, L-6). Without a mode to ask (tests), coins are on.
    /// </summary>
    private static bool CoinsOn(Context c) => c.Mode is null || c.Mode() is MoneyMode.Play or MoneyMode.Real;

    private static async Task<object> Settle(HttpContext http, Context c, RegionKeyGrant grant, Opening open, bool complete, CancellationToken ct)
    {
        var b = await Read<SettleBody>(http, ct);
        var (txn, hold) = (Scoped(c, grant, b.TxnId), Need(b.HoldId));
        // A repeat of a settlement that went through gets its first answer, whatever changed since; it moves nothing.
        if (await c.Ledger.EarlierAsync(txn, hold, complete, ct) is { } earlier)
            return complete ? await SellerSeen(c, open, hold, Of(earlier) with { Balance = null }, ct) : Of(earlier) with { Balance = null };
        var covered = await open.OpenAsync(ct) ?? grant;
        // Only the key that made a hold, in the region it was made in, may settle it. A hold whose key is gone is returned by
        // the expiry sweep.
        if (await c.Ledger.HoldMadeByAsync(hold, ct) is not { RegionId: { } region, KeyId: { } madeBy } || madeBy != grant.KeyId) return Code("unknown_hold");
        if (!covered.Regions.Contains(region)) return Code("wrong_region");
        // Balance would be Holding's; ToBalance is the seller's on complete and the buyer's on return. Coins off: a complete
        // that came after the sweep returned its hold takes nothing from the buyer.
        var result = complete ? await c.Ledger.CompleteHoldAsync(txn, hold, ct, coinsOn: CoinsOn(c)) : await c.Ledger.ReturnHoldAsync(txn, hold, ct);
        var answer = Of(result) with { Balance = null };
        if (!complete) return answer;
        // The seller wasn't paid: the owner is told, whatever the caller does with its answer.
        if (result.SaleUnpaid && c.Alerts is { } alerts)
            await alerts.SaleNotPaidAsync(grant.KeyId, region, await BuyerOf(c, hold, ct), AlertToken);
        return await SellerSeen(c, open, hold, answer, ct);
    }

    /// <summary>
    /// A complete's answer with the seller's balance only when the seller is placed verified in a region this key covers
    /// (the rule a transfer's payee has); otherwise none. When the hold's parties or the coverage can't be read, none.
    /// </summary>
    private static async Task<Answer> SellerSeen(Context c, Opening open, long hold, Answer answer, CancellationToken ct)
    {
        if (answer.ToBalance is null) return answer;
        try
        {
            if (await c.Ledger.HoldPartiesAsync(hold, ct) is { } parties)
                return await PayeeSeen(c, await open.CoveredAsync(ct), parties.Seller, answer, ct);
        }
        catch (MoneyUnavailableException)
        {
            // Told nothing.
        }
        return answer with { ToBalance = null };
    }

    /// <summary>The buyer of a hold, for an alert; null when it can't be read.</summary>
    private static async Task<Guid?> BuyerOf(Context c, long hold, CancellationToken ct)
    {
        try
        {
            return (await c.Ledger.HoldPartiesAsync(hold, ct))?.Buyer;
        }
        catch (MoneyUnavailableException)
        {
            return null;
        }
    }

    /// <summary>
    /// Authenticates, runs the endpoint and writes its answer. Nothing here logs what was sent: no key, no session id, no body.
    /// </summary>
    private static async Task Handle(HttpContext http, Context c, Endpoint endpoint, bool whileOff, bool arrival, bool early = false)
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

        // An early endpoint checks the coins itself, after it has looked for a stored repeat.
        if (!whileOff && !early && !CoinsOn(c))
        {
            await WriteAsync(http, Code("unavailable"), ct);
            return;
        }
        if (!arrival)
        {
            await RunAsync(http, c, grant, endpoint, early, ct);
            return;
        }
        // Counted before anything is read or looked up, a paired key's regions included.
        if (c.OpenArrivals.AddOrUpdate(grant.KeyId, 1, (_, open) => open + 1) > MaxOpenArrivals)
        {
            c.OpenArrivals.AddOrUpdate(grant.KeyId, 0, (_, open) => open - 1);
            await WriteAsync(http, Code("refused"), ct);
            return;
        }
        try
        {
            await RunAsync(http, c, grant, endpoint, early, ct);
        }
        finally
        {
            c.OpenArrivals.AddOrUpdate(grant.KeyId, 0, (_, open) => open - 1);
        }
    }

    /// <summary>Runs an authenticated request's endpoint and writes its answer.</summary>
    private static async Task RunAsync(HttpContext http, Context c, RegionKeyGrant grant, Endpoint endpoint, bool early, CancellationToken ct)
    {
        object answer;
        try
        {
            // An early endpoint asks for the coverage itself (Opening), once it has looked for a stored repeat.
            answer = await endpoint(http, early ? grant : await Covering(c, grant, ct), ct);
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
        await WriteAsync(http, answer, ct);
    }

    /// <summary>
    /// <paramref name="grant"/> with the regions it covers now. A paired Region Server's key covers the regions Robust lists at
    /// its address that are pinned to it, never this computer's own.
    /// </summary>
    private static async Task<RegionKeyGrant> Covering(Context c, RegionKeyGrant grant, CancellationToken ct)
    {
        if (grant.ServerHost is null) return grant;
        if (c.Pins is null) throw new MoneyUnavailableException(new InvalidOperationException("no region pins"));
        var own = c.Settings().Regions.Select(r => r.Uuid).ToHashSet();
        var server = c.Settings().Money.PairedServers?.FirstOrDefault(p => "server:" + p.Id.ToString("N") == grant.KeyId);
        return grant with
        {
            Regions = server is null ? new HashSet<Guid>() : await c.Pins.CoveredAsync(server, grant.FromPort, grant.ToPort, own, ct, raiseMoves: true),
        };
    }

    private static async Task WriteAsync(HttpContext http, object answer, CancellationToken ct)
    {
        if (!await ReadToEndAsync(http, ct)) return;
        http.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(http.Response.Body, answer, answer.GetType(), Json, ct);
    }

    /// <summary>
    /// Reads whatever of the body the endpoint left, so an answer never goes out with part of the body still unread: Kestrel
    /// would then close the connection while discarding the rest, and the client's next request on it would fail. False,
    /// with the 413 set, when the body is over <see cref="MoneyServiceHost.MaxBodyBytes"/>.
    /// </summary>
    private static async Task<bool> ReadToEndAsync(HttpContext http, CancellationToken ct)
    {
        try
        {
            await http.Request.Body.CopyToAsync(Stream.Null, ct);
            return true;
        }
        catch (BadHttpRequestException e)
        {
            http.Response.StatusCode = e.StatusCode;
            return false;
        }
    }

    private static async Task<T> Read<T>(HttpContext http, CancellationToken ct) where T : class =>
        await JsonSerializer.DeserializeAsync<T>(http.Request.Body, Json, ct) ?? throw new BadRequest();

    /// <summary>
    /// Null when coins may leave <paramref name="w"/> in <paramref name="regionId"/>; otherwise the
    /// refusal's code. In order: logged in with both session ids (Presence, which only Robust writes); verified somewhere
    /// by the service's own record, never Presence's region; that place is one of the key's regions and the one the request
    /// names; this key placed them there; and that region is verified.
    /// </summary>
    private static async Task<string?> WithPayer(Context c, Who w, RegionKeyGrant grant, Guid regionId, CancellationToken ct)
    {
        var (locations, verification) = (Locations(c), Verification(c));
        if (await LoggedIn(c, w, ct) is { } refusal) return refusal;
        if (await locations.WhereAsync(Need(w.SessionId), ct) is not { State: LocationState.Verified, RegionId: { } at } place) return "unverified_avatar";
        if (!grant.Regions.Contains(at) || at != regionId) return "wrong_region";
        // A region can come to another key (a server removed and another paired in its place, an approved move, a region
        // moved between programs): a key charges only people it placed, never someone another key vouched for.
        if (place.KeyId != grant.KeyId) return "unverified_avatar";
        return await verification.IsVerifiedAsync(grant, regionId, ct) ? null : "unverified_region";
    }

    /// <summary>
    /// Null when <paramref name="w"/> may read their balance through this key: logged in, and placed in one of the key's
    /// regions, verified or not. A session placed unverified is the key's to read only when this key placed it because a
    /// handoff or a start vouched for the arrival, not on its own claim; otherwise the refusal's code.
    /// </summary>
    private static async Task<string?> WithViewer(Context c, Who w, RegionKeyGrant grant, CancellationToken ct)
    {
        var locations = Locations(c);
        if (await LoggedIn(c, w, ct) is { } refusal) return refusal;
        if (await locations.WhereAsync(Need(w.SessionId), ct) is not { RegionId: { } at } place || !grant.Regions.Contains(at)) return "wrong_region";
        return place.State == LocationState.Unverified && !(place.Matched && place.KeyId == grant.KeyId) ? "wrong_region" : null;
    }

    /// <summary>
    /// <paramref name="answer"/> with the payee's balance only when <paramref name="payee"/> is placed verified in a region
    /// <paramref name="grant"/> covers; otherwise a region would learn the balance of anyone it can make a present avatar
    /// pay. A payment that went through still answers when the record can't be read: without the balance.
    /// </summary>
    private static async Task<Answer> PayeeSeen(Context c, RegionKeyGrant grant, Guid? payee, Answer answer, CancellationToken ct)
    {
        if (answer.ToBalance is null) return answer;
        try
        {
            if (payee is { } id && await Locations(c).WhereAgentAsync(id, ct) is { State: LocationState.Verified, RegionId: { } at } && grant.Regions.Contains(at))
                return answer;
        }
        catch (MoneyUnavailableException)
        {
            // Told nothing.
        }
        return answer with { ToBalance = null };
    }

    /// <summary>
    /// A stored repeat's answer: the first answer, with the payee's balance only if they are placed verified in a region the
    /// key covers now. When coverage can't be read, no balance.
    /// </summary>
    private static async Task<Answer> Repeat(Context c, Opening open, Guid? payee, MoneyResult earlier, CancellationToken ct)
    {
        var answer = Of(earlier);
        if (answer.ToBalance is null) return answer;
        try
        {
            return await PayeeSeen(c, await open.CoveredAsync(ct), payee, answer, ct);
        }
        catch (MoneyUnavailableException)
        {
            return answer with { ToBalance = null };
        }
    }

    /// <summary>The person a session belongs to as far as the service knows; nobody (all zeros) when it never placed it or can't be read.</summary>
    private static async Task<Guid> AgentOf(Context c, Guid session, CancellationToken ct)
    {
        try
        {
            return await Locations(c).WhereAsync(session, ct) is { } place ? place.AgentId : Guid.Empty;
        }
        catch (MoneyUnavailableException)
        {
            return Guid.Empty;
        }
    }

    /// <summary>Null when <paramref name="w"/> is logged in with both session ids (a missing one is a 400); otherwise the refusal's code.</summary>
    private static async Task<string?> LoggedIn(Context c, Who w, CancellationToken ct) =>
        await c.Presence.LoggedInAsync(Need(w.AgentId), Need(w.SessionId), Need(w.SecureSessionId), ct) switch
        {
            PresenceOutcome.Ok => null,
            PresenceOutcome.Unavailable => "unavailable",
            _ => "no_session",
        };

    /// <summary>
    /// An alert is written even when the caller hangs up first, so dropping the connection can't hide a claim. Raising one
    /// never fails or changes an answer (<see cref="MoneyAlerts"/> logs a failure and goes on).
    /// </summary>
    private static CancellationToken AlertToken => CancellationToken.None;

    /// <summary>
    /// Raises the alert a payment's answer calls for: a key over its daily limit, or
    /// <paramref name="payer"/> over their daily spending limit. Returns <paramref name="r"/> unchanged.
    /// </summary>
    private static async Task<MoneyResult> Watched(Context c, RegionKeyGrant grant, Guid? payer, MoneyResult r)
    {
        if (c.Alerts is not { } alerts) return r;
        if (r.Outcome == MoneyOutcome.OverKeyLimit)
            await alerts.KeyLimitReachedAsync(grant.KeyId, AlertToken);
        else if (r.Outcome == MoneyOutcome.OverDailyLimit && payer is { } agent)
            await alerts.PersonLimitHitAsync(agent, grant.KeyId, AlertToken);
        return r;
    }

    /// <summary>
    /// After a payout went through: when the key has now paid out for more than <see cref="MoneyRules.PayoutOwnersAlertAbove"/>
    /// different owners in a day, the owner is told once. Never fails or changes the answer; a database that can't be read
    /// raises nothing.
    /// </summary>
    private static async Task WatchPayoutOwners(Context c, RegionKeyGrant grant, CancellationToken ct)
    {
        if (c.Alerts is not { } alerts) return;
        try
        {
            if (await c.Ledger.KeyPayoutOwnersAsync(grant.KeyId, ct) > MoneyRules.PayoutOwnersAlertAbove)
                await alerts.ManyPayoutOwnersAsync(grant.KeyId, AlertToken);
        }
        catch (MoneyUnavailableException)
        {
            // The payout stands; the next one looks again.
        }
    }

    /// <summary>The answer to a handoff or an arrival. Conflict is an unverified place, as the arriving region sees it.</summary>
    private static string PlaceCode(PlaceOutcome outcome) => outcome switch
    {
        PlaceOutcome.Ok => "ok",
        PlaceOutcome.PlacedUnverified or PlaceOutcome.Conflict => "unverified_avatar",
        PlaceOutcome.Refused => "refused",
        PlaceOutcome.NoSession => "no_session",
        PlaceOutcome.NotCovered => "wrong_region",
        PlaceOutcome.NotHeld => "not_held",
        PlaceOutcome.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static MoneyLocations Locations(Context c) =>
        c.Locations ?? throw new MoneyUnavailableException(new InvalidOperationException("no location record"));

    private static RegionVerification Verification(Context c) =>
        c.Verification ?? throw new MoneyUnavailableException(new InvalidOperationException("no region verification"));

    /// <summary>
    /// The region's transaction id, made unique to its key: two programs that happen to use the same id never see each
    /// other's answers (review M2).
    /// </summary>
    private static Guid Scoped(Context c, RegionKeyGrant grant, Guid? txn) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(MoneyRules.TxnScope(c.Settings().Money, grant) + "\n" + Need(txn).ToString("D"))).AsSpan(0, 16));

    private static AccountRef Account(Who w) => new(Need(w.AgentId), w.Home ?? "");

    private static Answer Of(MoneyResult r) =>
        new(JsonNamingPolicy.SnakeCaseLower.ConvertName(r.Outcome.ToString()), r.FromBalance, r.ToBalance, r.EntryId, r.HoldId);

    private static Answer Code(string code) => new(code);

    private static T Need<T>(T? value) where T : class => value ?? throw new BadRequest();

    private static T Need<T>(T? value) where T : struct => value ?? throw new BadRequest();
}
