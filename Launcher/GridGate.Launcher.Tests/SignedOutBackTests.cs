using System.Net;
using AngleSharp.Html.Parser;
using Jint;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// After signing out, the browser's Back button must not show a signed-in page again. Pages are never kept by the browser
/// ("no-store"), and a signed-in page that a browser brings back from its back/forward cache anyway (Safari and Firefox keep
/// whole pages there whatever the headers say) loads itself again, so the server can send a signed-out visitor to sign in.
/// </summary>
public sealed class SignedOutBackTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string BackCheck = "/js/back-check.js";

    private static async Task<(HttpResponseMessage Response, List<string> Scripts)> GetAsync(HttpClient client, string url)
    {
        var r = await client.GetAsync(url, Ct);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} answered {(int)r.StatusCode}");
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
        return (r, page.QuerySelectorAll("script[src]").Select(s => s.GetAttribute("src")!.Split('?')[0]).ToList());
    }

    [Theory]
    [InlineData("/guest")]
    [InlineData("/guest/account")]
    [InlineData("/map")]
    [InlineData("/support")]
    public async Task A_residents_pages_are_not_kept_and_load_again_when_brought_back(string url)
    {
        await using var f = WorldMapApiTests.Factory();
        var (r, scripts) = await GetAsync(await WorldMapApiTests.ResidentAsync(f), url);
        using (r)
        {
            Assert.True(r.Headers.CacheControl is { NoStore: true }, r.Headers.CacheControl?.ToString());
            Assert.Contains(BackCheck, scripts);
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/regions")]
    [InlineData("/people")]
    [InlineData("/home")]
    public async Task Staff_pages_are_not_kept_and_load_again_when_brought_back(string url)
    {
        await using var f = WorldMapApiTests.Factory();
        var (r, scripts) = await GetAsync(await WorldMapApiTests.StaffAsync(f, "owner"), url);
        using (r)
        {
            Assert.True(r.Headers.CacheControl is { NoStore: true }, r.Headers.CacheControl?.ToString());
            Assert.Contains(BackCheck, scripts);
        }
    }

    [Theory]
    [InlineData("/home")]
    [InlineData("/featured")]
    [InlineData("/guest/sign-in")]
    public async Task A_visitors_pages_keep_the_fast_back_button(string url)
    {
        await using var f = WorldMapApiTests.Factory();
        var (r, scripts) = await GetAsync(WorldMapApiTests.Client(f), url);
        using (r)
            Assert.DoesNotContain(BackCheck, scripts);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void The_script_loads_the_page_again_only_when_brought_back(bool persisted, int reloads)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridGate.sln"))) dir = dir.Parent;
        var script = File.ReadAllText(Path.Combine(dir!.FullName, "Launcher", "GridGate.Launcher", "wwwroot", "js", "back-check.js"));

        var js = new Jint.Engine().Execute("""
            var handlers = {}, reloads = 0;
            var window = { addEventListener: function (type, f) { handlers[type] = f; } };
            var location = { reload: function () { reloads++; } };
            """).Execute(script);
        js.Execute($"handlers.pageshow({{ persisted: {(persisted ? "true" : "false")} }});");

        Assert.Equal(reloads, (int)js.Evaluate("reloads").AsNumber());
    }
}
