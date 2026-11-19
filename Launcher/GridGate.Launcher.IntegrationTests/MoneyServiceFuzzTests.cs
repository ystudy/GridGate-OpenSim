using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GridGate.Launcher.Core.Money;
using Xunit;
using static GridGate.Launcher.IntegrationTests.MoneyServiceRig;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// Structure-aware fuzzing of the money service (money design §12; money review 2026-11-15, L-5). The old test glued random
/// tokens together, so almost every body stopped at the JSON parser. These start from a valid body for each endpoint and
/// change one thing: a field set to a hostile value, removed, duplicated, retyped or nested deep, or an unknown field added.
/// Every request carries a valid key, so a 401 fails the test. Answers must be 200 (with a well-formed answer), 400 or 413,
/// never 500. After each run the books must still be sound.
///
/// <para>The normal run uses a fixed seed. For a longer one, set <c>GRIDGATE_MONEY_FUZZ_SEED</c> (any number, or
/// <c>random</c>) and <c>GRIDGATE_MONEY_FUZZ_CASES</c>; every failure names the seed and the case.</para>
/// </summary>
[Collection(GridCollection.Name)]
public sealed class MoneyServiceFuzzTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>, IAsyncDisposable
{
    private const int FixedSeed = 20261115;
    private const int DefaultCases = 120;

