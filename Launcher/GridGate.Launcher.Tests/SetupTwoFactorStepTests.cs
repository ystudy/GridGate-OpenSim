using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Pages.Profile;
using GridGate.Launcher.Security;
using GridGate.Launcher.Setup;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// Setup's "Turn on two-factor" step on the Region Server and Connected plans: right after "Your avatar", with the proof the
/// avatar step hands out (the setup link got the owner there).
/// </summary>
public sealed class SetupTwoFactorStepTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HtmlParser Parser = new();
    private const string Token = "tok-1234567890abcdefghijklmnopqrstuv"; // the setup tests' made-up token (allowed in .gitleaks.toml)
    private const string Step = "/setup/two-factor";

    private static JoinedGrid Avalon() =>
        new("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", LauncherVersion.Current, false, 8004, 8006);

    private static readonly ConnectedGrid OsGrid =
        new("osgrid", "OSGrid", "http://login.osgrid.org/", "http://login.osgrid.org/", "http://grid.osgrid.org/", null);

    private static LauncherSettings Settings(OwnerPlan plan, SetupState setup) => new()
    {
        Db = new DatabaseSecrets("", "", ""), Console = new ConsoleSecrets("", ""), MachineId = "", Plan = plan,
        Join = plan == OwnerPlan.RegionServer ? Avalon() : null,
        Connected = plan == OwnerPlan.Connected ? OsGrid : null,
        Setup = setup,
    };

    private static readonly SetupState AvatarDone = new(WorldNamed: true, OwnerCreated: true,
        Owner: new OwnerInfo("Test", "Owner", TestAuth.OwnerId), NetworkSet: true);

    [Theory]
    [InlineData(OwnerPlan.RegionServer)]
    [InlineData(OwnerPlan.Connected)]
    public void Region_and_connected_steps_list_two_factor_after_the_avatar(OwnerPlan plan)
    {
        var paths = SetupSteps.For(Settings(plan, new SetupState())).Select(st => st.Path).ToList();

        var avatar = paths.IndexOf("/setup/avatar");
        Assert.Equal(Step, paths[avatar + 1]);
        Assert.Equal("/setup/region", paths[avatar + 2]);
        Assert.Equal(SetupSteps.TwoFactorPath, Step);
        Assert.Equal("Turn on two-factor", SetupSteps.For(Settings(plan, new SetupState()))[avatar + 1].Title);
    }

    [Theory]
    [InlineData(OwnerPlan.Local)]
    [InlineData(OwnerPlan.SimplePublic)]
    [InlineData(OwnerPlan.GridAsset)]
    public void Other_plans_have_no_two_factor_step(OwnerPlan plan) =>
        Assert.DoesNotContain(SetupSteps.For(Settings(plan, new SetupState())), st => st.Path == Step);

    [Fact]
    public void Region_step_waits_for_two_factor()
    {
        Assert.False(SetupSteps.CanOpen(Settings(OwnerPlan.RegionServer, AvatarDone), "/setup/region"));
        Assert.Equal(Step, SetupSteps.FirstUnfinished(Settings(OwnerPlan.RegionServer, AvatarDone)));
        Assert.True(SetupSteps.CanOpen(Settings(OwnerPlan.RegionServer, AvatarDone with { OwnerTwoFactorSet = true }), "/setup/region"));
    }

    [Fact]
    public void Finished_install_never_sees_the_step()
    {
        var finished = AvatarDone with { RegionCreated = true, Finished = true };
        Assert.True(SetupSteps.For(Settings(OwnerPlan.RegionServer, finished)).All(st => st.Done(Settings(OwnerPlan.RegionServer, finished))));
    }

    /// <summary>A Region Server that has joined Avalon, before its owner exists.</summary>
    private static LauncherFactory BeforeAvatar()
    {
        var f = new LauncherFactory(s => s with
        {
            SetupToken = Token,
            Plan = OwnerPlan.RegionServer,
            Join = Avalon(),
            GridName = "Avalon",
            Setup = s.Setup with { WorldNamed = true, NetworkSet = true },
        })
        {
            Before = TestAuth.Apply,
        };
        f.Grid.EstateOwnerFound = ("Ann", "Lee");
        return f;
    }

    /// <summary>At the two-factor step on <paramref name="plan"/>, the owner being <see cref="TestAuth.OwnerId"/>.</summary>
    private static LauncherFactory AtStep(OwnerPlan plan = OwnerPlan.RegionServer)
    {
        var f = new LauncherFactory(s => s with
        {
            SetupToken = null,
            Plan = plan,
            Join = plan == OwnerPlan.RegionServer ? Avalon() : null,
            Connected = plan == OwnerPlan.Connected ? OsGrid : null,
            GridName = "Avalon",
            Recovery = null,
            Setup = AvatarDone,
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

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Dictionary<string, string> form)
    {
        var page = Parser.ParseDocument(await client.GetStringAsync(url.Split('?')[0], Ct));
        form["__RequestVerificationToken"] = page.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        return await client.PostAsync(url, new FormUrlEncodedContent(form), Ct);
    }

    private static async Task<IHtmlDocument> GetPageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
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

    private static Task<HttpResponseMessage> TurnOnAsync(HttpClient client, LauncherFactory f, IHtmlDocument page)
    {
        var secret = FromBase32(page.QuerySelector("#two-factor-secret")!.TextContent);
        return client.PostAsync(Step + "?handler=TurnOn", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Code"] = Totp.Code(secret, Totp.StepAt(f.Time.GetUtcNow())),
            ["Shown"] = page.QuerySelector("form#turn-on input[name=Shown]")!.GetAttribute("value")!,
            ["__RequestVerificationToken"] = page.QuerySelector("form#turn-on input[name=__RequestVerificationToken]")!.GetAttribute("value")!,
        }), Ct);
    }

    /// <summary>Through the setup link and the avatar step; returns the signed-in client.</summary>
    private static async Task<HttpClient> ThroughAvatarAsync(LauncherFactory f)
    {
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync("/setup?token=" + Token, Ct)).Dispose();
        using var resp = await PostAsync(client, "/setup/avatar", new() { ["FirstName"] = "Ann", ["LastName"] = "Lee" });
        Assert.Equal("/setup", resp.Headers.Location?.OriginalString);
        Assert.Contains(resp.Headers.GetValues("Set-Cookie"), c => c.StartsWith(OwnerEnrolProof.CookieName + "=", StringComparison.Ordinal));
        return client;
    }

    private static Guid OwnerOf(LauncherFactory f) => f.Store.LoadOrCreate().Setup.Owner!.PrincipalId;

    [Fact]
    public async Task Avatar_step_issues_the_proof_and_goes_on_to_two_factor()
    {
        await using var f = BeforeAvatar();
        var client = await ThroughAvatarAsync(f);

        using (var next = await client.GetAsync("/setup", Ct))
            Assert.Equal(Step, next.Headers.Location?.OriginalString);
        var page = await GetPageAsync(client, Step);

        Assert.NotNull(page.QuerySelector("#two-factor-secret"));
        Assert.Equal("Turn on two-factor", page.QuerySelector("h1")?.TextContent.Trim());
    }

    [Fact]
    public async Task Step_turns_it_on_and_setup_goes_on()
    {
        await using var f = BeforeAvatar();
        var client = await ThroughAvatarAsync(f);
        var page = await GetPageAsync(client, Step);

        using var on = await TurnOnAsync(client, f, page);

        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.Equal(TwoFactorState.On, f.Services.GetRequiredService<TwoFactorStore>().StateOf(OwnerOf(f)));
        Assert.True(f.Store.LoadOrCreate().Setup.OwnerTwoFactorSet);
        var shown = Parser.ParseDocument(await on.Content.ReadAsStringAsync(Ct));
        Assert.NotEmpty(shown.QuerySelectorAll("#backup-codes li"));
        Assert.NotNull(shown.QuerySelector("a[href='/setup']"));
        Assert.Contains(on.Headers.GetValues("Set-Cookie"), c => c.StartsWith(OwnerEnrolProof.CookieName + "=;", StringComparison.Ordinal));
        using var next = await client.GetAsync("/setup", Ct);
        Assert.Equal("/setup/region", next.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Step_after_the_hour_shows_the_command()
    {
        await using var f = BeforeAvatar();
        var client = await ThroughAvatarAsync(f);
        f.Time.Advance(OwnerEnrolProof.FromSetup + TimeSpan.FromSeconds(1));

        var page = await GetPageAsync(client, Step);

        Assert.Null(page.QuerySelector("#two-factor-secret"));
        Assert.Contains(TwoFactorModel.LinkCommand, page.QuerySelector("main")!.TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OwnerPlan.RegionServer)]
    [InlineData(OwnerPlan.Connected)]
    public async Task Step_without_proof_shows_the_command_and_refuses_turn_on(OwnerPlan plan)
    {
        await using var f = AtStep(plan);
        var client = await SignedInAsync(f);

        var page = await GetPageAsync(client, Step);

        Assert.Null(page.QuerySelector("#two-factor-secret"));
        Assert.Contains(TwoFactorModel.LinkCommand, page.QuerySelector("main")!.TextContent, StringComparison.Ordinal);
        using var post = await client.PostAsync(Step + "?handler=TurnOn", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Code"] = "123456",
            ["Shown"] = "",
            ["__RequestVerificationToken"] = page.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!,
        }), Ct);
        Assert.Equal(TwoFactorState.Off, f.Services.GetRequiredService<TwoFactorStore>().StateOf(TestAuth.OwnerId));
        Assert.False(f.Store.LoadOrCreate().Setup.OwnerTwoFactorSet);
    }

    [Fact]
    public async Task Step_counts_as_done_when_two_factor_is_already_on()
    {
        await using var f = AtStep();
        var client = await SignedInAsync(f);
        OwnerTwoFactorKit.Enrol(f);

        using var r = await client.GetAsync(Step, Ct);

        Assert.Equal("/setup", r.Headers.Location?.OriginalString);
        Assert.True(f.Store.LoadOrCreate().Setup.OwnerTwoFactorSet);
    }

    [Fact]
    public async Task Step_is_closed_to_an_admin()
    {
        await using var f = AtStep();
        var client = await SignedInAsync(f, "admin");

        using var r = await client.GetAsync(Step, Ct);

        Assert.Equal("/setup", r.Headers.Location?.OriginalString);
    }
}
