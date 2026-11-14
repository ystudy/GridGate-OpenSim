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

    private const int MaxDepth = 4;

    private sealed class Bad : Exception;

    /// <summary>The call in <paramref name="body"/>, or null for anything that isn't a plain XML-RPC call.</summary>
    public static Call? Read(Stream body, int maxBytes = 16384)
    {
        try
        {
            var bytes = ReadCapped(body, maxBytes);
            if (bytes is null || bytes.Length == 0) return null;
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = maxBytes,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
            var root = XDocument.Load(reader).Root;
            if (root is null || root.Name.LocalName != "methodCall") return null;
            var method = root.Element("methodName")?.Value.Trim();
            if (string.IsNullOrEmpty(method)) return null;
            var value = root.Element("params")?.Element("param")?.Element("value");
            if (value is null) return new Call(method, new Dictionary<string, object>());
            return Value(value, 1) is IReadOnlyDictionary<string, object> members ? new Call(method, members) : null;
        }
        catch (Exception e) when (e is XmlException or Bad or IOException or InvalidOperationException)
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
        if (typed.Count > 1) throw new Bad();
        var e = typed[0];
        return e.Name.LocalName switch
        {
            "string" => e.Value,
            "int" or "i4" => int.TryParse(e.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i) ? i : throw new Bad(),
            "boolean" => e.Value.Trim() switch { "1" => true, "0" => false, _ => throw new Bad() },
            "double" => double.TryParse(e.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new Bad(),
            "struct" => Struct(e, depth),
            _ => throw new Bad(),
        };
    }

    private static Dictionary<string, object> Struct(XElement s, int depth)
    {
        var members = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var m in s.Elements())
        {
            if (m.Name.LocalName != "member") throw new Bad();
            var name = m.Element("name")?.Value ?? throw new Bad();
            var value = m.Element("value") ?? throw new Bad();
            members[name] = Value(value, depth + 1);
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
