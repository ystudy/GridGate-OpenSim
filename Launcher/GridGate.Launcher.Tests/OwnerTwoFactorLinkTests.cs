using System.Net;
using System.Text.Json;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Pages.Profile;
using GridGate.Launcher.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// On a Region Server or Connected Server, the owner's two-factor page (<c>/profile/two-factor</c>) makes a secret only with
/// proof from this computer: a one-time link from <c>admin-link --two-factor</c>.
/// </summary>
public sealed class OwnerTwoFactorLinkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HtmlParser Parser = new();
    private const string Page = "/profile/two-factor";

    private static LauncherFactory Make(OwnerPlan plan = OwnerPlan.RegionServer)
    {
        var f = new LauncherFactory(s => s with
        {
            SetupToken = null,
            GridName = "Avalon",
            Plan = plan,
            Join = plan == OwnerPlan.RegionServer
                ? new JoinedGrid("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", LauncherVersion.Current, false, 8004, 8006)
                : null,
            Connected = plan == OwnerPlan.Connected
                ? new ConnectedGrid("osgrid", "OSGrid", "http://login.osgrid.org/", "http://login.osgrid.org/", "http://grid.osgrid.org/", null)
                : null,
            Setup = new SetupState(WorldNamed: true, OwnerCreated: true, RegionCreated: true, Finished: true,
                Owner: new OwnerInfo("Test", "Owner", TestAuth.OwnerId), NetworkSet: true),
        })
        {
            Before = TestAuth.Apply,
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f, string role = "owner")
    {
        var c = f.CreateClient(new() { AllowAutoRedirect = false });
        (await c.GetAsync("/__test/signin?role=" + role, Ct)).Dispose();
        return c;
    }

    private static async Task<IHtmlDocument> GetPageAsync(HttpClient client, string url = Page)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static string MainText(IHtmlDocument page) => page.QuerySelector("main")!.TextContent;

    private static TwoFactorState State(LauncherFactory f) => f.Services.GetRequiredService<TwoFactorStore>().StateOf(TestAuth.OwnerId);

    private static string LinkFile(LauncherFactory f) => Path.Combine(f.Paths.Security, "two-factor-link.json");

    private static string NewLink(LauncherFactory f) => f.Services.GetRequiredService<OwnerEnrolLinks>().Create(TestAuth.OwnerId);

    private static int AuditCount(LauncherFactory f, string action)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return 0;
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(l => JsonDocument.Parse(l).RootElement.GetProperty("action").GetString() == action);
    }

    private static byte[] FromBase32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var ch in text.Trim().TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(char.ToUpperInvariant(ch));
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            bytes.Add((byte)(buffer >> bits));
        }
        return [.. bytes];
    }

    private static Task<HttpResponseMessage> TurnOnAsync(HttpClient client, LauncherFactory f, IHtmlDocument page, byte[] secret) =>
        client.PostAsync(Page + "?handler=TurnOn", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Code"] = Totp.Code(secret, Totp.StepAt(f.Time.GetUtcNow())),
            ["Shown"] = page.QuerySelector("form#turn-on input[name=Shown]")!.GetAttribute("value")!,
            ["__RequestVerificationToken"] = page.QuerySelector("form#turn-on input[name=__RequestVerificationToken]")!.GetAttribute("value")!,
        }), Ct);

    /// <summary>Opens a new link and follows its redirect; returns the page with the secret.</summary>
    private static async Task<IHtmlDocument> ProvenAsync(HttpClient client, LauncherFactory f)
    {
        using (var r = await client.GetAsync(Page + "?link=" + NewLink(f), Ct))
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return await GetPageAsync(client);
    }

    [Theory]
    [InlineData(OwnerPlan.RegionServer)]
    [InlineData(OwnerPlan.Connected)]
    public async Task Owner_without_proof_sees_the_command_not_a_secret(OwnerPlan plan)
    {
        await using var f = Make(plan);
        var client = await SignedInAsync(f);

        var page = await GetPageAsync(client);

        Assert.Null(page.QuerySelector("#two-factor-secret"));
        Assert.Null(page.QuerySelector("form#turn-on"));
        Assert.Null(page.QuerySelector(".two-factor-qr"));
        Assert.Contains(TwoFactorModel.LinkCommand, MainText(page), StringComparison.Ordinal);
        Assert.Equal(TwoFactorState.Off, State(f));
    }

    [Fact]
    public async Task Link_turns_it_on()
    {
        await using var f = Make();
        var client = await SignedInAsync(f);
        var token = NewLink(f);

        using (var r = await client.GetAsync(Page + "?link=" + token, Ct))
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal(Page, r.Headers.Location!.OriginalString);
            Assert.Contains(r.Headers.GetValues("Set-Cookie"), c => c.StartsWith(OwnerEnrolProof.CookieName + "=", StringComparison.Ordinal));
        }
        Assert.False(File.Exists(LinkFile(f)));
        Assert.Equal(1, AuditCount(f, "two-factor-link-used"));

        var page = await GetPageAsync(client);
        var secret = FromBase32(page.QuerySelector("#two-factor-secret")!.TextContent);
        using var on = await TurnOnAsync(client, f, page, secret);

        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.Equal(TwoFactorState.On, State(f));
        Assert.Equal(1, AuditCount(f, "two-factor-on"));
        Assert.Contains(on.Headers.GetValues("Set-Cookie"), c => c.StartsWith(OwnerEnrolProof.CookieName + "=;", StringComparison.Ordinal));
        Assert.DoesNotContain(f.Logs, l => l.Message.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Turn_on_post_after_the_proof_ran_out_is_refused()
    {
        await using var f = Make();
        var client = await SignedInAsync(f);
        var page = await ProvenAsync(client, f);
        var secret = FromBase32(page.QuerySelector("#two-factor-secret")!.TextContent);

        f.Time.Advance(OwnerEnrolProof.FromLink + TimeSpan.FromSeconds(1));
        using var on = await TurnOnAsync(client, f, page, secret);

        Assert.Equal(TwoFactorState.Off, State(f));
        var after = Parser.ParseDocument(await on.Content.ReadAsStringAsync(Ct));
        Assert.Contains(TwoFactorModel.LinkCommand, MainText(after), StringComparison.Ordinal);
        Assert.Null(after.QuerySelector("#two-factor-secret"));
    }

    [Fact]
    public async Task Turn_on_post_from_another_browser_without_proof_is_refused()
    {
        await using var f = Make();
        var proven = await SignedInAsync(f);
        var page = await ProvenAsync(proven, f);
        var secret = FromBase32(page.QuerySelector("#two-factor-secret")!.TextContent);

        // A second browser signed in as the owner, posting the proven form's fields without the proof cookie.
        var other = await SignedInAsync(f);
        var otherPage = await GetPageAsync(other);
        using var on = await other.PostAsync(Page + "?handler=TurnOn", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Code"] = Totp.Code(secret, Totp.StepAt(f.Time.GetUtcNow())),
            ["Shown"] = page.QuerySelector("form#turn-on input[name=Shown]")!.GetAttribute("value")!,
            ["__RequestVerificationToken"] = otherPage.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!,
        }), Ct);

        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.NotNull(Parser.ParseDocument(await on.Content.ReadAsStringAsync(Ct)).QuerySelector("#two-factor-link"));
        Assert.Equal(TwoFactorState.Off, State(f));
    }

    [Fact]
    public async Task Link_opened_by_an_admin_is_refused_and_kept()
    {
        await using var f = Make();
        var client = await SignedInAsync(f, "admin");

        var page = await GetPageAsync(client, Page + "?link=" + NewLink(f));

        Assert.Contains(TwoFactorModel.LinkRunOut, MainText(page), StringComparison.Ordinal);
        Assert.True(File.Exists(LinkFile(f)));
        Assert.Equal(1, AuditCount(f, "two-factor-link-refused"));
    }

    [Fact]
    public async Task Admin_on_a_region_server_needs_no_link()
    {
        await using var f = Make();
        var client = await SignedInAsync(f, "admin");

        var page = await GetPageAsync(client);

        Assert.NotNull(page.QuerySelector("#two-factor-secret"));
    }

    [Fact]
    public async Task Link_opened_signed_out_goes_to_sign_in_and_back()
    {
        await using var f = Make();
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        var token = NewLink(f);

        using var r = await client.GetAsync(Page + "?link=" + token, Ct);

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var location = Uri.UnescapeDataString(r.Headers.Location!.OriginalString);
        Assert.Contains("/login", location, StringComparison.Ordinal);
        Assert.Contains(Page + "?link=" + token, location, StringComparison.Ordinal);
        Assert.True(File.Exists(LinkFile(f)));
    }

    [Fact]
    public async Task Wrong_link_is_refused_and_kept()
    {
        await using var f = Make();
        var client = await SignedInAsync(f);
        NewLink(f);

        var page = await GetPageAsync(client, Page + "?link=not-the-link");

        Assert.Contains(TwoFactorModel.LinkRunOut, MainText(page), StringComparison.Ordinal);
        Assert.Null(page.QuerySelector("#two-factor-secret"));
        Assert.True(File.Exists(LinkFile(f)));
        Assert.Equal(1, AuditCount(f, "two-factor-link-refused"));
    }

    [Fact]
    public async Task Owner_on_the_grid_asset_plan_needs_no_proof()
    {
        await using var f = Make(OwnerPlan.GridAsset);
        var client = await SignedInAsync(f);

        var page = await GetPageAsync(client);

        Assert.NotNull(page.QuerySelector("#two-factor-secret"));
        Assert.DoesNotContain(TwoFactorModel.LinkCommand, MainText(page), StringComparison.Ordinal);
    }
}
