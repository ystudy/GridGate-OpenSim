using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core.Settings;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// The account pages (spec 2026-11-20-account-pages-design): sign in, the sign-in code, forgot password, choose a new password
/// and sign up show the page's headline on the left and the form's card on the right. The staff sign-in keeps its own card.
/// </summary>
public sealed class AccountPagesLookTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The page has the split: the headline (and, when <paramref name="line"/>, the page's line) in the left column, the card in
    /// the right one, and none of the old small card.
    /// </summary>
    internal static void AssertSplit(IDocument page, string headline, bool line)
    {
        var split = page.QuerySelector(".account-split");
        Assert.NotNull(split);
        var h1 = page.QuerySelector(".account-split > .account-lead > h1");
        Assert.NotNull(h1);
        Assert.Equal(headline, h1.TextContent.Trim());
        Assert.Equal(line, page.QuerySelector(".account-split > .account-lead > p.account-line") is not null);
        Assert.NotNull(page.QuerySelector(".account-split > .account-card.card"));
        Assert.Null(page.QuerySelector(".sign-in-card"));
    }

    private static LauncherFactory Factory()
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = TestAuth.Apply,
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static HttpClient Client(LauncherFactory f) => f.CreateClient(new() { AllowAutoRedirect = false });

    private static async Task<IDocument> PageAsync(HttpClient client, string path)
    {
        using var r = await client.GetAsync(path, Ct);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{path} answered {(int)r.StatusCode}");
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static string Token(IDocument page) =>
        page.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value") ?? throw new InvalidOperationException("no token");

    [Fact]
    public async Task Sign_in_has_headline_left_and_form_right()
    {
        await using var f = Factory();
        var page = await PageAsync(Client(f), "/guest/sign-in");

        AssertSplit(page, "Sign in", line: true);
        Assert.NotNull(page.QuerySelector(".account-card form input[name=Password]"));
    }

    [Fact]
    public async Task Sign_in_failure_keeps_the_error_in_the_card()
    {
        await using var f = Factory();
        var client = Client(f);
        var form = await PageAsync(client, "/guest/sign-in");

        using var r = await client.PostAsync("/guest/sign-in", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "Test Guest",
            ["Password"] = "not-the-password-9",
            ["__RequestVerificationToken"] = Token(form),
        }), Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var page = new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));

        AssertSplit(page, "Sign in", line: true);
        Assert.NotNull(page.QuerySelector(".account-card .error"));
    }

    [Fact]
    public async Task Sign_up_has_headline_left_and_wide_form_right()
    {
        await using var rig = SignUpsTests.Factory();
        var page = await PageAsync(Client(rig.F), "/signup");

        AssertSplit(page, "Create an account", line: true);
        Assert.NotNull(page.QuerySelector(".account-split.account-split-wide"));
        Assert.NotNull(page.QuerySelector(".account-card form#sign-up"));
    }

    [Fact]
    public async Task Sign_up_closed_shows_headline_alone()
    {
        await using var rig = SignUpsTests.Factory(open: false);
        var page = await PageAsync(Client(rig.F), "/signup");

        AssertSplit(page, "Create an account", line: false);
        Assert.Contains("This grid isn't open for sign-ups yet.", page.QuerySelector(".account-card")!.TextContent);
    }

    /// <summary>The message pages of sign-up, email change and help (spec 2026-11-21-last-pages-restyle-design) use the split too.</summary>
    [Theory]
    [InlineData("/guest/confirm", "Confirm your email", false)]
    [InlineData("/guest/confirm-email", "Confirm your new email", false)]
    [InlineData("/support/confirm", "Send your ticket", false)]
    [InlineData("/support/lost-link", "Lost your ticket link?", true)]
    [InlineData("/support/ticket?id=00000000-0000-0000-0000-000000000001", "This link has run out", false)]
    public async Task Message_pages_have_headline_left_and_card_right(string path, string headline, bool line)
    {
        await using var f = Factory();
        AssertSplit(await PageAsync(Client(f), path), headline, line);
    }

    [Fact]
    public async Task Staff_login_keeps_the_staff_card()
    {
        await using var f = Factory();
        var page = await PageAsync(Client(f), "/login");

        Assert.NotNull(page.QuerySelector(".sign-in-card"));
        Assert.Null(page.QuerySelector(".account-split"));
    }
}
