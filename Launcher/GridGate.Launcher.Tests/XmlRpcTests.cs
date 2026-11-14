using System.Globalization;
using System.Text;
using System.Xml.Linq;
using GridGate.Launcher.Economy;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The economy helper's XML-RPC, read and written the way Firestorm needs (money plan 2, task 7; money design §8.6).</summary>
public sealed class XmlRpcTests
{
    private static Stream Body(string xml) => new MemoryStream(Encoding.UTF8.GetBytes(xml));

    // What Firestorm's llcurrencyuimanager.cpp sends for a quote.
    private const string Quote = """
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
        """;

    [Fact]
    public void Reads_firestorm_quote_call()
    {
        var call = XmlRpc.Read(Body(Quote));

        Assert.NotNull(call);
        Assert.Equal("getCurrencyQuote", call.Method);
        Assert.Equal("a0000000-0000-0000-0000-00000000000a", call.Params["agentId"]);
        Assert.Equal("de", call.Params["language"]);
        Assert.Equal(250, call.Params["currencyBuy"]);
        Assert.Equal(1, call.Params["viewerMinorVersion"]);
    }

    [Fact]
    public void Untyped_value_is_a_string()
    {
        var call = XmlRpc.Read(Body(
            "<methodCall><methodName>m</methodName><params><param><value><struct><member><name>x</name><value>plain</value></member></struct></value></param></params></methodCall>"));

        Assert.Equal("plain", call!.Params["x"]);
    }

    [Fact]
    public void Booleans_and_doubles_are_read()
    {
        var call = XmlRpc.Read(Body(
            "<methodCall><methodName>m</methodName><params><param><value><struct>" +
            "<member><name>b</name><value><boolean>1</boolean></value></member>" +
            "<member><name>d</name><value><double>2.5</double></value></member>" +
            "</struct></value></param></params></methodCall>"));

        Assert.Equal(true, call!.Params["b"]);
        Assert.Equal(2.5, call.Params["d"]);
    }

    [Fact]
    public void A_call_without_params_has_none()
    {
        var call = XmlRpc.Read(Body("<methodCall><methodName>m</methodName></methodCall>"));

        Assert.Equal("m", call!.Method);
        Assert.Empty(call.Params);
    }

    [Fact]
    public void Dtd_is_refused() =>
        Assert.Null(XmlRpc.Read(Body("<?xml version=\"1.0\"?><!DOCTYPE m [<!ENTITY x \"y\">]><methodCall><methodName>&x;</methodName></methodCall>")));

    [Fact]
    public void Oversized_body_is_refused()
    {
        var padding = new string(' ', 20 * 1024);

        Assert.Null(XmlRpc.Read(Body($"<methodCall><methodName>m</methodName>{padding}</methodCall>")));
    }

    [Theory]
    [InlineData("<a>")]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<methodCall><params/></methodCall>")]
    [InlineData("<methodCall><methodName>m</methodName><params><param><value><string>not a struct</string></value></param></params></methodCall>")]
    [InlineData("<methodCall><methodName>m</methodName><params><param><value><struct><member><name>n</name><value><int>12x</int></value></member></struct></value></param></params></methodCall>")]
    public void Garbage_is_null(string xml) => Assert.Null(XmlRpc.Read(Body(xml)));

    [Fact]
    public void Deep_nesting_is_refused()
    {
        var inner = "<value><string>x</string></value>";
        for (var i = 0; i < 6; i++) inner = $"<value><struct><member><name>n</name>{inner}</member></struct></value>";
        Assert.Null(XmlRpc.Read(Body($"<methodCall><methodName>m</methodName><params><param>{inner}</param></params></methodCall>")));
    }

    [Fact]
    public void Booleans_are_1_or_0()
    {
        var xml = XmlRpc.Response([("success", false), ("other", true)]);

        Assert.Contains("<boolean>0</boolean>", xml);
        Assert.Contains("<boolean>1</boolean>", xml);
        Assert.DoesNotContain("false", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("true", xml, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    public void Ints_are_plain_digits(string culture)
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var xml = XmlRpc.Response([("n", 1234), ("m", -5), ("l", 7L)]);

            Assert.Contains("<int>1234</int>", xml);
            Assert.Contains("<int>-5</int>", xml);
            Assert.Contains("<int>7</int>", xml);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Long_outside_int_throws() =>
        Assert.Throws<OverflowException>(() => XmlRpc.Response([("n", (long)int.MaxValue + 1)]));

    [Fact]
    public void Strings_are_escaped()
    {
        var xml = XmlRpc.Response([("errorMessage", "<&> \"quoted\"")]);

        Assert.Equal("<&> \"quoted\"", XDocument.Parse(xml).Descendants("string").Single().Value);
    }

    [Fact]
    public void Response_shape_is_exact()
    {
        var xml = XmlRpc.Response([("success", true), ("currency", new (string, object)[] { ("estimatedCost", 0) })]);
        var root = XDocument.Parse(xml).Root!;

        Assert.StartsWith("<?xml version=\"1.0\"?>", xml);
        Assert.Equal("methodResponse", root.Name.LocalName);
        var value = Assert.Single(Assert.Single(Assert.Single(root.Elements()).Elements("param")).Elements("value"));
        Assert.Equal("params", root.Elements().Single().Name.LocalName);
        var top = Assert.Single(value.Elements());
        Assert.Equal("struct", top.Name.LocalName);
        Assert.Equal(["success", "currency"], top.Elements("member").Select(m => m.Element("name")!.Value));
        Assert.Equal("struct", top.Elements("member").Last().Element("value")!.Elements().Single().Name.LocalName);
    }

    [Fact]
    public void Fault_has_code_and_message()
    {
        var root = XDocument.Parse(XmlRpc.Fault(2, "unknown method")).Root!;

        Assert.Equal("fault", root.Elements().Single().Name.LocalName);
        Assert.Contains("<int>2</int>", root.ToString(SaveOptions.DisableFormatting));
        Assert.Contains("unknown method", root.Value);
    }
}
