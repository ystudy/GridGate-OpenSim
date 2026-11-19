using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Economy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// The economy helper's XML-RPC reader under hostile input: a seeded mutation fuzz and the hand-made cases the money review
/// found untested (money review 2026-11-15, H-7). The reader is the anonymous parser every viewer talks to, so it must
/// never throw and the helper must always answer 200 with a well-formed fault or struct.
/// </summary>
public sealed class XmlRpcFuzzTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Agent = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Secure = Guid.Parse("60000000-0000-0000-0000-000000000006");

    /// <summary>The normal run's seed; set GG_FUZZ_SEED (and GG_FUZZ_CASES) to replay a failure or to run longer.</summary>
    private const int DefaultSeed = 20261115;
    private const int DefaultCases = 4000;

    private static int Seed => int.TryParse(Environment.GetEnvironmentVariable("GG_FUZZ_SEED"), out var s) ? s : DefaultSeed;
    private static int Cases => int.TryParse(Environment.GetEnvironmentVariable("GG_FUZZ_CASES"), out var n) && n > 0 ? n : DefaultCases;

    private static Stream Body(string xml) => new MemoryStream(Encoding.UTF8.GetBytes(xml));
    private static Stream Body(byte[] bytes) => new MemoryStream(bytes);

    private static string Call(string method, string members) =>
        $"<?xml version=\"1.0\"?><methodCall><methodName>{method}</methodName><params><param><value><struct>{members}</struct></value></param></params></methodCall>";

    private static string Member(string name, string value) => $"<member><name>{name}</name><value>{value}</value></member>";

    private static string Who(string language = "en") =>
        Member("agentId", $"<string>{Agent}</string>") + Member("secureSessionId", $"<string>{Secure}</string>") + Member("language", $"<string>{language}</string>");

    // ---- the seeds: calls the helper accepts, as Firestorm sends them (from XmlRpcTests and EconomyHelperTests) ----

    private static readonly string[] Seeds =
    [
        """
        <?xml version="1.0"?>
        <methodCall><methodName>getCurrencyQuote</methodName><params><param><value><struct>
        <member><name>agentId</name><value><string>a0000000-0000-0000-0000-00000000000a</string></value></member>
        <member><name>secureSessionId</name><value><string>60000000-0000-0000-0000-000000000006</string></value></member>
        <member><name>language</name><value><string>de</string></value></member>
        <member><name>currencyBuy</name><value><int>250</int></value></member>
        <member><name>viewerChannel</name><value><string>Firestorm-Releasex64</string></value></member>
        <member><name>viewerMajorVersion</name><value><int>7</int></value></member>
        <member><name>viewerMinorVersion</name><value><i4>1</i4></value></member>
        <member><name>viewerPatchVersion</name><value><int>11</int></value></member>
        <member><name>viewerBuildVersion</name><value><string>76000</string></value></member>
        </struct></value></param></params></methodCall>
        """,
        Call("buyCurrency", Who() + Member("currencyBuy", "<int>100</int>") + Member("estimatedCost", "<int>0</int>") + Member("confirm", "<string></string>")),
        Call("preflightBuyLandPrep", Who("fr") + Member("currencyBuy", "<int>0</int>") + Member("billableArea", "<int>512</int>")),
        Call("preflightBuyLandPrep", Who() + Member("currencyBuy", "<int>25</int>") + Member("billableArea", "<int>512</int>")),
        Call("buyLandPrep", Who("es") + Member("currencyBuy", "<int>25</int>") + Member("final", "<boolean>1</boolean>")),
        Call("getCurrencyQuote", Who() + Member("ratio", "<double>2.5</double>") + Member("plain", "untyped")
            + Member("nested", "<struct>" + Member("inner", "<struct>" + Member("x", "<i4>-5</i4>") + "</struct>") + "</struct>")),
        "<methodCall><methodName>getCurrencyQuote</methodName></methodCall>",
    ];

    private static readonly byte[][] SeedBytes = Seeds.Select(Encoding.UTF8.GetBytes).ToArray();

    // What gets inserted: markup that breaks or bends the structure, entities, encodings and bytes that aren't UTF-8.
    private static readonly byte[][] Fragments =
    [
        .. new[]
        {
            "<", ">", "&", "&amp;", "&lt;", "&#0;", "&#x0;", "&#xD800;", "&#x10FFFF;", "&#1114112;", "&nope;", "&x;", "]]>", "<![CDATA[<int>1</int>]]>",
            "<!-- c -->", "<?pi x?>", "<?xml version=\"1.0\" encoding=\"UTF-16\"?>", "<?xml version=\"1.0\" encoding=\"x-nope\"?>",
            "<!DOCTYPE m [<!ENTITY x \"y\">]>", "<!DOCTYPE m SYSTEM \"http://127.0.0.1:1/x.dtd\">", "<!ENTITY % p \"q\">",
            "<struct>", "</struct>", "<value>", "</value>", "<member>", "</member>", "<name>", "</name>", "<params>", "<param>",
            "<member><name>agentId</name><value><string>not-a-guid</string></value></member>",
            "<member><name>currencyBuy</name><value><int>2147483647</int></value></member>",
            "<member><name>currencyBuy</name><value><int>-2147483648</int></value></member>",
            "<int>2147483648</int>", "<int>0x10</int>", "<int> 7 </int>", "<double>NaN</double>", "<double>1e400</double>", "<boolean>2</boolean>",
            "<boolean>true</boolean>", "<array><data><value>1</value></data></array>", "<base64>AAAA</base64>", "<dateTime.iso8601>x</dateTime.iso8601>",
            "<nil/>", "<string/>", "<value/>", " xmlns=\"urn:x\"", " xmlns:a=\"urn:a\"", "a:", " a=\"1\" a=\"2\"", "\0", "\t", "\r\n",
        }.Select(Encoding.UTF8.GetBytes),
        [0xEF, 0xBB, 0xBF], [0xFF, 0xFE], [0xFE, 0xFF], [0xFF], [0xC3, 0x28], [0xC0, 0xAF], [0xED, 0xA0, 0x80], [0xF0, 0x9F, 0x98], [0xF4, 0x90, 0x80, 0x80],
        [0xE9], [0x80],
    ];

    private static readonly string[] Tags = ["member", "value", "struct", "param", "params", "name", "string", "int", "methodName", "methodCall"];

    private static byte[] Mutate(Random rng, byte[] input)
    {
        var b = new List<byte>(input);
        var rounds = 1 + rng.Next(4);
        for (var i = 0; i < rounds; i++)
        {
            switch (rng.Next(9))
            {
                case 0 when b.Count > 0: // flip one bit
                    b[rng.Next(b.Count)] ^= (byte)(1 << rng.Next(8));
                    break;
                case 1 when b.Count > 0: // any byte
                    b[rng.Next(b.Count)] = (byte)rng.Next(256);
                    break;
                case 2: // truncate
                    var cut = rng.Next(b.Count + 1);
                    b.RemoveRange(cut, b.Count - cut);
                    break;
                case 3: // insert a fragment
                    b.InsertRange(rng.Next(b.Count + 1), Fragments[rng.Next(Fragments.Length)]);
                    break;
                case 4: // duplicate a whole element
                case 5: // delete a whole element
                    if (Element(b, Tags[rng.Next(Tags.Length)], rng) is { } span)
                    {
                        var copy = b.GetRange(span.Start, span.Length);
                        if (rng.Next(2) == 0) b.RemoveRange(span.Start, span.Length);
                        else b.InsertRange(span.Start + span.Length, copy);
                    }
                    break;
                case 6: // duplicate or delete a span between two '<'
                    var lts = Enumerable.Range(0, b.Count).Where(k => b[k] == (byte)'<').ToList();
                    if (lts.Count >= 2)
                    {
                        var x = lts[rng.Next(lts.Count)];
                        var y = lts[rng.Next(lts.Count)];
                        (x, y) = (Math.Min(x, y), Math.Max(x, y));
                        if (rng.Next(2) == 0) b.RemoveRange(x, y - x);
                        else b.InsertRange(y, b.GetRange(x, y - x));
                    }
                    break;
                case 7: // a huge value: under the cap, at it, and over it
                    var at = b.IndexOf((byte)'>', rng.Next(b.Count + 1)) is var gt and >= 0 ? gt + 1 : b.Count;
                    var length = new[] { 100, 1000, 8000, 16000, XmlRpc.MaxBytes, 17000 }[rng.Next(6)];
                    var fill = new[] { '9', 'a', ' ', '-', 'é', '世' }[rng.Next(6)];
                    b.InsertRange(at, Encoding.UTF8.GetBytes(new string(fill, length)));
                    break;
                default: // splice in a piece of another seed
                    var other = SeedBytes[rng.Next(SeedBytes.Length)];
                    var from = rng.Next(other.Length);
                    var take = rng.Next(Math.Min(400, other.Length - from) + 1);
                    b.InsertRange(rng.Next(b.Count + 1), other.Skip(from).Take(take));
                    break;
            }
        }
        return b.ToArray();
    }

    /// <summary>A random <c>&lt;tag&gt;…&lt;/tag&gt;</c> in <paramref name="b"/> (the first close after the open), or null.</summary>
    private static (int Start, int Length)? Element(List<byte> b, string tag, Random rng)
    {
        var text = Encoding.Latin1.GetString(b.ToArray());
        var open = $"<{tag}>";
        var starts = new List<int>();
        for (var k = text.IndexOf(open, StringComparison.Ordinal); k >= 0; k = text.IndexOf(open, k + 1, StringComparison.Ordinal)) starts.Add(k);
        if (starts.Count == 0) return null;
        var start = starts[rng.Next(starts.Count)];
        var close = text.IndexOf($"</{tag}>", start, StringComparison.Ordinal);
        return close < 0 ? null : (start, close + tag.Length + 3 - start);
    }

    /// <summary>The fuzz inputs: every seed as it is, then <see cref="Cases"/> mutants, all from one seed.</summary>
    private static IEnumerable<byte[]> Inputs(int seed, int cases)
    {
        foreach (var s in SeedBytes) yield return s;
        var rng = new Random(seed);
        for (var i = 0; i < cases; i++) yield return Mutate(rng, SeedBytes[rng.Next(SeedBytes.Length)]);
    }

    private static string Replay(int seed, int index, byte[] input) =>
        $"seed {seed} (set GG_FUZZ_SEED={seed} to replay), case {index}, input (base64): {Convert.ToBase64String(input)}";

    /// <summary>What a read call may hold: a method, and members that are strings, ints, bools, doubles or structs, at most 4 deep.</summary>
    private static void AssertPlain(XmlRpc.Call call, string where)
    {
        Assert.False(string.IsNullOrEmpty(call.Method), where);
        Assert.Equal(call.Method.Trim(), call.Method);
        Walk(call.Params, 1);

        void Walk(IReadOnlyDictionary<string, object> members, int depth)
        {
            Assert.True(depth <= 4, $"nested {depth} deep; {where}");
            foreach (var (name, value) in members)
            {
                Assert.NotNull(name);
                switch (value)
                {
                    case string or int or bool or double:
                        break;
                    case IReadOnlyDictionary<string, object> inner:
                        Walk(inner, depth + 1);
                        break;
                    default:
                        Assert.Fail($"member {name} is a {value?.GetType().Name ?? "null"}; {where}");
                        break;
                }
            }
        }
    }

    /// <summary>
    /// A well-formed XML-RPC answer, the shape Firestorm reads: a fault struct with faultCode and faultString, or a params
    /// struct with success, errorMessage and errorURI; booleans 1 or 0, ints plain digits.
    /// </summary>
    private static void AssertAnswer(string xml, string where)
    {
        XElement root;
        try
        {
            root = XDocument.Parse(xml).Root!;
        }
        catch (XmlException e)
        {
            Assert.Fail($"the answer isn't XML ({e.Message}); {where}");
            return;
        }
        Assert.True(xml.StartsWith("<?xml version=\"1.0\"?>", StringComparison.Ordinal), where);
        Assert.True(root.Name.LocalName == "methodResponse", where);
        var top = Assert.Single(root.Elements());
        foreach (var b in root.Descendants("boolean")) Assert.True(b.Value is "0" or "1", $"boolean {b.Value}; {where}");
        foreach (var i in root.Descendants("int")) Assert.True(int.TryParse(i.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _), $"int {i.Value}; {where}");
        var value = top.Name.LocalName switch
        {
            "fault" => Assert.Single(top.Elements("value")),
            "params" => Assert.Single(Assert.Single(top.Elements("param")).Elements("value")),
            _ => null,
        };
        Assert.True(value is not null, $"top element {top.Name.LocalName}; {where}");
        var names = Assert.Single(value.Elements("struct")).Elements("member").Select(m => m.Element("name")?.Value).ToList();
        if (top.Name.LocalName == "fault")
            Assert.True(names is ["faultCode", "faultString"], $"fault members {string.Join(",", names)}; {where}");
        else
            Assert.True(names.Contains("success") && names.Contains("errorMessage") && names.Contains("errorURI"), $"members {string.Join(",", names)}; {where}");
    }

    private sealed class OkSessions : ISecureSessionCheck
    {
        public List<(Guid, Guid)> Asked { get; } = [];

        public Task<PresenceOutcome> HasSecureSessionAsync(Guid agentId, Guid secureSessionId, CancellationToken ct)
        {
            lock (Asked) Asked.Add((agentId, secureSessionId));
            return Task.FromResult(PresenceOutcome.Ok);
        }
    }

    private static LauncherFactory Helper(OkSessions sessions, MoneyMode mode = MoneyMode.Play) =>
        new(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Plan = OwnerPlan.Local,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Money = new MoneySettings(Mode: mode, StartingBalancePlay: 100),
            MoneyPort = 0,
        })
        {
            ExtraServices = services =>
            {
                services.RemoveAll<ISecureSessionCheck>();
                services.AddSingleton<ISecureSessionCheck>(sessions);
                services.RemoveAll<MoneyGate>();
                services.AddSingleton(sp => new MoneyGate(sp.GetRequiredService<Func<LauncherSettings>>(), MoneyGate.Open));
            },
        };

    private static async Task<(HttpStatusCode Status, string? Type, string Xml)> PostAsync(HttpClient c, string path, byte[] body)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new("text/xml");
        using var r = await c.PostAsync(path, content, Ct);
        return (r.StatusCode, r.Content.Headers.ContentType?.MediaType, await r.Content.ReadAsStringAsync(Ct));
    }

    // ---- the fuzz ----

    /// <summary>money review 2026-11-15, H-7: no mutant of a real call makes <see cref="XmlRpc.Read"/> throw, and what it reads is plain.</summary>
    [Fact]
    public void Mutated_calls_never_make_the_reader_throw()
    {
        var seed = Seed;
        var watch = Stopwatch.StartNew();
        var read = 0;
        var index = 0;
        foreach (var input in Inputs(seed, Cases))
        {
            XmlRpc.Call? call;
            try
            {
                call = XmlRpc.Read(Body(input));
            }
            catch (Exception e)
            {
                Assert.Fail($"XmlRpc.Read threw {e.GetType().Name}: {e.Message}; {Replay(seed, index, input)}");
                return;
            }
            if (call is not null)
            {
                read++;
                AssertPlain(call, Replay(seed, index, input));
            }
            index++;
        }

        // Every seed reads, and the mutants are a mix of calls that still read and calls that don't.
        Assert.True(read >= Seeds.Length, $"only {read} inputs read; seed {seed}");
        Assert.True(read < index, $"every mutant read; seed {seed}");
        // The normal run stays quick; a longer run asked for with GG_FUZZ_CASES may take what it takes.
        Assert.True(Cases != DefaultCases || watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}; seed {seed}");
    }

    /// <summary>money review 2026-11-15, H-7: the helper answers every mutant with a well-formed fault or struct, never an error.</summary>
    [Fact]
    public async Task Helper_answers_every_mutant_with_a_fault_or_a_struct()
    {
        var seed = Seed;
        await using var f = Helper(new OkSessions());
        var helper = f.Services.GetRequiredService<EconomyHelper>();
        var index = 0;
        foreach (var input in Inputs(seed, Cases))
        {
            var path = index % 2 == 0 ? EconomyHelper.CurrencyPath : EconomyHelper.LandPath;
            string xml;
            try
            {
                xml = await helper.AnswerAsync(path, Body(input), null, Ct);
            }
            catch (Exception e)
            {
                Assert.Fail($"the helper threw {e.GetType().Name}: {e.Message}; {path}; {Replay(seed, index, input)}");
                return;
            }
            AssertAnswer(xml, $"{path}; {Replay(seed, index, input)}");
            index++;
        }
    }

    /// <summary>money review 2026-11-15, H-7: over HTTP too, every mutant gets 200, text/xml and a well-formed fault or struct.</summary>
    [Fact]
    public async Task Helper_endpoint_answers_200_to_every_mutant()
    {
        var seed = Seed + 1;
        await using var f = Helper(new OkSessions());
        var c = f.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        var index = 0;
        foreach (var input in Inputs(seed, Math.Max(1, Cases / 8)))
        {
            var path = index % 2 == 0 ? EconomyHelper.CurrencyPath : EconomyHelper.LandPath;
            var (status, type, xml) = await PostAsync(c, path, input);
            var where = $"{path}; {Replay(seed, index, input)}";
            Assert.True(status == HttpStatusCode.OK, $"{(int)status}; {where}");
            Assert.True(type == "text/xml", $"{type}; {where}");
            AssertAnswer(xml, where);
            index++;
        }
    }

    // ---- entities and DTDs ----

    private const string Laughs = """
        <?xml version="1.0"?>
        <!DOCTYPE lolz [
          <!ENTITY lol "lol">
          <!ENTITY lol1 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
          <!ENTITY lol2 "&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;">
          <!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;">
          <!ENTITY lol4 "&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;">
          <!ENTITY lol5 "&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;">
          <!ENTITY lol6 "&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;">
          <!ENTITY lol7 "&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;">
          <!ENTITY lol8 "&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;">
          <!ENTITY lol9 "&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;">
        ]>
        <methodCall><methodName>getCurrencyQuote</methodName><params><param><value><struct>
        <member><name>language</name><value><string>&lol9;</string></value></member>
        </struct></value></param></params></methodCall>
        """;

    /// <summary>money review 2026-11-15, H-7: a billion-laughs internal subset is refused at once, not expanded.</summary>
    [Fact]
    public void Billion_laughs_is_refused_quickly()
    {
        var watch = Stopwatch.StartNew();

        Assert.Null(XmlRpc.Read(Body(Laughs)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
    }

    /// <summary>money review 2026-11-15, H-7: internal subsets of every kind are refused (general, parameter, attribute-list, quadratic blow-up).</summary>
    [Theory]
    [InlineData("<!DOCTYPE methodCall [<!ENTITY % p \"<!ENTITY x 'y'>\"> %p;]><methodCall><methodName>&x;</methodName></methodCall>")]
    [InlineData("<!DOCTYPE methodCall [<!ATTLIST methodCall a CDATA \"d\">]><methodCall><methodName>m</methodName></methodCall>")]
    [InlineData("<!DOCTYPE methodCall [<!ELEMENT methodCall ANY>]><methodCall><methodName>m</methodName></methodCall>")]
    [InlineData("<!DOCTYPE methodCall><methodCall><methodName>m</methodName></methodCall>")]
    public void Internal_subsets_are_refused(string xml) => Assert.Null(XmlRpc.Read(Body(xml)));

    /// <summary>money review 2026-11-15, H-7: one big entity used many times (the quadratic blow-up) is refused too.</summary>
    [Fact]
    public void Quadratic_blowup_is_refused()
    {
        var big = new string('a', 4000);
        var uses = string.Concat(Enumerable.Repeat("&a;", 2000));
        var xml = $"<!DOCTYPE m [<!ENTITY a \"{big}\">]><methodCall><methodName>m</methodName><params><param><value><struct>"
            + $"<member><name>x</name><value><string>{uses}</string></value></member></struct></value></param></params></methodCall>";

        Assert.True(Encoding.UTF8.GetByteCount(xml) < XmlRpc.MaxBytes);

        Assert.Null(XmlRpc.Read(Body(xml)));
    }

    /// <summary>money review 2026-11-15, H-7: an external DTD or an external entity is refused, and the file it names is never read into the call.</summary>
    [Fact]
    public void External_dtd_and_entity_are_refused()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gg-xxe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var secret = Path.Combine(dir, "secret.txt");
            File.WriteAllText(secret, "gg-not-for-viewers");
            var dtd = Path.Combine(dir, "evil.dtd");
            File.WriteAllText(dtd, $"<!ENTITY x SYSTEM \"{new Uri(secret).AbsoluteUri}\">");

            string[] calls =
            [
                $"<?xml version=\"1.0\"?><!DOCTYPE methodCall SYSTEM \"{new Uri(dtd).AbsoluteUri}\"><methodCall><methodName>&x;</methodName></methodCall>",
                $"<?xml version=\"1.0\"?><!DOCTYPE methodCall PUBLIC \"-//x//y\" \"{new Uri(dtd).AbsoluteUri}\"><methodCall><methodName>m</methodName></methodCall>",
                $"<?xml version=\"1.0\"?><!DOCTYPE m [<!ENTITY x SYSTEM \"{new Uri(secret).AbsoluteUri}\">]><methodCall><methodName>getCurrencyQuote</methodName>"
                    + "<params><param><value><struct><member><name>language</name><value><string>&x;</string></value></member></struct></value></param></params></methodCall>",
                $"<?xml version=\"1.0\"?><!DOCTYPE m [<!ENTITY % d SYSTEM \"{new Uri(dtd).AbsoluteUri}\"> %d;]><methodCall><methodName>&x;</methodName></methodCall>",
                "<?xml version=\"1.0\"?><!DOCTYPE methodCall SYSTEM \"http://127.0.0.1:1/evil.dtd\"><methodCall><methodName>m</methodName></methodCall>",
            ];
            foreach (var xml in calls) Assert.Null(XmlRpc.Read(Body(xml)));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>money review 2026-11-15, H-7: the helper turns a DTD into a fault, and nothing from the entity reaches the answer.</summary>
    [Fact]
    public async Task Helper_answers_a_dtd_with_a_fault()
    {
        var sessions = new OkSessions();
        await using var f = Helper(sessions);
        var c = f.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });

        var (status, _, xml) = await PostAsync(c, EconomyHelper.CurrencyPath, Encoding.UTF8.GetBytes(Laughs));

        Assert.Equal(HttpStatusCode.OK, status);
        AssertAnswer(xml, "billion laughs");
        Assert.Contains("<fault>", xml);
        Assert.DoesNotContain("lol", xml);
        Assert.Empty(sessions.Asked);
    }

    // ---- encodings ----

    private static readonly string Quote = Call("getCurrencyQuote", Who("fr") + Member("currencyBuy", "<int>250</int>") + Member("note", "<string>café 世</string>"));

    /// <summary>UTF-16 (either byte order, with its BOM) is refused: the helper reads UTF-8 only.</summary>
    [Theory]
    [InlineData("utf-16LE")]
    [InlineData("utf-16BE")]
    public void Utf16_with_a_bom_is_refused(string encoding)
    {
        var e = Encoding.GetEncoding(encoding);
        var xml = Quote.Replace("<?xml version=\"1.0\"?>", "<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
        var bytes = e.GetPreamble().Concat(e.GetBytes(xml)).ToArray();

        Assert.Null(XmlRpc.Read(Body(bytes)));
    }

    /// <summary>A declared ISO-8859-1 body with a character outside ASCII is refused.</summary>
    [Fact]
    public void Latin1_declared_with_accents_is_refused()
    {
        var xml = Call("getCurrencyQuote", Who("fr") + Member("note", "<string>café</string>"))
            .Replace("<?xml version=\"1.0\"?>", "<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?>");

        Assert.Null(XmlRpc.Read(Body(Encoding.Latin1.GetBytes(xml))));
    }

    /// <summary>
    /// money review 2026-11-15, H-7: encodings that are unknown, not installed, or don't match the bytes are refused, never
    /// thrown.
    /// </summary>
    [Theory]
    [InlineData("utf-7")]
    [InlineData("UTF-32")]
    [InlineData("x-no-such-encoding")]
    [InlineData("UTF-16")]
    [InlineData("")]
    public void Odd_declared_encodings_are_refused(string encoding)
    {
        var xml = Quote.Replace("<?xml version=\"1.0\"?>", $"<?xml version=\"1.0\" encoding=\"{encoding}\"?>");

        Assert.Null(XmlRpc.Read(Body(Encoding.ASCII.GetBytes(xml))));
    }

    /// <summary>
    /// money review 2026-11-15, H-7: a code page .NET knows only once something registers CodePagesEncodingProvider (a
    /// library elsewhere in the test run does). Either way the body is refused or read as the same call, never thrown or
    /// misread.
    /// </summary>
    [Theory]
    [InlineData("windows-1252")]
    [InlineData("ibm037")]
    public void Code_page_encodings_are_refused_or_read_the_same(string encoding)
    {
        var xml = Quote.Replace("<?xml version=\"1.0\"?>", $"<?xml version=\"1.0\" encoding=\"{encoding}\"?>");
        var plain = XmlRpc.Read(Body(Encoding.ASCII.GetBytes(Quote)));

        var call = XmlRpc.Read(Body(Encoding.ASCII.GetBytes(xml)));

        if (call is not null)
        {
            Assert.Equal(plain!.Method, call.Method);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(plain.Params), System.Text.Json.JsonSerializer.Serialize(call.Params));
        }
    }

    /// <summary>money review 2026-11-15, H-7: a UTF-32 body with its BOM neither throws nor reads as something else.</summary>
    [Fact]
    public void Utf32_with_a_bom_does_not_throw()
    {
        var bytes = Encoding.UTF32.GetPreamble().Concat(Encoding.UTF32.GetBytes(Quote)).ToArray();

        var call = XmlRpc.Read(Body(bytes));

        if (call is not null)
        {
            Assert.Equal("getCurrencyQuote", call.Method);
            Assert.Equal(Agent.ToString(), call.Params["agentId"]);
        }
    }

    /// <summary>money review 2026-11-15, H-7: bytes that aren't UTF-8 (stray, overlong, surrogate, cut short, past U+10FFFF) are refused.</summary>
    [Theory]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0xC3, 0x28 })]
    [InlineData(new byte[] { 0xC0, 0xAF })]
    [InlineData(new byte[] { 0xE0, 0x80, 0xAF })]
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]
    [InlineData(new byte[] { 0xF0, 0x9F, 0x98 })]
    [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 })]
    [InlineData(new byte[] { 0x80 })]
    public void Invalid_utf8_is_refused(byte[] bad)
    {
        var parts = Quote.Split("café");
        var bytes = Encoding.UTF8.GetBytes(parts[0]).Concat(bad).Concat(Encoding.UTF8.GetBytes(parts[1])).ToArray();

        Assert.Null(XmlRpc.Read(Body(bytes)));
    }

    /// <summary>money review 2026-11-15, H-7: the helper answers an invalid UTF-8 body with its "bad request" fault.</summary>
    [Fact]
    public async Task Helper_answers_invalid_utf8_with_a_fault()
    {
        await using var f = Helper(new OkSessions());
        var c = f.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        var parts = Quote.Split("café");
        var bytes = Encoding.UTF8.GetBytes(parts[0]).Concat(new byte[] { 0xC3, 0x28 }).Concat(Encoding.UTF8.GetBytes(parts[1])).ToArray();

        var (status, _, xml) = await PostAsync(c, EconomyHelper.CurrencyPath, bytes);

        Assert.Equal(HttpStatusCode.OK, status);
        AssertAnswer(xml, "invalid UTF-8");
        Assert.Contains("bad request", XDocument.Parse(xml).Root!.Value);
    }

    // ---- the wide struct ----

    /// <summary>A call with as many members as fit in <paramref name="bytes"/> bytes, padded with spaces to exactly that size.</summary>
    private static (string Xml, int Members) Wide(int bytes)
    {
        const string head = "<methodCall><methodName>m</methodName><params><param><value><struct>";
        const string tail = "</struct></value></param></params></methodCall>";
        var sb = new StringBuilder(head);
        var n = 0;
        while (true)
        {
            var next = Member("m" + n.ToString(CultureInfo.InvariantCulture), "<int>1</int>");
            if (sb.Length + next.Length + tail.Length > bytes) break;
            sb.Append(next);
            n++;
        }
        sb.Append(' ', bytes - sb.Length - tail.Length);
        return (sb + tail, n);
    }

    /// <summary>
    /// money review 2026-11-15, H-7: the reader has no member count of its own; the 16 KB body cap is what bounds a wide
    /// struct. A body of exactly <see cref="XmlRpc.MaxBytes"/> reads every member; one byte more is refused.
    /// </summary>
    [Fact]
    public void Wide_struct_reads_up_to_the_byte_cap_and_no_further()
    {
        var (fits, members) = Wide(XmlRpc.MaxBytes);
        Assert.Equal(XmlRpc.MaxBytes, Encoding.UTF8.GetByteCount(fits));

        var call = XmlRpc.Read(Body(fits));

        Assert.NotNull(call);
        Assert.Equal(members, call.Params.Count);
        Assert.True(members > 250, $"only {members} members fit");

        var over = fits.Replace("<methodName>m</methodName>", "<methodName>mm</methodName>");
        Assert.Equal(XmlRpc.MaxBytes + 1, Encoding.UTF8.GetByteCount(over));
        Assert.Null(XmlRpc.Read(Body(over)));
    }

    /// <summary>money review 2026-11-15, H-7: the cap counts bytes, so a struct of multi-byte characters can't get past it.</summary>
    [Fact]
    public void Wide_struct_of_multibyte_text_is_capped_by_bytes()
    {
        string Text(char c) => Call("m", Member("x", $"<string>{new string(c, 6000)}</string>"));
        Assert.True(Text('世').Length < XmlRpc.MaxBytes && Encoding.UTF8.GetByteCount(Text('世')) > XmlRpc.MaxBytes);

        Assert.NotNull(XmlRpc.Read(Body(Text('a'))));
        Assert.Null(XmlRpc.Read(Body(Text('世'))));
    }

    /// <summary>money review 2026-11-15, H-7: the helper refuses a body past the cap with its "bad request" fault, without asking about the session.</summary>
    [Fact]
    public async Task Helper_refuses_a_struct_past_the_cap()
    {
        var sessions = new OkSessions();
        await using var f = Helper(sessions);
        var c = f.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        var wide = Call("getCurrencyQuote", Who() + string.Concat(Enumerable.Range(0, 1000).Select(i => Member("m" + i, "<int>1</int>"))));
        Assert.True(Encoding.UTF8.GetByteCount(wide) > XmlRpc.MaxBytes);

        var (status, _, xml) = await PostAsync(c, EconomyHelper.CurrencyPath, Encoding.UTF8.GetBytes(wide));

        Assert.Equal(HttpStatusCode.OK, status);
        AssertAnswer(xml, "wide struct");
        Assert.Contains("bad request", XDocument.Parse(xml).Root!.Value);
        Assert.Empty(sessions.Asked);
    }

    // ---- duplicates ----

    /// <summary>A duplicated member name is refused, never resolved to one of its values.</summary>
    [Fact]
    public void Duplicate_member_is_refused()
    {
        var call = XmlRpc.Read(Body(Call("m", Member("currencyBuy", "<int>1</int>") + Member("currencyBuy", "<string>two</string>") + Member("currencyBuy", "<int>3</int>"))));

        Assert.Null(call);
    }

    /// <summary>money review 2026-11-15, H-7: a second methodName or param is ignored; the first counts.</summary>
    [Fact]
    public void Duplicate_method_name_and_param_keep_the_first()
    {
        var call = XmlRpc.Read(Body(
            "<methodCall><methodName>first</methodName><methodName>second</methodName><params>"
            + "<param><value><struct>" + Member("a", "<int>1</int>") + "</struct></value></param>"
            + "<param><value><struct>" + Member("b", "<int>2</int>") + "</struct></value></param>"
            + "</params><params><param><value><struct>" + Member("c", "<int>3</int>") + "</struct></value></param></params></methodCall>"));

        Assert.NotNull(call);
        Assert.Equal("first", call.Method);
        Assert.Equal(new[] { "a" }, call.Params.Keys);
    }

    /// <summary>money review 2026-11-15, H-7: two typed values in one value, or a member with two names, read as nothing odd.</summary>
    [Fact]
    public void Duplicate_value_parts_are_refused_or_take_the_first()
    {
        Assert.Null(XmlRpc.Read(Body(Call("m", Member("a", "<int>1</int><int>2</int>")))));

        var call = XmlRpc.Read(Body(Call("m", "<member><name>a</name><name>b</name><value><int>1</int></value><value><int>2</int></value></member>")));

        Assert.NotNull(call);
        Assert.Equal(1, Assert.Single(call.Params, p => p.Key == "a").Value);
    }

    /// <summary>
    /// money review 2026-11-15, H-7: the helper takes the last of duplicated ids, so a viewer can't slip a second agent past
    /// the session check: the one it checks is the one it answers for.
    /// </summary>
    [Fact]
    public async Task Helper_refuses_duplicated_ids()
    {
        var sessions = new OkSessions();
        await using var f = Helper(sessions);
        var c = f.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        var other = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
        var body = Call("preflightBuyLandPrep",
            Member("agentId", $"<string>{other}</string>") + Who() + Member("currencyBuy", "<int>0</int>"));

        var (status, _, xml) = await PostAsync(c, EconomyHelper.LandPath, Encoding.UTF8.GetBytes(body));

        Assert.Equal(HttpStatusCode.OK, status);
        AssertAnswer(xml, "duplicate ids");
        Assert.Contains("<fault>", xml);
        Assert.Empty(sessions.Asked);
    }
}
