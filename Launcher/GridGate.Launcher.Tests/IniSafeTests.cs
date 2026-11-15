using System.Security;
using GridGate.Launcher.Core.Ini;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Plans;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// OpenSim reads its INIs with Nini, which expands <c>${Section|Key}</c> in every value and reads <c>; # " [ ]</c> as INI
/// syntax. A value another computer sent (grid information) must not carry any of them, or it could copy a password or the
/// money key into an address every viewer is given (money review 2026-11-15, H-1).
/// </summary>
public sealed class IniSafeTests
{
    public static TheoryData<string> ChangedByTheIniReader => new()
    {
        "http://attacker.example/${Network|ConsolePass}/",
        "http://attacker.example/${GridGateMoney|RegionKey}",
        "http://attacker.example/${DatabaseService|ConnectionString}",
        "http://a.example/x\"y",
        "http://a.example/x;y",
        "http://a.example/x#y",
        "http://a.example/[x]",
        "http://a.example/x\\y",
        "http://a.example/a b",
    };

    [Theory]
    [MemberData(nameof(ChangedByTheIniReader))]
    public void Address_refuses_what_the_ini_reader_would_change(string value) => Assert.Null(IniSafe.Address(value));

    [Theory]
    [InlineData("http://192.168.1.5:8001/economy/")]
    [InlineData("https://hg.osgrid.org:443/")]
    [InlineData("http://login.osgrid.org/")]
    [InlineData("http://hg.example.org:8002")]
    public void Address_keeps_a_plain_address(string value) => Assert.Equal(value, IniSafe.Address(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ftp://192.168.1.5/economy/")]
    [InlineData("/economy/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://192.168.1.5:8001/economy/\nRegionKey = x")]
    public void Address_refuses_what_is_not_an_http_address(string? value) => Assert.Null(IniSafe.Address(value));

    [Theory]
    [InlineData("${Network|ConsolePass}")]
    [InlineData("Avalon ${Const|BaseURL}")]
    [InlineData("A;B")]
    [InlineData("A#B")]
    [InlineData("A\"B")]
    [InlineData("[A]")]
    [InlineData("A=B")]
    [InlineData("A\\B")]
    [InlineData("A\nB")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Name_refuses_what_the_ini_reader_would_change(string? value) => Assert.Null(IniSafe.Name(value));

    [Theory]
    [InlineData("OSgrid")]
    [InlineData("Avalon Grid")]
    [InlineData("Kitely's world (beta)!")]
    public void Name_keeps_a_plain_name(string value) => Assert.Equal(value, IniSafe.Name(value));

    [Fact]
    public void Name_refuses_more_than_64_characters()
    {
        Assert.Equal(new string('a', 64), IniSafe.Name(new string('a', 64)));
        Assert.Null(IniSafe.Name(new string('a', 65)));
    }

    // ---- A Region Server reading its Grid Asset Server ----

    private static string Joining(string extra) =>
        "<gridinfo><gridgate_version>7.1.0</gridgate_version><gridgate_private_port>8003</gridgate_private_port>" +
        $"<gridgate_hypergrid>false</gridgate_hypergrid><gridgate_plan>grid-asset</gridgate_plan>{extra}</gridinfo>";

    private static string El(string name, string value) => $"<{name}>{SecurityElement.Escape(value)}</{name}>";

    [Theory]
    [MemberData(nameof(ChangedByTheIniReader))]
    public void Join_drops_an_economy_address_the_ini_reader_would_change(string economy) =>
        Assert.Null(GridJoin.Parse("192.168.1.5", 8002, Joining(El("economy", economy))).Economy);

    [Fact]
    public void Join_keeps_a_plain_economy_address() =>
        Assert.Equal("http://192.168.1.5:8001/economy/",
            GridJoin.Parse("192.168.1.5", 8002, Joining(El("economy", "http://192.168.1.5:8001/economy/"))).Economy);

    [Theory]
    [InlineData("${Network|ConsolePass}")]
    [InlineData("Avalon; [Network]")]
    public void Join_names_the_grid_by_its_host_when_its_name_would_change(string name) =>
        Assert.Equal("192.168.1.5", GridJoin.Parse("192.168.1.5", 8002, Joining(El("gridname", name))).GridName);

    [Theory]
    [MemberData(nameof(ChangedByTheIniReader))]
    public void Join_uses_the_typed_address_when_the_login_would_change(string login) =>
        Assert.Equal("http://192.168.1.5:8002/", GridJoin.Parse("192.168.1.5", 8002, Joining(El("login", login))).LoginUri);

    [Theory]
    [InlineData("${GridGateMoney|RegionKey}")]
    [InlineData("ABCDEF;x")]
    [InlineData("not hex")]
    public void Join_drops_a_fingerprint_that_is_not_hex(string fingerprint) =>
        Assert.Null(GridJoin.Parse("192.168.1.5", 8002, Joining(El("gridgate_money_fingerprint", fingerprint))).MoneyFingerprint);

    // ---- A Connected Server reading another grid (often someone else's) ----

    private static readonly Uri Other = new("http://login.example.org:8002/");

    [Fact]
    public void Other_grid_named_by_its_host_when_its_name_would_change() =>
        Assert.Equal("login.example.org",
            OtherGridInfoParser.Parse(Other, $"<gridinfo>{El("gridname", "${Network|ConsolePass}")}</gridinfo>").GridName);

    [Theory]
    [MemberData(nameof(ChangedByTheIniReader))]
    public void Other_grid_login_falls_back_to_the_typed_address(string login) =>
        Assert.Equal(Other.ToString(), OtherGridInfoParser.Parse(Other, $"<gridinfo>{El("login", login)}</gridinfo>").LoginUri);

    [Theory]
    [MemberData(nameof(ChangedByTheIniReader))]
    public void Other_grid_gatekeeper_that_would_change_is_dropped(string gatekeeper) =>
        Assert.Null(OtherGridInfoParser.Parse(Other, $"<gridinfo>{El("gatekeeper", gatekeeper)}</gridinfo>").GatekeeperUri);

    // ---- The currency symbol, written into Robust's [LoginService] Currency (money review H-3) ----

    [Theory]
    [InlineData("${x}")]
    [InlineData("${}")]
    [InlineData("{x}")]
    [InlineData("C#")]
    public void Symbol_refuses_what_the_ini_reader_would_change(string symbol) => Assert.False(MoneySymbolRule.Valid(symbol));

    [Theory]
    [InlineData("C$")]
    [InlineData("L$")]
    [InlineData("OS$")]
    [InlineData("€")]
    public void Symbol_keeps_a_dollar_sign_on_its_own(string symbol) => Assert.True(MoneySymbolRule.Valid(symbol));
}