    private MoneyServiceRig? _rig;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_rig is not null) await _rig.DisposeAsync();
    }

    private static int Seed() =>
        Environment.GetEnvironmentVariable("GRIDGATE_MONEY_FUZZ_SEED") switch
        {
            null or "" => FixedSeed,
            "random" => RandomNumberGenerator.GetInt32(int.MaxValue),
            var s => int.Parse(s),
        };

    private static int Cases() =>
        int.TryParse(Environment.GetEnvironmentVariable("GRIDGATE_MONEY_FUZZ_CASES"), out var n) && n > 0 ? n : DefaultCases;

    /// <summary>Values that have broken parsers and ledgers elsewhere: sizes, Unicode, controls, extreme numbers, odd GUIDs.</summary>
    private static readonly JsonNode?[] Hostile =
    [
        null, true, false, "", " ", "x", new string('a', 300), new string('é', 5_000), new string('‮', 64),
        "\u0000", "\u001f\u0007", "​‍", "😀", "á́́", "﻿pay_avatar", "PAY_AVATAR",
        "http://othér.example:8002", "HTTP://OTHER.EXAMPLE:8002/", "system:bank", "system:holding", "javascript:alert(1)",
        "${Network|ConsolePass}", "'; DROP TABLE entries; --", "%00", "../../etc/passwd",
        0, -1, 1, 2, -9_223_372_036_854_775_807L, long.MaxValue, long.MinValue, int.MaxValue, 1_000_000, 1_000_001,
        JsonNode.Parse("1e308"), JsonNode.Parse("-0"), JsonNode.Parse("1.5"), JsonNode.Parse("99999999999999999999999"),
        "1", "-1", "00000000-0000-0000-0000-000000000000", "A0000000-0000-0000-0000-00000000000A",
        "{a0000000-0000-0000-0000-00000000000a}", "a00000000000000000000000000000000a", "a0000000-0000-0000-0000-00000000000",
        "a0000000-0000-0000-0000-00000000000ag", "not-a-guid", new JsonArray(), new JsonArray(1, 2), new JsonObject(),
        new JsonObject { ["agentId"] = A.ToString() },
    ];

    /// <summary>A valid body for each endpoint key p1 may call, in region Here, for avatar A.</summary>
    private static Dictionary<string, Func<long, JsonObject>> Templates() => new()
    {
        ["balance"] = _ => Who(),
        ["transfer"] = _ => new JsonObject
        {
            ["txnId"] = Guid.NewGuid().ToString(), ["type"] = "pay_avatar", ["from"] = Who(), ["to"] = new JsonObject { ["agentId"] = B.ToString(), ["home"] = "" },
            ["amount"] = 2, ["regionId"] = Here.ToString(), ["objectId"] = Guid.NewGuid().ToString(), ["objectName"] = "tip jar",
        },
        ["payout"] = _ => new JsonObject
        {
            ["txnId"] = Guid.NewGuid().ToString(), ["ownerId"] = A.ToString(), ["to"] = new JsonObject { ["agentId"] = B.ToString(), ["home"] = "" },
            ["amount"] = 1, ["regionId"] = Here.ToString(), ["objectId"] = Guid.NewGuid().ToString(), ["objectName"] = "slot",
        },
        ["charge"] = _ => new JsonObject { ["txnId"] = Guid.NewGuid().ToString(), ["type"] = "upload_fee", ["payer"] = Who(), ["amount"] = 3, ["regionId"] = Here.ToString() },
        ["hold"] = _ => new JsonObject
        {
            ["txnId"] = Guid.NewGuid().ToString(), ["kind"] = "object", ["buyer"] = Who(), ["seller"] = new JsonObject { ["agentId"] = B.ToString(), ["home"] = "" },
            ["amount"] = 1, ["regionId"] = Here.ToString(), ["objectId"] = Guid.NewGuid().ToString(), ["objectName"] = "dress",
        },
        // A fee held before its action: no seller.
        ["hold#fee"] = _ => new JsonObject
        {
            ["txnId"] = Guid.NewGuid().ToString(), ["kind"] = "upload_fee", ["buyer"] = Who(), ["amount"] = 3, ["regionId"] = Here.ToString(),
        },
        ["hold/complete"] = hold => new JsonObject { ["txnId"] = Guid.NewGuid().ToString(), ["holdId"] = hold },
        // A complete for a hold the expiry sweep already returned: the sale is taken again (the id it is given is a swept hold's).
        ["hold/complete#late"] = hold => new JsonObject { ["txnId"] = Guid.NewGuid().ToString(), ["holdId"] = hold },
        ["hold/return"] = hold => new JsonObject { ["txnId"] = Guid.NewGuid().ToString(), ["holdId"] = hold },
        ["economy"] = _ => new JsonObject(),
        ["health"] = _ => new JsonObject(),
        // A handoff from Here to Here: A stays in p1's region, handed there, and the arrival there verifies A again at once, so
        // a change to it can't wait for a handoff.
        ["handoff"] = _ => new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["sessionId"] = SessionA.ToString(), ["fromRegion"] = Here.ToString(), ["toRegion"] = Here.ToString(), ["kind"] = "teleport",
        },
        ["handoff/cancel"] = _ => new JsonObject { ["sessionId"] = SessionA.ToString(), ["fromRegion"] = Here.ToString() },
        ["arrived"] = _ => new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["sessionId"] = SessionA.ToString(), ["agentId"] = A.ToString(), ["regionId"] = Here.ToString(),
        },
    };

    /// <summary>The endpoint a template is for: a template key may carry a <c>#</c> and a variant name after it.</summary>
    private static string PathOf(string template) => template.Split('#')[0];

    /// <summary>A purchase of one coin that the expiry sweep returned, for a late complete to take again.</summary>
    private static async Task<long> SweptHold(MoneyServiceRig rig)
    {
        var hold = (await rig.Ledger.HoldAsync(new HoldRequest(Guid.NewGuid(), "object", AccountRef.Resident(A), AccountRef.Resident(B), 1, Here, null, "dress", "p1"), Ct)).HoldId!.Value;
        await rig.Db.Sql($"UPDATE `money`.`holds` SET created = created - INTERVAL 10 MINUTE WHERE id = {hold}");
        Assert.True(await rig.Ledger.ReturnExpiredHoldsAsync(Ct) >= 1);
        return hold;
    }

    private static async Task<bool> StillSwept(MoneyServiceRig rig, long hold) =>
        await rig.Db.Text($"SELECT state FROM `money`.`holds` WHERE id = {hold}") == "returned"
        && await rig.Db.Scalar($"SELECT COUNT(*) FROM `money`.`entries` WHERE hold_id = {hold} AND type = '{EntryType.LateSale}'") == 0;

    private static JsonObject Who() => new()
    {
        ["agentId"] = A.ToString(), ["home"] = "", ["sessionId"] = SessionA.ToString(), ["secureSessionId"] = SecureA.ToString(),
    };

    /// <summary>Every place in <paramref name="node"/> a value sits, as (parent, property name).</summary>
    private static List<(JsonObject Parent, string Name)> Places(JsonObject node)
    {
        List<(JsonObject, string)> places = [];
        foreach (var (name, value) in node.ToList())
        {
            places.Add((node, name));
            if (value is JsonObject inner) places.AddRange(Places(inner));
        }
        return places;
    }

    /// <summary>One change to <paramref name="body"/>, as the text to send and a few words for a failure message.</summary>
    private static (string Text, string What) Mutate(JsonObject body, Random rng)
    {
        var places = Places(body);
        var kind = places.Count == 0 ? 3 : rng.Next(8);
        var (parent, name) = places.Count == 0 ? (body, "") : places[rng.Next(places.Count)];
        switch (kind)
        {
            case 0:
            case 1:
            case 2:
                var value = Hostile[rng.Next(Hostile.Length)]?.DeepClone();
                parent[name] = value;
                return (body.ToJsonString(), $"{name} = {value?.ToJsonString() ?? "null"}");
            case 3:
                body["unexpected" + rng.Next(3)] = Hostile[rng.Next(Hostile.Length)]?.DeepClone();
                return (body.ToJsonString(), "an unknown field");
            case 4:
                parent.Remove(name);
                return (body.ToJsonString(), $"{name} removed");
            case 5:
                // A duplicate property: the second value is hostile.
                var text = body.ToJsonString();
                var dup = $"\"{name}\":{Hostile[rng.Next(Hostile.Length)]?.ToJsonString() ?? "null"},";
                return (text.Insert(1, dup), $"{name} twice");
            case 6:
                // Deep nesting in place of a value.
                var depth = rng.Next(2) == 0 ? 70 : 2_000;
                var nested = new StringBuilder();
                for (var i = 0; i < depth; i++) nested.Append("{\"a\":");
                nested.Append('1').Append('}', depth);
                parent[name] = 0;
                return (body.ToJsonString().Replace($"\"{name}\":0", $"\"{name}\":{nested}"), $"{name} nested {depth} deep");
            default:
                // A body around the size limit.
                parent[name] = new string('z', rng.Next(2) == 0 ? 15_000 : 17_000);
                return (body.ToJsonString(), $"{name} near the size limit");
        }
    }

    private static void AssertWellFormed(MoneyServiceRig rig, string path, (HttpStatusCode Status, JsonNode? Body, string Raw) answer, string context)
    {
        Assert.True(answer.Status is HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge,
            $"{context}: {(int)answer.Status} {answer.Raw} | {string.Join(" | ", rig.Logs.TakeLast(3))}");
        if (answer.Status != HttpStatusCode.OK) return;
        Assert.True(answer.Body is JsonObject, $"{context}: not a JSON object: {answer.Raw}");
        if (path != "health")
            Assert.True(answer.Body!["code"]?.GetValueKind() == JsonValueKind.String, $"{context}: no code: {answer.Raw}");
    }

    [Fact]
    public async Task One_changed_field_never_breaks_an_endpoint_or_the_books()
    {
        var rig = _rig = await MoneyServiceRig.StartAsync(maria);
        await rig.Ledger.SetPayoutsAllowedAsync(A, true, Ct);
        await rig.Ledger.GiveAsync(AccountRef.Resident(A), 100_000, new StaffAction("Owner Person", "fuzz float"), Ct);
        var seed = Seed();
        var rng = new Random(seed);
        var templates = Templates();
        // Settling needs a hold to point at; each settle uses up one, so a fresh one is made when it's gone.
        async Task<long> OpenHold() =>
            (await rig.Ledger.HoldAsync(new HoldRequest(Guid.NewGuid(), "object", AccountRef.Resident(A), AccountRef.Resident(B), 1, Here, null, "dress", "p1"), Ct)).HoldId!.Value;
        var hold = await OpenHold();
        var late = await SweptHold(rig);
        // An upload fee paid through a hold and given back by the owner, so the books the fuzz ends on hold a fee refund.
        var fee = await rig.Ledger.HoldAsync(new HoldRequest(Guid.NewGuid(), "upload_fee", AccountRef.Resident(A), null, 3, Here, null, null, "p1"), Ct);
        Assert.Equal(MoneyOutcome.Ok, (await rig.Ledger.CompleteHoldAsync(Guid.NewGuid(), fee.HoldId!.Value, Ct)).Outcome);
        Assert.Equal(MoneyOutcome.Ok, (await rig.Ledger.RefundHeldFeeAsync(AccountRef.Resident(A), fee.EntryId!.Value, new StaffAction("Owner Person", "fuzz fee refund"), Ct)).Outcome);

        var cases = Cases();
        foreach (var (name, template) in templates)
        {
            var path = PathOf(name);
            for (var i = 0; i < cases; i++)
            {
                if (await rig.Db.Text($"SELECT state FROM `money`.`holds` WHERE id = {hold}") != "open") hold = await OpenHold();
                if (!await StillSwept(rig, late)) late = await SweptHold(rig);
                var (text, what) = Mutate(template(name.EndsWith("#late", StringComparison.Ordinal) ? late : hold), rng);
                var context = $"seed {seed}, {name} case {i} ({what})";
                AssertWellFormed(rig, path, await rig.Post(path, text), context);
            }
        }

        await rig.AssertBooksSoundAsync($"seed {seed}:");
        Assert.Equal("ok", CodeOf(await rig.Post("balance", WhoA())));
        Assert.DoesNotContain(rig.Logs, l => l.StartsWith("Error", StringComparison.Ordinal) && l.Contains("failed with"));
    }

    [Fact]
    public async Task Unchanged_templates_succeed()
    {
        // The fuzz test's starting bodies are valid: otherwise every case would stop at the first check.
        var rig = _rig = await MoneyServiceRig.StartAsync(maria);
        await rig.Ledger.SetPayoutsAllowedAsync(A, true, Ct);
        var hold = (await rig.Ledger.HoldAsync(new HoldRequest(Guid.NewGuid(), "object", AccountRef.Resident(A), AccountRef.Resident(B), 1, Here, null, "dress", "p1"), Ct)).HoldId!.Value;

        var late = await SweptHold(rig);
        foreach (var (name, template) in Templates().Where(t => t.Key != "hold/return"))
        {
            var path = PathOf(name);
            var answer = await rig.Post(path, template(name.EndsWith("#late", StringComparison.Ordinal) ? late : hold).ToJsonString());
            Assert.True(answer.Status == HttpStatusCode.OK && (path == "health" || CodeOf(answer) == "ok"), $"{name}: {answer.Raw}");
        }
        await rig.AssertBooksSoundAsync();
    }

    /// <summary>Authorization headers that must never authenticate, whatever the body.</summary>
    public static TheoryData<string> WrongHeaders() =>
    [
        "",
        "GridGateRegion",
        "GridGateRegion ",
        "GridGateRegion  " + Key,
        "GridGateRegion " + Key + "x",
        "GridGateRegion " + Key[..^1],
        "GridGateRegion " + Key.ToUpperInvariant(),
        "gridgateregion " + Key,
        "GridGateRegion\t" + Key,
        "Bearer " + Key,
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("p1:" + Key)),
        Key,
        "GridGateRegion " + Key + "," + Key2,
        "GridGateRegion " + new string('k', 8_000),
        "GridGateRegion %00" + Key,
    ];

    [Fact]
    public async Task A_body_over_the_size_limit_is_413_on_every_endpoint()
    {
        // Found by the fuzz test above: Kestrel refused the body while the handler read it, and the handler answered 500.
        var rig = _rig = await MoneyServiceRig.StartAsync(maria);
        var big = new JsonObject { ["agentId"] = A.ToString(), ["home"] = new string('z', (int)GridGate.Launcher.Money.MoneyServiceHost.MaxBodyBytes) }.ToJsonString();

        // economy and health use nothing from their body, and still read it: an answer sent with most of a big body unread
        // left the connection to close under the next request.
        foreach (var path in Templates().Keys.Select(PathOf).Distinct().Append("pair"))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await rig.Post(path, big, key: path == "pair" ? null : Key)).Status);
        Assert.Equal("ok", CodeOf(await rig.Post("balance", WhoA())));
        Assert.DoesNotContain(rig.Logs, l => l.Contains("failed with"));
    }

    /// <remarks>
    /// A trailing space isn't here: HTTP drops white space at the ends of a header value, so <c>"GridGateRegion key "</c>
    /// arrives as the right key.
    /// </remarks>
    [Theory]
    [MemberData(nameof(WrongHeaders))]
    public async Task A_wrong_authorization_header_is_always_401(string header)
    {
        var rig = _rig = await MoneyServiceRig.StartAsync(maria);

        foreach (var path in Templates().Keys.Select(PathOf).Distinct())
        {
            var answer = await rig.Post(path, WhoA(), authorization: header);
            Assert.True(answer.Status == HttpStatusCode.Unauthorized, $"{path} with [{header[..Math.Min(header.Length, 20)]}…{header[Math.Max(0, header.Length - 12)..]}] ({header.Length} chars): {(int)answer.Status} {answer.Raw}");
            Assert.Equal("", answer.Raw);
            rig.FailureClock.Advance(TimeSpan.FromMinutes(11));
        }
        // Someone else's wrong keys never slow down or refuse a right one.
        Assert.Equal("ok", CodeOf(await rig.Post("balance", WhoA())));
    }

    [Fact]
    public async Task A_changed_pairing_request_never_pairs()
    {
        // /pair needs no key and is open to the whole network. Each case has a fresh code and a body that would pair, with
        // the host, nonce or proof changed; the proof covers those three, so none may pair.
        var rig = _rig = await MoneyServiceRig.StartAsync(maria);
        var seed = Seed();
        var rng = new Random(seed);
        string[] proven = ["host", "nonce", "proof"];

        for (var i = 0; i < Math.Max(60, Cases() / 2); i++)
        {
            var (code, _) = rig.Pairing.Issue();
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            const string host = "192.168.1.20";
            var body = new JsonObject
            {
                ["host"] = host, ["firstPort"] = 9000, ["machineId"] = "rs1", ["nonce"] = nonce,
                ["proof"] = PairingProof.Client(code, rig.Fingerprint, nonce, host),
            };
            var field = proven[rng.Next(proven.Length)];
            var value = rng.Next(3) switch
            {
                0 => Hostile[rng.Next(Hostile.Length)]?.DeepClone(),
                1 => JsonValue.Create(Flip((string)body[field]!, rng)),
                _ => null,
            };
            if (value is null && rng.Next(2) == 0) body.Remove(field);
            else body[field] = value;
            var context = $"seed {seed}, pair case {i} ({field} = {value?.ToJsonString() ?? "removed"})";

            var answer = await rig.Post("pair", body.ToJsonString(), key: null);

            Assert.True(answer.Status is HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge, $"{context}: {(int)answer.Status}");
            if (answer.Status == HttpStatusCode.OK) Assert.True(CodeOf(answer) == "refused", $"{context}: {answer.Raw}");
            if (i % 15 == 14) rig.FailureClock.Advance(TimeSpan.FromMinutes(11));
        }
        Assert.Empty(rig.Settings.Money.PairedServers ?? []);
    }

    [Fact]
    public async Task A_changed_pairing_body_without_a_code_never_pairs()
    {
        var rig = _rig = await MoneyServiceRig.StartAsync(maria);
        var seed = Seed();
        var rng = new Random(seed);

        for (var i = 0; i < Math.Max(60, Cases() / 2); i++)
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var body = new JsonObject
            {
                ["host"] = "192.168.1.20", ["firstPort"] = 9000, ["machineId"] = "rs1", ["nonce"] = nonce,
                ["proof"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            };
            var (text, what) = Mutate(body, rng);

            var answer = await rig.Post("pair", text, key: null);

            Assert.True(answer.Status is HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge,
                $"seed {seed}, pair case {i} ({what}): {(int)answer.Status}");
            if (answer.Status == HttpStatusCode.OK) Assert.Equal("refused", CodeOf(answer));
            if (i % 15 == 14) rig.FailureClock.Advance(TimeSpan.FromMinutes(11));
        }
        Assert.Empty(rig.Settings.Money.PairedServers ?? []);
    }

    /// <summary>
    /// <paramref name="s"/> with one character changed to another letter, not just its case: a proof is hex, and hex digits
    /// compare without case.
    /// </summary>
    private static string Flip(string s, Random rng)
    {
        if (s.Length == 0) return "x";
        var chars = s.ToCharArray();
        var i = rng.Next(chars.Length);
        chars[i] = char.ToLowerInvariant(chars[i]) == 'a' ? 'b' : 'a';
        return new string(chars);
    }
}
