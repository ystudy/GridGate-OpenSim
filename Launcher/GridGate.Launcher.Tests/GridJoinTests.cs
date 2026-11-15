using GridGate.Launcher.Core.Plans;
using GridGate.Launcher.Core.Settings;
using Xunit;

namespace GridGate.Launcher.Tests;

public sealed class GridJoinTests
{
    private static string Xml(string plan = "grid-asset") =>
        "<gridinfo><gridname>Avalon</gridname><login>http://192.168.1.5:8002/</login><gridgate_version>7.1.0</gridgate_version>" +
        "<gridgate_private_port>8003</gridgate_private_port><gridgate_hypergrid>false</gridgate_hypergrid>" +
        $"<gridgate_region_ports>8004-8006</gridgate_region_ports><gridgate_plan>{plan}</gridgate_plan></gridinfo>";

    [Fact]
    public void Parse_reads_a_grid_asset_server() =>
        Assert.Equal(new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", "7.1.0", false, 8004, 8006),
            GridJoin.Parse("192.168.1.5", 8002, Xml()));

    [Fact]
    public void Parse_reads_the_grids_money()
    {
        var xml = Xml().Replace("</gridinfo>", "<economy>http://192.168.1.5:8001/economy/</economy><gridgate_money>play</gridgate_money>"
            + "<gridgate_money_port>8000</gridgate_money_port><gridgate_money_fingerprint>ABCDEF</gridgate_money_fingerprint></gridinfo>");

        var join = GridJoin.Parse("192.168.1.5", 8002, xml);

        Assert.Equal(Core.Money.MoneyMode.Play, join.Money);
        Assert.Equal(8000, join.MoneyPort);
        Assert.Equal("ABCDEF", join.MoneyFingerprint);
        Assert.Equal("http://192.168.1.5:8001/economy/", join.Economy);
    }

    [Fact]
    public void Parse_older_grid_asset_has_no_money()
    {
        var join = GridJoin.Parse("192.168.1.5", 8002, Xml());

        Assert.Null(join.Money);
        Assert.Null(join.MoneyPort);
        Assert.Null(join.MoneyFingerprint);
        Assert.Null(join.Economy);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://192.168.1.5/economy/")]
    [InlineData("/economy/")]
    [InlineData("http://192.168.1.5:8001/economy/\nRegionKey = x")]
    public void Parse_drops_an_economy_address_that_is_not_http(string economy)
    {
        var xml = Xml().Replace("</gridinfo>", $"<economy>{System.Security.SecurityElement.Escape(economy)}</economy></gridinfo>");

        Assert.Null(GridJoin.Parse("192.168.1.5", 8002, xml).Economy);
    }

    [Fact]
    public void Parse_unreadable_money_reads_as_off()
    {
        var xml = Xml().Replace("</gridinfo>", "<gridgate_money>lots</gridgate_money><gridgate_money_port>x</gridgate_money_port></gridinfo>");

        var join = GridJoin.Parse("192.168.1.5", 8002, xml);

        Assert.Equal(Core.Money.MoneyMode.Off, join.Money);
        Assert.Null(join.MoneyPort);
    }

    [Fact]
    public void Joining_another_grid_drops_the_money_pairing()
    {
        var paired = TestSettings.Create() with
        {
            Join = GridJoin.Parse("192.168.1.5", 8002, Xml()),
            Money = new Core.Money.MoneySettings(Pairing: new Core.Money.ServerPairing("192.168.1.5", "F", "K", DateTimeOffset.UtcNow)),
        };

        Assert.NotNull(GridGate.Launcher.Setup.JoinForm.Apply(paired, GridJoin.Parse("192.168.1.5", 8002, Xml())).Money.Pairing);
        Assert.Null(GridGate.Launcher.Setup.JoinForm.Apply(paired, GridJoin.Parse("192.168.1.9", 8002, Xml())).Money.Pairing);
    }

    [Fact]
    public void Parse_reads_empty_region_ports()
    {
        var xml = Xml().Replace("<gridgate_region_ports>8004-8006</gridgate_region_ports>", "<gridgate_region_ports></gridgate_region_ports>");
        var join = GridJoin.Parse("192.168.1.5", 8002, xml);
        Assert.Null(join.RegionPortsFrom);
        Assert.Null(join.RegionPortsTo);
    }

    [Fact]
    public void Parse_refuses_another_plan()
    {
        var e = Assert.Throws<GridJoinException>(() => GridJoin.Parse("192.168.1.5", 8002, Xml("simple-public")));
        Assert.Contains("Simple Public Server", e.Message);
        Assert.Contains("Grid Asset Server", e.Message);
    }

    [Fact]
    public void Parse_refuses_a_grid_without_gridgate_keys()
    {
        var e = Assert.Throws<GridJoinException>(() => GridJoin.Parse("grid.osgrid.org", 80, "<gridinfo><gridname>OSgrid</gridname></gridinfo>"));
        Assert.Contains("isn't a GridGate Grid Asset Server", e.Message);
    }

    [Fact]
    public void Parse_refuses_what_is_not_grid_info()
    {
        var e = Assert.Throws<GridJoinException>(() => GridJoin.Parse("192.168.1.5", 8002, "<html><body>Hello</body></html>"));
        Assert.Contains("didn't answer like a grid", e.Message);
    }

    [Theory]
    [InlineData("192.168.1.5", "192.168.1.5", 8002)]
    [InlineData(" 192.168.1.5:9002 ", "192.168.1.5", 9002)]
    [InlineData("http://grid.example.org:8002/", "grid.example.org", 8002)]
    [InlineData("grid.example.org", "grid.example.org", 8002)]
    public void JoinAddress_parses(string typed, string host, int port)
    {
        Assert.Equal((host, port), JoinAddress.Parse(typed, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.1.5:")]
    [InlineData("192.168.1.5:70000")]
    [InlineData("a b")]
    [InlineData("ftp://x")]
    public void JoinAddress_refuses(string typed)
    {
        Assert.Null(JoinAddress.Parse(typed, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public async Task Client_marks_nothing_answering_as_no_answer()
    {
        using var http = new HttpClient(new Answer(_ => throw new HttpRequestException("Connection refused")));

        var e = await Assert.ThrowsAsync<GridJoinException>(() =>
            new GridJoinClient(http).ReadAsync("192.168.1.5", 8002, TestContext.Current.CancellationToken));

        Assert.True(e.NoAnswer);
        Assert.Contains("Nothing answered", e.Message);
    }

    [Fact]
    public async Task Client_does_not_mark_another_plan_as_no_answer()
    {
        using var http = new HttpClient(new Answer(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Xml("local")) }));

        var e = await Assert.ThrowsAsync<GridJoinException>(() =>
            new GridJoinClient(http).ReadAsync("192.168.1.5", 8002, TestContext.Current.CancellationToken));

        Assert.False(e.NoAnswer);
    }

    private sealed class Answer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(answer(request));
    }
}
