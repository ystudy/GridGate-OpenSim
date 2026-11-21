using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core.Pictures;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Core.Theme;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// The public side's shared look (spec 2026-11-17-public-look-design): always dark, its own stylesheet, the top bar and the
/// footer on every page that uses _GuestLayout. Staff pages and the viewer splash page keep their own look.
/// </summary>
public sealed class PublicLookTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] GuestPages = ["/home", "/signup", "/support", "/guest/sign-in"];

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

    private static async Task<HttpClient> StaffAsync(LauncherFactory f, string role)
    {
        var client = Client(f);
        using var r = await client.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return client;
    }

    private static async Task<IDocument> PageAsync(HttpClient client, string path)
    {
        using var r = await client.GetAsync(path, Ct);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{path} answered {(int)r.StatusCode}");
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static List<string> Sheets(IDocument page) =>
        page.QuerySelectorAll("link[rel=stylesheet]").Select(l => l.GetAttribute("href")!.Split('?')[0]).ToList();

    // ---- Task 2: the top bar ----

    [Fact]
    public async Task Visitor_top_bar_has_login_register_and_menu()
    {
        await using var f = Factory();
        var page = await PageAsync(Client(f), "/home");

        var login = page.QuerySelector("header a#login-link")!;
        Assert.Equal("/guest/sign-in", login.GetAttribute("href"));
        Assert.Equal("Sign in", login.TextContent.Trim());
        Assert.NotNull(page.QuerySelector("header details#site-menu > summary"));
        Assert.Equal(["/home", "/map", "/guest/connect", "/support", "/guest/forgot-password"],
            page.QuerySelectorAll("header details#site-menu nav a").Select(a => a.GetAttribute("href")));
        Assert.NotNull(page.QuerySelector("header details#site-menu form#language-picker"));
        Assert.Null(page.QuerySelector("#resident-link"));
        Assert.Null(page.QuerySelector("img#site-logo"));
        Assert.Equal("/home", page.QuerySelector("header a#site-home")!.GetAttribute("href"));
        Assert.Equal("Avalon", page.QuerySelector("header a#site-home .grid-name")!.TextContent.Trim());
    }

    [Fact]
    public async Task Register_shows_only_while_sign_up_is_open()
    {
        await using (var open = SignUpsTests.Factory())
            Assert.Equal("Register", (await PageAsync(Client(open.F), "/home")).QuerySelector("header a#register-link[href='/signup']")!.TextContent.Trim());
        await using (var closed = SignUpsTests.Factory(open: false))
        {
            var page = await PageAsync(Client(closed.F), "/home");
            Assert.Null(page.QuerySelector("header a#register-link"));
            Assert.NotNull(page.QuerySelector("header a#login-link")); // signing in still works
        }
    }

    [Fact]
    public async Task Top_bar_shows_the_logo_when_one_is_set()
    {
        await using var f = Factory();
        var name = await SetLogoAsync(f);

        var page = await PageAsync(Client(f), "/home");

        var logo = page.QuerySelector("header a#site-home img#site-logo")!;
        Assert.Equal("/theme/images/" + name, logo.GetAttribute("src"));
        Assert.Equal("Avalon", logo.GetAttribute("alt"));
    }

    [Fact]
    public async Task Top_bar_hides_a_logo_whose_file_is_gone()
    {
        await using var f = Factory();
        var name = await SetLogoAsync(f);
        var pictures = f.Services.GetRequiredKeyedService<PictureStore>("theme");
        File.Delete(pictures.Open(name)!.Value.Path);

        var page = await PageAsync(Client(f), "/home");

        Assert.Null(page.QuerySelector("img#site-logo"));
        Assert.Equal("Avalon", page.QuerySelector("header a#site-home .grid-name")!.TextContent.Trim());
    }

    private static async Task<string> SetLogoAsync(LauncherFactory f)
    {
        var pictures = f.Services.GetRequiredKeyedService<PictureStore>("theme");
        var added = Assert.IsType<PictureResult.Added>(await pictures.AddAsync(new MemoryStream(TestPictures.Png(200, 80)), "logo.png", "Test Owner", Ct));
        f.Services.GetRequiredService<ThemeStore>().SetLogo(added.Picture.Name);
        return added.Picture.Name;
    }

    [Fact]
    public async Task Signed_in_resident_sees_name_and_sign_out()
    {
        await using var f = Factory();
        var client = Client(f);
        (await client.GetAsync("/__test/guest-signin", Ct)).Dispose();

        var page = await PageAsync(client, "/home");

        var resident = page.QuerySelector("header a#resident-link")!;
        Assert.Equal("/guest", resident.GetAttribute("href"));
        Assert.Equal("Test Guest", resident.TextContent.Trim());
        var signOut = page.QuerySelector("header form#sign-out-form")!;
        Assert.Equal("post", signOut.GetAttribute("method"));
        Assert.Equal("/guest/sign-out", signOut.GetAttribute("action"));
        Assert.NotNull(signOut.QuerySelector("input[name=__RequestVerificationToken]"));
        Assert.Equal("Sign out", signOut.QuerySelector("button[type=submit]")!.TextContent.Trim());
        Assert.Null(page.QuerySelector("#login-link"));
        Assert.Null(page.QuerySelector("#register-link"));
    }

    // The top bar's Sign out works from every public page, not only the front page: the token a page makes is for whichever
    // user that page saw (on /support and /signup the staff scheme's anonymous user, on /home and /guest the resident).
    [Theory]
    [InlineData("/home")]
    [InlineData("/support")]
    [InlineData("/signup")]
    [InlineData("/guest")]
    public async Task Top_bar_sign_out_works_from_every_public_page(string path)
    {
        await using var f = Factory();
        var client = Client(f);
        (await client.GetAsync("/__test/guest-signin", Ct)).Dispose();
        var token = (await PageAsync(client, path)).QuerySelector("form#sign-out-form input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

        using var r = await client.PostAsync("/guest/sign-out", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }), Ct);

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/", r.Headers.Location?.OriginalString);
        Assert.Null((await PageAsync(client, "/home")).QuerySelector("#resident-link")); // signed out
    }

    [Fact]
    public async Task Sign_out_still_needs_its_token()
    {
        await using var f = Factory();
        var client = Client(f);
        (await client.GetAsync("/__test/guest-signin", Ct)).Dispose();

        using var r = await client.PostAsync("/guest/sign-out", new FormUrlEncodedContent(new Dictionary<string, string>()), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.NotNull((await PageAsync(client, "/home")).QuerySelector("#resident-link")); // still signed in
    }

    [Fact]
    public async Task Resident_name_keeps_its_own_case()
    {
        await using var f = Factory();
        var client = Client(f);
        (await client.GetAsync("/__test/guest-signin", Ct)).Dispose();

        var name = (await PageAsync(client, "/home")).QuerySelector("#resident-link")!;

        Assert.DoesNotContain("site-account", name.ClassList); // .site-account is uppercase: a name is shown as written
    }

    [Fact]
    public async Task Staff_with_resident_cookie_sees_only_back_to_staff()
    {
        await using var f = Factory();
        var client = await StaffAsync(f, "admin");
        (await client.GetAsync("/__test/guest-signin", Ct)).Dispose();

        var page = await PageAsync(client, "/home");

        Assert.NotNull(page.QuerySelector("header a#staff-side-link"));
        Assert.Null(page.QuerySelector("#login-link"));
        Assert.Null(page.QuerySelector("#register-link"));
        Assert.Null(page.QuerySelector("#resident-link"));
    }

    [Fact]
    public async Task Get_sign_out_does_not_sign_out()
    {
        await using var f = Factory();
        var client = Client(f);
        (await client.GetAsync("/__test/guest-signin", Ct)).Dispose();

        using var r = await client.GetAsync("/guest/sign-out", Ct);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, r.StatusCode);
        Assert.NotNull((await PageAsync(client, "/home")).QuerySelector("#resident-link")); // still signed in
    }

    // ---- Task 3: the footer ----

    [Fact]
    public async Task Footer_has_legal_copyright_and_staff_sign_in()
    {
        await using var f = Factory();
        var client = Client(f);
        foreach (var path in new[] { "/home", "/signup", "/support" })
        {
            var page = await PageAsync(client, path);
            Assert.Equal("© Avalon. All rights reserved.", page.QuerySelector("footer#site-footer p#site-copyright")!.TextContent.Trim());
            Assert.Equal("/login", page.QuerySelector("footer#site-footer a#staff-link")!.GetAttribute("href"));
            Assert.Null(page.QuerySelector("header #staff-link"));
        }
    }

    [Fact]
    public async Task Footer_hides_staff_sign_in_for_staff()
    {
        await using var f = Factory();
        var page = await PageAsync(await StaffAsync(f, "admin"), "/home");
        Assert.Null(page.QuerySelector("#staff-link"));
        Assert.NotNull(page.QuerySelector("footer#site-footer p#site-copyright"));
    }

    // ---- Task 1: the always-dark public stylesheet ----

    [Fact]
    public async Task Guest_pages_are_always_dark_and_load_guest_css()
    {
        await using var f = Factory();
        var client = Client(f);
        foreach (var path in GuestPages)
        {
            var page = await PageAsync(client, path);
            Assert.Equal("dark", page.DocumentElement.GetAttribute("data-bs-theme"));
            var sheets = Sheets(page);
            Assert.Contains("/css/guest.css", sheets);
            Assert.True(sheets.IndexOf("/css/guest.css") > sheets.IndexOf("/css/site.css"), path + ": guest.css must come after site.css");
            Assert.Null(page.QuerySelector("script[src^='/js/theme.js']"));
            Assert.Contains("guest-site", page.Body!.ClassList);
        }
    }

    [Fact]
    public async Task Staff_pages_do_not_load_guest_css()
    {
        await using var f = Factory();
        var page = await PageAsync(await StaffAsync(f, "admin"), "/profile/sessions");
        Assert.DoesNotContain("/css/guest.css", Sheets(page));
        Assert.NotNull(page.QuerySelector("script[src^='/js/theme.js']"));
    }

    [Fact]
    public async Task Splash_page_is_unchanged()
    {
        await using var f = Factory();
        var page = await PageAsync(Client(f), "/welcome");
        Assert.DoesNotContain("/css/guest.css", Sheets(page));
    }

    [Fact]
    public async Task Guest_css_is_served()
    {
        await using var f = Factory();
        using var r = await Client(f).GetAsync("/css/guest.css", Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/css", r.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("text", "bg")]
    [InlineData("muted", "bg")]
    [InlineData("text", "surface")]
    [InlineData("muted", "surface")]
    [InlineData("yellow-text", "yellow")]
    [InlineData("blue-text", "blue")]
    [InlineData("link", "bg")]
    [InlineData("link", "surface")]
    public void Guest_colours_meet_contrast(string fore, string back)
    {
        var vars = GuestCssColours();
        var ratio = Contrast(vars[fore], vars[back]);
        Assert.True(ratio >= 4.5, $"--gg-{fore} on --gg-{back}: contrast {ratio:0.00}, WCAG AA needs 4.5");
    }

    private static Dictionary<string, string> GuestCssColours()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridGate.sln"))) dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("GridGate.sln not found");
        var css = File.ReadAllText(Path.Combine(root, "Launcher", "GridGate.Launcher", "wwwroot", "css", "guest.css"));
        return Regex.Matches(css, @"--gg-([a-z-]+)\s*:\s*(#[0-9a-fA-F]{6})\s*;")
            .GroupBy(m => m.Groups[1].Value)
            .ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
    }

    private static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int i)
        {
            var c = int.Parse(hex.AsSpan(1 + i * 2, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
    }
}
