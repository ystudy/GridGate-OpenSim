using System.Text;
using GridGate.Launcher.Economy;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>What the economy helper's XML-RPC reader refuses, so it reads only plain calls.</summary>
public sealed class XmlRpcStrictTests
{
    private static Stream Body(string xml) => new MemoryStream(Encoding.UTF8.GetBytes(xml));

    private static string Call(string value, string method = "<methodName>m</methodName>", string head = "<?xml version=\"1.0\"?>") =>
        $"{head}<methodCall>{method}<params><param><value><struct><member><name>x</name><value>{value}</value></member></struct></value></param></params></methodCall>";

    [Fact]
    public void A_plain_call_is_read()
    {
        var call = XmlRpc.Read(Body(Call("<int>5</int>", head: "<?xml version=\"1.0\" encoding=\"utf-8\"?>")));

        Assert.Equal(5, call!.Params["x"]);
    }

    [Theory]
    [InlineData("UTF-8")]
    [InlineData("utf-8")]
    [InlineData("utf8")]
    public void Declared_utf8_is_read(string name) =>
        Assert.NotNull(XmlRpc.Read(Body(Call("<int>5</int>", head: $"<?xml version=\"1.0\" encoding=\"{name}\"?>"))));

    [Fact]
    public void A_utf8_byte_order_mark_is_read() =>
        Assert.NotNull(XmlRpc.Read(new MemoryStream([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Call("<int>5</int>"))])));

    [Fact]
    public void A_namespaced_root_is_refused() =>
        Assert.Null(XmlRpc.Read(Body("<x:methodCall xmlns:x=\"urn:x\"><x:methodName>m</x:methodName></x:methodCall>")));

    [Fact]
    public void A_default_namespace_is_refused() =>
        Assert.Null(XmlRpc.Read(Body("<methodCall xmlns=\"urn:x\"><methodName>m</methodName></methodCall>")));

    [Fact]
    public void A_namespaced_member_is_refused() =>
        Assert.Null(XmlRpc.Read(Body(Call("<int xmlns=\"urn:x\">5</int>"))));

    [Fact]
    public void Markup_inside_the_method_name_is_refused() =>
        Assert.Null(XmlRpc.Read(Body(Call("<int>5</int>", method: "<methodName><a><b>m</b></a></methodName>"))));

    [Fact]
    public void Duplicate_member_names_are_refused() =>
        Assert.Null(XmlRpc.Read(Body(
            "<methodCall><methodName>m</methodName><params><param><value><struct>" +
            "<member><name>x</name><value><int>1</int></value></member><member><name>x</name><value><int>2</int></value></member>" +
            "</struct></value></param></params></methodCall>")));

    [Theory]
    [InlineData("a<int>5</int>")]
    [InlineData("<int>5</int>b")]
    [InlineData("a<string>s</string>")]
    public void Text_around_a_typed_value_is_refused(string value) =>
        Assert.Null(XmlRpc.Read(Body(Call(value))));

    [Fact]
    public void Whitespace_around_a_typed_value_is_read() =>
        Assert.NotNull(XmlRpc.Read(Body(Call("\n  <int>5</int>\n  "))));

    [Theory]
    [InlineData("NaN")]
    [InlineData("1e999")]
    [InlineData("-1e999")]
    [InlineData("Infinity")]
    public void Non_finite_doubles_are_refused(string number) =>
        Assert.Null(XmlRpc.Read(Body(Call($"<double>{number}</double>"))));

    [Theory]
    [InlineData("ucs-4")]
    [InlineData("UTF-16")]
    [InlineData("utf-7")]
    [InlineData("ISO-8859-1")]
    public void An_encoding_other_than_utf8_is_refused_when_the_body_isnt_plain_ascii(string name) =>
        Assert.Null(XmlRpc.Read(Body(Call("<string>café</string>", head: $"<?xml version=\"1.0\" encoding=\"{name}\"?>"))));

    [Theory]
    [InlineData("ucs-4")]
    [InlineData("UTF-16")]
    [InlineData("utf-7")]
    public void An_encoding_other_than_utf8_is_refused_even_for_ascii(string name) =>
        Assert.Null(XmlRpc.Read(Body(Call("<int>5</int>", head: $"<?xml version=\"1.0\" encoding=\"{name}\"?>"))));

    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32")]
    public void Wide_encodings_are_refused_with_or_without_a_byte_order_mark(string encoding)
    {
        var e = Encoding.GetEncoding(encoding);

        Assert.Null(XmlRpc.Read(new MemoryStream([.. e.GetPreamble(), .. e.GetBytes(Call("<int>5</int>"))])));
        Assert.Null(XmlRpc.Read(new MemoryStream(e.GetBytes(Call("<int>5</int>")))));
    }

    [Fact]
    public void An_ascii_body_that_declares_latin1_is_read()
    {
        // A viewer's XML-RPC library may declare iso-8859-1 over a body that is plain ASCII, which UTF-8 reads the same.
        Assert.NotNull(XmlRpc.Read(Body(Call("<int>5</int>", head: "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?>"))));
    }
}
