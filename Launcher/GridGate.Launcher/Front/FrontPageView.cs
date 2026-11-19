using System.Globalization;
using GridGate.Launcher.Core.Front;
using GridGate.Launcher.Core.Guests;
using GridGate.Launcher.Core.Pictures;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Core.Splash;
using GridGate.Launcher.Endpoints;
using GridGate.Launcher.Splash;

namespace GridGate.Launcher.Front;

/// <summary>Where the front page's main button sends a visitor.</summary>
public enum FrontMainButton { CreateAccount, SignIn, YourAccount }

/// <summary>A featured place's card: the line and picture are null when there is none.</summary>
public sealed record FrontCard(string Name, string? Line, string? PictureUrl);

public sealed record FrontNews(DateOnly Date, string Text);

/// <param name="Key">The <c>ResidentText</c> key of the tile's label.</param>
/// <param name="Value">The number, formatted for the page's language.</param>
public sealed record FrontNumber(string Key, string Value);

/// <summary>What the front page shows; an empty list hides its section.</summary>
public sealed record FrontView(string? PhotoUrl, FrontMainButton Main, IReadOnlyList<FrontNumber> Numbers, IReadOnlyList<FrontCard> Places,
    IReadOnlyList<FrontNews> News);

/// <summary>
/// Resolves each section of the front page from the source the owner chose (spec 2026-11-19-front-page-design): the published
/// splash design, the front page's own lists, or the automatic list; the live parts come from <see cref="FrontPageSnapshot"/>.
/// The owner's text shows in the visitor's language, else in English, else not at all.
/// </summary>
public sealed class FrontPageView(FrontPageStore store, SplashStore splash, FrontPageSnapshot snapshot,
    [FromKeyedServices("front")] PictureStore pictures, Func<LauncherSettings> settings, FrontMapTiles tiles)
{
    public const int MaxNews = 5;

    public async Task<FrontView> BuildAsync(string language, bool residentSignedIn, CancellationToken ct)
    {
        var s = store.Load();
        var live = await snapshot.CurrentAsync(ct);
        var main = residentSignedIn ? FrontMainButton.YourAccount
            : SignUpRules.IsOpen(settings()) ? FrontMainButton.CreateAccount : FrontMainButton.SignIn;
        var published = splash.Published()?.Design;

        IEnumerable<FrontCard> places = s.PlacesSource switch
        {
            FrontPlacesSource.Own => s.Places.Select(p => Card(p, language, FrontApi.PictureUrl)),
            // A region the grid has no map tile for gets the blank card rather than a picture that would be a 404.
            FrontPlacesSource.Automatic => live.Busiest.Select(p => new FrontCard(p.Name, null, tiles.KnownMissing(p.RegionId) ? null : FrontApi.MapUrl(p.RegionId))),
            // Top to bottom, then left to right, as the splash page shows them (the design keeps blocks in the order they were made).
            _ => Blocks(published, SplashBlockKind.Places).OrderBy(b => b.Y).ThenBy(b => b.X).SelectMany(b => b.Content.Places ?? []).Select(p => Card(p, language, SplashSnapshot.PictureUrl)),
        };
        var news = s.NewsSource == FrontNewsSource.Own ? s.News : Blocks(published, SplashBlockKind.News).SelectMany(b => b.Content.News ?? []);

        return new FrontView(
            s.Photo is { } photo && pictures.Exists(photo) ? FrontApi.PictureUrl(photo) : null,
            main,
            Numbers(s.Numbers, live),
            [.. places],
            [.. news.Where(n => !n.Hidden)
                .Select(n => (n.Date, Text: Words(n.Text, language)))
                .Where(n => n.Text is not null)
                .OrderByDescending(n => n.Date)
                .Take(MaxNews)
                .Select(n => new FrontNews(n.Date, n.Text!))]);
    }

    private static IEnumerable<SplashBlock> Blocks(SplashDesign? design, SplashBlockKind kind) =>
        design?.Blocks.Where(b => b.Kind == kind) ?? [];

    private static FrontCard Card(SplashPlace p, string language, Func<string, string> pictureUrl) =>
        new(p.RegionName, Words(p.Line, language), p.Picture is { } picture ? pictureUrl(picture) : null);

    /// <summary>The visitor's language, else English, else nothing; blank text counts as none.</summary>
    private static string? Words(IReadOnlyDictionary<string, string>? text, string language) =>
        text is null ? null
            : text.GetValueOrDefault(language) is { Length: > 0 } own && !string.IsNullOrWhiteSpace(own) ? own
            : text.GetValueOrDefault("en") is { } en && !string.IsNullOrWhiteSpace(en) ? en
            : null;

    private static List<FrontNumber> Numbers(FrontNumbers on, FrontLive live)
    {
        var culture = CultureInfo.CurrentUICulture;
        var list = new List<FrontNumber>();
        void Add(bool shown, long? value, string key)
        {
            if (shown && value is { } v) list.Add(new FrontNumber(key, v.ToString("N0", culture)));
        }
        Add(on.InWorld, live.InWorld, "Home.InWorld");
        Add(on.Regions, live.RegionsOnline, "Home.RegionsOnline");
        Add(on.Registered, live.Registered, "Home.Registered");
        Add(on.Active, live.ActiveThisMonth, "Home.Active");
        return list;
    }
}
