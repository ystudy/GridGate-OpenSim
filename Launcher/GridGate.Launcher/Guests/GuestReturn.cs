namespace GridGate.Launcher.Guests;

/// <summary>
/// Where the guest door goes after a sign-in (spec 2026-11-20-public-map-design, "Coming back after sign-in"): the page that
/// asked, when it is a path on this site, otherwise the resident's home page. Never another site, so the link can't be used
/// to send someone elsewhere with a real sign-in page in between.
/// </summary>
public static class GuestReturn
{
    public const string Home = "/guest";

    /// <summary>
    /// <paramref name="returnUrl"/> when it starts with one <c>/</c> (not <c>//</c> or <c>/\</c>, which browsers read as another
    /// host) and has no control characters; <see cref="Home"/> otherwise.
    /// </summary>
    public static string Pick(string? returnUrl) =>
        returnUrl is { Length: > 0 } url && url[0] == '/' && (url.Length == 1 || url[1] is not ('/' or '\\')) && !url.Any(char.IsControl)
            ? url
            : Home;
}
