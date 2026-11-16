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
    /// <summary>A module's handoff (money design §8.4); <c>Kind</c> is <c>teleport</c>, <c>crossing</c> or <c>other_grid</c>.</summary>
    internal sealed record HandoffBody(Guid? Id, Guid? SessionId, Guid? FromRegion, Guid? ToRegion, string? Kind);
    internal sealed record CancelBody(Guid? SessionId, Guid? FromRegion);
    internal sealed record ArrivedBody(Guid? Id, Guid? SessionId, Guid? AgentId, Guid? RegionId);

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
    /// the certificate this listener serves. Without <see cref="Verification"/> and <see cref="Locations"/> nobody can pay
    /// (money design §8.5): every payment, balance, handoff and arrival is <c>unavailable</c>.
    /// </summary>
    internal sealed record Context(MoneyLedger Ledger, PresenceCheck Presence, Func<LauncherSettings> Settings, MoneyAuthFailures Failures, ILogger Log,
        MoneyPairing? Pairing = null, Func<Func<LauncherSettings, LauncherSettings>, LauncherSettings>? Update = null, RegionPins? Pins = null,
        string Fingerprint = "", Func<MoneyMode>? Mode = null, RegionVerification? Verification = null, MoneyLocations? Locations = null)
    {
        /// <summary>How many <c>/arrived</c> calls each key (by key id) has open now; see <see cref="MaxOpenArrivals"/>.</summary>
        public ConcurrentDictionary<string, int> OpenArrivals { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// An arrival nothing vouches for yet is held up to <see cref="MoneyLocations.ArrivalWait"/> (money design §8.4), looking
    /// again every <see cref="MoneyLocations.ArrivalRetry"/>. A key may have this many open at once; past that it is refused
    /// before anything is looked up, so a key spamming arrivals can't take the database's connections.
    /// </summary>
    internal const int MaxOpenArrivals = 20;

    public static void Map(IEndpointRouteBuilder app, Context c)
    {
        app.MapPost("/money/v1/pair", (RequestDelegate)(http => PairAsync(http, c)));
        // How many regions the key covers now: a Region Server's page says when the grid sees none of its regions. The call
        // also records which money protocol the program speaks and which of its covered regions it runs (money design §8.2);
        // A body without regions is a Region Server launcher's pairing check (it shares the server's key): answered, not
        // recorded, so it never overwrites what a module reported. Regions without a protocol are protocol 1.
        Post(app, "/money/v1/health", c, async (http, grant, ct) =>
        {
            var b = await Read<HealthBody>(http, ct);
            var protocol = b.Protocol ?? 1;
            if (protocol < 1) throw new BadRequest();
            if (c.Verification is { } verification && b.Regions is not null)
                await verification.HeardAsync(grant.KeyId, protocol, b.Regions.Where(grant.Regions.Contains).ToList(), ct);
            return new HealthAnswer(true, grant.Regions.Count);
        });
        Post(app, "/money/v1/economy", c, (_, _, _) =>
        {
            var m = c.Settings().Money;
            return Task.FromResult<object>(new EconomyAnswer("ok", m.CurrencySymbol, m.UploadFee, m.ClassifiedFee, m.EffectiveVisitors != VisitorMoney.Off, m.ScriptPayouts, m.MaxPayment));
        });
        // Reading a balance moves nothing: the avatar must be logged in and placed in one of the key's regions, verified or
        // not, so a region can show someone their balance while money is paused for them.
        Post(app, "/money/v1/balance", c, async (http, grant, ct) =>
        {
            var w = await Read<Who>(http, ct);
            return await WithViewer(c, w, grant, ct) is { } refusal ? Code(refusal) : Of(await c.Ledger.BalanceAsync(Account(w), ct));
        });
        // Every payment checks its payer (money design §8.5). Receivers get no location check: they lose nothing by it and are
        // often offline; the ledger's visitor rule still applies to them.
        Post(app, "/money/v1/transfer", c, async (http, grant, ct) =>
        {
            var b = await Read<TransferBody>(http, ct);
            var type = Need(b.Type);
            if (!PaymentTypes.Contains(type)) throw new BadRequest();
            var (from, region) = (Need(b.From), Need(b.RegionId));
            var request = new TransferRequest(Scoped(grant, b.TxnId), type, Account(from), Account(Need(b.To)), Need(b.Amount), region, b.ObjectId, b.ObjectName, grant.KeyId);
            return await WithPayer(c, from, grant, region, ct) is { } refusal ? Code(refusal) : Of(await c.Ledger.TransferAsync(request, ct));
        });
        // A script payout has no payer in the region (the object's owner is usually offline): it needs a verified region.
        Post(app, "/money/v1/payout", c, async (http, grant, ct) =>
        {
            var b = await Read<PayoutBody>(http, ct);
            var region = Need(b.RegionId);
            var request = new PayoutRequest(Scoped(grant, b.TxnId), Need(b.OwnerId), Account(Need(b.To)), Need(b.Amount), region, Need(b.ObjectId), Need(b.ObjectName), grant.KeyId);
            if (!grant.Regions.Contains(region)) return Code("wrong_region");
            if (!await Verification(c).IsVerifiedAsync(grant, region, ct)) return Code("unverified_region");
            return Of(await c.Ledger.PayoutAsync(request, ct));
        });
        Post(app, "/money/v1/charge", c, async (http, grant, ct) =>
        {
            var b = await Read<ChargeBody>(http, ct);
            var (payer, region) = (Need(b.Payer), Need(b.RegionId));
            var request = new ChargeRequest(Scoped(grant, b.TxnId), Need(b.Type), Account(payer), Need(b.Amount), b.PayTo, grant.KeyId);
            return await WithPayer(c, payer, grant, region, ct) is { } refusal ? Code(refusal) : Of(await c.Ledger.ChargeAsync(request, ct));
        });
        Post(app, "/money/v1/hold", c, async (http, grant, ct) =>
        {
            var b = await Read<HoldBody>(http, ct);
            var (buyer, region) = (Need(b.Buyer), Need(b.RegionId));
            var request = new HoldRequest(Scoped(grant, b.TxnId), Need(b.Kind), Account(buyer), Account(Need(b.Seller)), Need(b.Amount), region, b.ObjectId, b.ObjectName, grant.KeyId);
            // Holding's balance (ToBalance) is nobody's balance: it never leaves the service (plan 1 deferred finding).
            return await WithPayer(c, buyer, grant, region, ct) is { } refusal ? Code(refusal) : Of(await c.Ledger.HoldAsync(request, ct)) with { ToBalance = null };
        });
        // Where avatars are (money design §8.4). These keep working while coins are off, so the record still follows everyone
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
            return Code(PlaceCode(await Locations(c).HandoffAsync(grant, Need(b.Id), Need(b.SessionId), Need(b.FromRegion), Need(b.ToRegion), kind, ct)));
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
            var result = await Locations(c).ArrivedAsync(grant, Need(b.SessionId), Need(b.AgentId), Need(b.RegionId), ct);
            if (result.Outcome is PlaceOutcome.Refused or PlaceOutcome.Conflict)
            {
                // Task 10: raise the alert (Conflict and a refusal with VerifiedElsewhere are warnings)
            }
            return Code(PlaceCode(result.Outcome));
        }, whileOff: true, arrival: true);
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

    /// <summary>Maps <paramref name="endpoint"/>; an <paramref name="arrival"/> counts against its key's <see cref="MaxOpenArrivals"/>.</summary>
    private static void Post(IEndpointRouteBuilder app, string path, Context c, Endpoint endpoint, bool whileOff = false, bool arrival = false) =>
        app.MapPost(path, (RequestDelegate)(http => Handle(http, c, endpoint, whileOff, arrival)));

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
    private static async Task Handle(HttpContext http, Context c, Endpoint endpoint, bool whileOff, bool arrival)
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

        if (!whileOff && !CoinsOn(c))
        {
            await WriteAsync(http, Code("unavailable"), ct);
            return;
        }
        if (!arrival)
        {
            await RunAsync(http, c, grant, endpoint, ct);
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
            await RunAsync(http, c, grant, endpoint, ct);
        }
        finally
        {
            c.OpenArrivals.AddOrUpdate(grant.KeyId, 0, (_, open) => open - 1);
        }
    }

    /// <summary>Runs an authenticated request's endpoint and writes its answer.</summary>
    private static async Task RunAsync(HttpContext http, Context c, RegionKeyGrant grant, Endpoint endpoint, CancellationToken ct)
    {
        object answer;
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
        await WriteAsync(http, answer, ct);
    }

    private static async Task WriteAsync(HttpContext http, object answer, CancellationToken ct)
    {
        http.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(http.Response.Body, answer, answer.GetType(), Json, ct);
    }

    private static async Task<T> Read<T>(HttpContext http, CancellationToken ct) where T : class =>
        await JsonSerializer.DeserializeAsync<T>(http.Request.Body, Json, ct) ?? throw new BadRequest();

    /// <summary>
    /// Null when coins may leave <paramref name="w"/> in <paramref name="regionId"/> (money design §8.5); otherwise the
    /// refusal's code. In order: logged in with both session ids (Presence, which only Robust writes); verified somewhere
    /// by the service's own record, never Presence's region; that place is one of the key's regions and the one the request
    /// names; and that region is verified.
    /// </summary>
    private static async Task<string?> WithPayer(Context c, Who w, RegionKeyGrant grant, Guid regionId, CancellationToken ct)
    {
        var (locations, verification) = (Locations(c), Verification(c));
        if (await LoggedIn(c, w, ct) is { } refusal) return refusal;
        if (await locations.WhereAsync(Need(w.SessionId), ct) is not { State: LocationState.Verified, RegionId: { } at }) return "unverified_avatar";
        if (!grant.Regions.Contains(at) || at != regionId) return "wrong_region";
        return await verification.IsVerifiedAsync(grant, regionId, ct) ? null : "unverified_region";
    }

    /// <summary>
    /// Null when <paramref name="w"/> may read their balance through this key: logged in, and placed in one of the key's
    /// regions in any state; otherwise the refusal's code.
    /// </summary>
    private static async Task<string?> WithViewer(Context c, Who w, RegionKeyGrant grant, CancellationToken ct)
    {
        var locations = Locations(c);
        if (await LoggedIn(c, w, ct) is { } refusal) return refusal;
        return await locations.WhereAsync(Need(w.SessionId), ct) is { RegionId: { } at } && grant.Regions.Contains(at) ? null : "wrong_region";
    }

    /// <summary>Null when <paramref name="w"/> is logged in with both session ids (a missing one is a 400); otherwise the refusal's code.</summary>
    private static async Task<string?> LoggedIn(Context c, Who w, CancellationToken ct) =>
        await c.Presence.LoggedInAsync(Need(w.AgentId), Need(w.SessionId), Need(w.SecureSessionId), ct) switch
        {
            PresenceOutcome.Ok => null,
            PresenceOutcome.Unavailable => "unavailable",
            _ => "no_session",
        };

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
    private static Guid Scoped(RegionKeyGrant grant, Guid? txn) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(grant.KeyId + "\n" + Need(txn).ToString("D"))).AsSpan(0, 16));

    private static AccountRef Account(Who w) => new(Need(w.AgentId), w.Home ?? "");

    private static Answer Of(MoneyResult r) =>
        new(JsonNamingPolicy.SnakeCaseLower.ConvertName(r.Outcome.ToString()), r.FromBalance, r.ToBalance, r.EntryId, r.HoldId);

    private static Answer Code(string code) => new(code);

    private static T Need<T>(T? value) where T : class => value ?? throw new BadRequest();

    private static T Need<T>(T? value) where T : struct => value ?? throw new BadRequest();
}
