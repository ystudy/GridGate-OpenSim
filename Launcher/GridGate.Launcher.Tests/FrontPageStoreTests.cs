using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Front;
using GridGate.Launcher.Core.Splash;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The front page's saved settings and which of its pictures are shown and kept (spec 2026-11-19-front-page-design).</summary>
public sealed class FrontPageStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gg-front-" + Guid.NewGuid().ToString("N"));
    private const string Pic = "0123456789abcdef0123456789abcdef.png";
    private const string Photo = "fedcba9876543210fedcba9876543210.jpg";

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private FrontPageStore Store(Func<string, bool>? exists = null) =>
        new(new LauncherPaths(_root, _root, _root)) { PictureExists = exists ?? (_ => true) };

    private static SplashPlace Place(string name, string? picture = null) =>
        new(Guid.NewGuid(), name, new Dictionary<string, string> { ["en"] = "A line about " + name }, picture);

    private static FrontPageSettings Full() => new(
        Photo,
        new FrontNumbers(InWorld: false, Regions: true, Registered: false, Active: true),
        FrontPlacesSource.Own,
        [Place("Harbour", Pic), Place("Hills")],
        FrontNewsSource.Own,
        [new SplashNewsItem(new DateOnly(2026, 11, 19), new Dictionary<string, string> { ["en"] = "Opening night" }, false)]);

    [Fact]
    public void Missing_file_loads_the_defaults()
    {
        var s = Store().Load();

        Assert.Null(s.Photo);
        Assert.Equal(new FrontNumbers(), s.Numbers);
        Assert.Equal(FrontPlacesSource.Splash, s.PlacesSource);
        Assert.Empty(s.Places);
        Assert.Equal(FrontNewsSource.Splash, s.NewsSource);
        Assert.Empty(s.News);
    }

    [Fact]
    public void Saves_and_loads_every_field()
    {
        var full = Full();

        Assert.Empty(Store().Save(full));
        var back = Store().Load(); // a new store reads the file

        Assert.Equal(full.Photo, back.Photo);
        Assert.Equal(full.Numbers, back.Numbers);
        Assert.Equal(full.PlacesSource, back.PlacesSource);
        Assert.Equal(full.Places.Select(p => (p.RegionId, p.RegionName, p.Line["en"], p.Picture)),
            back.Places.Select(p => (p.RegionId, p.RegionName, p.Line["en"], p.Picture)));
        Assert.Equal(full.NewsSource, back.NewsSource);
        Assert.Equal(full.News.Select(n => (n.Date, n.Text["en"], n.Hidden)), back.News.Select(n => (n.Date, n.Text["en"], n.Hidden)));
    }

    [Fact]
    public void Refuses_more_places_than_the_limit()
    {
        var store = Store();
        var tooMany = FrontPageSettings.Default with { Places = [.. Enumerable.Range(0, SplashRules.MaxPlaces + 1).Select(i => Place("R" + i))] };

        var problems = store.Save(tooMany);

        Assert.Single(problems);
        Assert.Empty(store.Load().Places); // nothing saved
    }

    [Fact]
    public void Refuses_a_bad_or_missing_picture_name()
    {
        Assert.NotEmpty(Store().Save(FrontPageSettings.Default with { Photo = "../secret.png" }));
        Assert.NotEmpty(Store(exists: _ => false).Save(FrontPageSettings.Default with { Photo = Photo }));
    }

    [Fact]
    public void Refuses_a_line_longer_than_the_splash_limit()
    {
        var place = Place("Harbour") with { Line = new Dictionary<string, string> { ["en"] = new string('x', SplashRules.MaxPlaceLine + 1) } };

        Assert.NotEmpty(Store().Save(FrontPageSettings.Default with { Places = [place] }));
    }

    [Theory]
    [InlineData(FrontPlacesSource.Own, true)]
    [InlineData(FrontPlacesSource.Splash, false)]
    [InlineData(FrontPlacesSource.Automatic, false)]
    public void Own_pictures_are_shown_only_while_the_source_is_own(FrontPlacesSource source, bool shown)
    {
        var s = Full() with { PlacesSource = source };

        Assert.Equal(shown, FrontPictures.Shown(s).Contains(Pic));
        Assert.Contains(Pic, FrontPictures.Kept(s));
    }

    [Fact]
    public void Photo_is_always_shown_and_kept()
    {
        var s = Full() with { PlacesSource = FrontPlacesSource.Automatic };

        Assert.Contains(Photo, FrontPictures.Shown(s));
        Assert.Contains(Photo, FrontPictures.Kept(s));
    }

    [Fact]
    public void A_picture_in_use_is_not_removed()
    {
        var store = Store();
        store.Save(Full() with { PlacesSource = FrontPlacesSource.Splash });
        var deleted = new List<string>();

        Assert.False(store.RemovePictureIfUnused(Pic, n => { deleted.Add(n); return true; }));
        Assert.True(store.RemovePictureIfUnused("00000000000000000000000000000000.png", n => { deleted.Add(n); return true; }));
        Assert.Equal(["00000000000000000000000000000000.png"], deleted);
    }
}
