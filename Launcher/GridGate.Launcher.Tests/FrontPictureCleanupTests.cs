using GridGate.Launcher.Core.Front;
using GridGate.Launcher.Core.Pictures;
using GridGate.Launcher.Core.Splash;
using GridGate.Launcher.Front;
using GridGate.Launcher.Splash;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// Unused front page pictures go after 30 days, but a picture any saved setting mentions stays, even an own list that isn't
/// showing; and the front and splash cleanups never touch each other's pictures (spec 2026-11-19-front-page-design).
/// </summary>
public sealed class FrontPictureCleanupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> AddAsync(PictureStore pictures)
    {
        var added = Assert.IsType<PictureResult.Added>(await pictures.AddAsync(new MemoryStream(TestPictures.Png(64, 64)), "test.png", "Test Owner", Ct));
        return added.Picture.Name;
    }

    private static PictureStore Front(LauncherFactory f) => f.Services.GetRequiredKeyedService<PictureStore>("front");

    [Fact]
    public async Task Removes_a_picture_no_setting_mentions_after_30_days()
    {
        await using var f = new LauncherFactory();
        var cleanup = f.Services.GetRequiredService<FrontPictureCleanup>();
        var name = await AddAsync(Front(f));

        await cleanup.RunOnceAsync(Ct);
        f.Time.Advance(TimeSpan.FromDays(29));
        await cleanup.RunOnceAsync(Ct);
        Assert.True(Front(f).Exists(name));

        f.Time.Advance(TimeSpan.FromDays(1));
        await cleanup.RunOnceAsync(Ct);

        Assert.False(Front(f).Exists(name));
        Assert.Contains("front-image-deleted", AuditText(f));
    }

    private static string AuditText(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return "";
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd();
    }

    [Fact]
    public async Task Keeps_own_list_pictures_while_the_source_is_splash()
    {
        await using var f = new LauncherFactory();
        var cleanup = f.Services.GetRequiredService<FrontPictureCleanup>();
        var store = f.Services.GetRequiredService<FrontPageStore>();
        var name = await AddAsync(Front(f));
        var place = new SplashPlace(Guid.NewGuid(), "Harbour", new Dictionary<string, string> { ["en"] = "Boats" }, name);
        Assert.Empty(store.Save(FrontPageSettings.Default with { PlacesSource = FrontPlacesSource.Splash, Places = [place] }));

        await cleanup.RunOnceAsync(Ct);
        f.Time.Advance(TimeSpan.FromDays(31));
        await cleanup.RunOnceAsync(Ct);

        Assert.True(Front(f).Exists(name));
    }

    [Fact]
    public async Task Never_touches_splash_pictures_and_the_splash_cleanup_never_touches_front_ones()
    {
        await using var f = new LauncherFactory();
        var front = f.Services.GetRequiredService<FrontPictureCleanup>();
        var splash = f.Services.GetRequiredService<SplashPictureCleanup>();
        var splashPictures = f.Services.GetRequiredKeyedService<PictureStore>("splash");
        var splashName = await AddAsync(splashPictures);
        var frontName = await AddAsync(Front(f));
        // Each picture is used only by its own page: the splash draft's background, and the front page's photo. Neither page
        // mentions the other's, so a cleanup that looked at the other store would remove it after 30 days.
        f.Services.GetRequiredService<SplashStore>().SaveDraft(
            new SplashDesign(1280, 800, new SplashBackground("#000000", new SplashPicture(splashName, SplashFit.Fill), 50, 50, 0), false, 16, []), 0, "A");
        Assert.Empty(f.Services.GetRequiredService<FrontPageStore>().Save(FrontPageSettings.Default with { Photo = frontName }));

        await front.RunOnceAsync(Ct);
        await splash.RunOnceAsync(Ct);
        f.Time.Advance(TimeSpan.FromDays(31));
        await front.RunOnceAsync(Ct);
        await splash.RunOnceAsync(Ct);

        Assert.True(splashPictures.Exists(splashName));
        Assert.True(Front(f).Exists(frontName));
    }
}
