using System.Text.Json.Serialization;
using GridGate.Launcher.Core.Splash;

namespace GridGate.Launcher.Core.Front;

// The public front page's own settings (spec 2026-11-19-front-page-design): a choice of source per section, and the front
// page's own lists. Places and news items are the splash design's records, so both editors and pages share one shape.
// Enums are stored by name, so the file reads well.

/// <summary>Where the front page's featured places come from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FrontPlacesSource>))]
public enum FrontPlacesSource { Splash, Own, Automatic }

/// <summary>Where the front page's news comes from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FrontNewsSource>))]
public enum FrontNewsSource { Splash, Own }

/// <summary>Which live numbers the front page shows; all on by default.</summary>
public sealed record FrontNumbers(bool InWorld = true, bool Regions = true, bool Registered = true, bool Active = true);

/// <param name="Photo">A front picture-store name, or null for the navy gradient.</param>
/// <param name="Places">The front page's own list, kept whichever source is chosen.</param>
/// <param name="News">The front page's own news, kept whichever source is chosen.</param>
public sealed record FrontPageSettings(
    string? Photo,
    FrontNumbers Numbers,
    FrontPlacesSource PlacesSource,
    IReadOnlyList<SplashPlace> Places,
    FrontNewsSource NewsSource,
    IReadOnlyList<SplashNewsItem> News)
{
    /// <summary>No photo, every number, and the splash page's places and news.</summary>
    public static FrontPageSettings Default { get; } = new(null, new FrontNumbers(), FrontPlacesSource.Splash, [], FrontNewsSource.Splash, []);
}
