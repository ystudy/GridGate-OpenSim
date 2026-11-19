namespace GridGate.Launcher.Core.Front;

/// <summary>
/// The two "in use" sets of the front page's pictures (spec 2026-11-19-front-page-design, Constraints): what anyone may fetch
/// (what the page shows now), and what the cleanup keeps (what any saved setting mentions, so switching a source away and back
/// never loses a picture).
/// </summary>
public static class FrontPictures
{
    /// <summary>The photo, and the own places' pictures while the places come from the own list.</summary>
    public static IReadOnlySet<string> Shown(FrontPageSettings s) =>
        Names(s.Photo, s.PlacesSource == FrontPlacesSource.Own ? s.Places.Select(p => p.Picture) : []);

    /// <summary>The photo and every own place's picture, whichever source is chosen.</summary>
    public static IReadOnlySet<string> Kept(FrontPageSettings s) => Names(s.Photo, s.Places.Select(p => p.Picture));

    private static HashSet<string> Names(string? photo, IEnumerable<string?> places) =>
        [.. places.Append(photo).OfType<string>()];
}
