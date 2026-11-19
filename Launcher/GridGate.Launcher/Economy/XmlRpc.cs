using System.Globalization;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace GridGate.Launcher.Economy;

/// <summary>
/// The little XML-RPC the economy helper needs (money design §8.6). Reading is strict and refuses anything odd; writing
/// follows what Firestorm's parser reads: booleans as 1 or 0, integers as plain digits, and always a struct answer.
/// </summary>
public static class XmlRpc
{
    /// <summary>A call: its method and the first param's struct members (string, int, bool, double or a nested struct).</summary>
    public sealed record Call(string Method, IReadOnlyDictionary<string, object> Params);

    /// <summary>The largest call read: 16 KB.</summary>
    public const int MaxBytes = 16384;

    private const int MaxDepth = 4;

    private sealed class Bad : Exception;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Whether a declared encoding is one this reader reads: none, or UTF-8. A viewer's library may declare an ASCII-compatible
    /// one over a body that is plain ASCII, which UTF-8 reads the same; every other declaration is refused.
    /// </summary>
    private static bool PlainEncoding(string? declared, byte[] bytes) =>
        declared is null || declared.Equals("utf-8", StringComparison.OrdinalIgnoreCase) || declared.Equals("utf8", StringComparison.OrdinalIgnoreCase)
        || (declared.ToLowerInvariant() is "us-ascii" or "ascii" or "iso-8859-1" or "latin1" && bytes.All(b => b < 0x80));

    /// <summary>The call in <paramref name="body"/>, or null for anything that isn't a plain XML-RPC call.</summary>
    public static Call? Read(Stream body, int maxBytes = MaxBytes)
    {
        try
        {
            var bytes = ReadCapped(body, maxBytes);
            if (bytes is null || bytes.Length == 0) return null;
            // UTF-8 only: a byte order mark is dropped, and bytes that aren't valid UTF-8 (UTF-16 and UTF-32 included) throw.
            var start = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
            var text = StrictUtf8.GetString(bytes, start, bytes.Length - start);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = maxBytes,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var doc = XDocument.Load(reader);
            if (!PlainEncoding(doc.Declaration?.Encoding, bytes)) return null;
            var root = doc.Root;
            if (root is null || root.Name.LocalName != "methodCall" || root.DescendantsAndSelf().Any(e => e.Name.Namespace != XNamespace.None)) return null;
            // Exactly one name, and at most one params with one param: a second would read as the first, or be ignored.
            var names = root.Elements("methodName").ToList();
            var allParams = root.Elements("params").ToList();
            if (names.Count != 1 || allParams.Count > 1 || allParams.Any(p => p.Elements("param").Count() > 1)) return null;
            var name = names[0];
            // The name is text only: nested markup around it would read as the same method.
            if (name.HasElements) return null;
            var method = name.Value.Trim();
            if (string.IsNullOrEmpty(method)) return null;
            var values = allParams.SelectMany(p => p.Elements("param")).SelectMany(p => p.Elements("value")).ToList();
            if (values.Count > 1) return null;
            var value = values.FirstOrDefault();
            if (value is null) return new Call(method, new Dictionary<string, object>());
            return Value(value, 1) is IReadOnlyDictionary<string, object> members ? new Call(method, members) : null;
        }
        catch (Exception e) when (e is XmlException or Bad or IOException or InvalidOperationException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static byte[]? ReadCapped(Stream body, int maxBytes)
    {
        using var copy = new MemoryStream();
        var buffer = new byte[4096];
        int n;
        while ((n = body.Read(buffer, 0, buffer.Length)) > 0)
        {
            copy.Write(buffer, 0, n);
            if (copy.Length > maxBytes) return null;
        }
        return copy.ToArray();
    }

    private static object Value(XElement value, int depth)
    {
        if (depth > MaxDepth) throw new Bad();
        var typed = value.Elements().ToList();
        if (typed.Count == 0) return value.Value;
        if (typed.Count > 1 || HasText(value)) throw new Bad();
        var e = typed[0];
        // A simple type holds text only: nested markup would be flattened to its text.
        if (e.Name.LocalName != "struct" && e.HasElements) throw new Bad();
        return e.Name.LocalName switch
        {
            "string" => e.Value,
            "int" or "i4" => int.TryParse(e.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i) ? i : throw new Bad(),
            "boolean" => e.Value.Trim() switch { "1" => true, "0" => false, _ => throw new Bad() },
            "double" => double.TryParse(e.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : throw new Bad(),
            "struct" => Struct(e, depth),
            _ => throw new Bad(),
        };
    }

    /// <summary>Text, other than white space, directly inside <paramref name="e"/>.</summary>
    private static bool HasText(XElement e) => e.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value));

    private static Dictionary<string, object> Struct(XElement s, int depth)
    {
        var members = new Dictionary<string, object>(StringComparer.Ordinal);
        if (HasText(s)) throw new Bad();
        foreach (var m in s.Elements())
        {
            if (m.Name.LocalName != "member" || HasText(m)) throw new Bad();
            var names = m.Elements("name").ToList();
            var values = m.Elements("value").ToList();
            if (names.Count != 1 || values.Count != 1 || names[0].HasElements) throw new Bad();
            var name = names[0].Value;
            var value = values[0];
            if (!members.TryAdd(name, Value(value, depth + 1))) throw new Bad();
        }
        return members;
    }

    /// <summary>
    /// <c>methodResponse/params/param/value/struct</c> with <paramref name="members"/>. Values may be bool, int, long (in
    /// the int range), string, or a nested list of members.
    /// </summary>
    public static string Response(IEnumerable<(string Name, object Value)> members)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\"?><methodResponse><params><param><value>");
        WriteStruct(sb, members);
        sb.Append("</value></param></params></methodResponse>");
        return sb.ToString();
    }

    /// <summary>A fault: only for calls the helper can't read or doesn't know.</summary>
    public static string Fault(int code, string message)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\"?><methodResponse><fault><value>");
        WriteStruct(sb, [("faultCode", code), ("faultString", message)]);
        sb.Append("</value></fault></methodResponse>");
        return sb.ToString();
    }

    private static void WriteStruct(StringBuilder sb, IEnumerable<(string Name, object Value)> members)
    {
        sb.Append("<struct>");
        foreach (var (name, value) in members)
        {
            sb.Append("<member><name>").Append(SecurityElement.Escape(name)).Append("</name><value>");
            switch (value)
            {
                case bool b:
                    sb.Append("<boolean>").Append(b ? '1' : '0').Append("</boolean>");
                    break;
                case int i:
                    sb.Append("<int>").Append(i.ToString(CultureInfo.InvariantCulture)).Append("</int>");
                    break;
                case long l:
                    sb.Append("<int>").Append(checked((int)l).ToString(CultureInfo.InvariantCulture)).Append("</int>");
                    break;
                case string s:
                    sb.Append("<string>").Append(SecurityElement.Escape(s)).Append("</string>");
                    break;
                case IEnumerable<(string, object)> nested:
                    WriteStruct(sb, nested);
                    break;
                default:
                    throw new ArgumentException($"XML-RPC can't write a {value?.GetType().Name ?? "null"}.", nameof(members));
            }
            sb.Append("</value></member>");
        }
        sb.Append("</struct>");
    }
}
