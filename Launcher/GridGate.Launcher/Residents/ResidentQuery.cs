using System.Globalization;
using GridGate.Launcher.Core.Grid;

namespace GridGate.Launcher.Residents;

/// <summary>
/// The Residents page's choices, from its GET form, each made harmless: an unknown sort is the default, the search is trimmed
/// and cut to <see cref="MaxSearch"/> characters, and the page is at least 1.
/// </summary>
public sealed record ResidentFilter(string? Q, string Sort, int Page)
{
    public const string SortNewest = "newest";
    public const string SortName = "name";
    public const int MaxSearch = 100;

    public static ResidentFilter Parse(string? q, string? sort, string? page)
    {
        var words = q?.Trim();
        if (words is { Length: > MaxSearch }) words = words[..MaxSearch];
        return new ResidentFilter(
            string.IsNullOrEmpty(words) ? null : words,
            sort == SortName ? SortName : SortNewest,
            int.TryParse(page, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 1 ? p : 1);
    }
}

/// <summary>What one visitor's page shows.</summary>
/// <param name="Shown">How many residents are on the page in all (the stats row), not just the ones the search matched.</param>
public sealed record ResidentView(ResidentFilter Filter, IReadOnlyList<PublicProfile> Page, int PageNumber, int PageCount, int Shown,
    bool Matched);

/// <summary>Turns the directory into one visitor's page (spec 2026-11-21-resident-profiles-design, "The directory page").</summary>
public static class ResidentQuery
{
    public const int PageSize = 24;

    /// <summary>How much of About a card shows.</summary>
    public const int CardAbout = 140;

    public static ResidentView View(ResidentData data, ResidentFilter filter)
    {
        IEnumerable<PublicProfile> matched = data.Profiles;
        if (filter.Q is { } q) matched = matched.Where(p => Has(p.Name, q) || Has(p.About, q));
        var byName = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
        var sorted = (filter.Sort == ResidentFilter.SortName
            ? matched.OrderBy(p => p.FirstName, byName).ThenBy(p => p.LastName, byName)
            : matched.OrderByDescending(p => p.Created ?? DateTimeOffset.MinValue).ThenBy(p => p.Name, byName))
            .ThenBy(p => p.Id).ToList();

        var pages = Math.Max(1, (sorted.Count + PageSize - 1) / PageSize);
        var page = Math.Min(filter.Page, pages);
        return new ResidentView(filter with { Page = page }, [.. sorted.Skip((page - 1) * PageSize).Take(PageSize)], page, pages,
            data.Profiles.Count, sorted.Count > 0);
    }

    /// <summary>The shown resident with this name (case ignored), or null: the same answer for "not shown" and "no such resident".</summary>
    public static PublicProfile? Find(ResidentData data, string? first, string? last) =>
        string.IsNullOrEmpty(first) || string.IsNullOrEmpty(last)
            ? null
            : data.Profiles.FirstOrDefault(p => string.Equals(p.FirstName, first, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.LastName, last, StringComparison.OrdinalIgnoreCase));

    /// <summary>The shown resident who owns pick <paramref name="pick"/>, with the pick; null when no shown resident has it.</summary>
    public static (PublicProfile Owner, PublicPick Pick)? FindPick(ResidentData data, Guid pick)
    {
        foreach (var p in data.Profiles)
            if (p.Picks.FirstOrDefault(x => x.Id == pick) is { } found) return (p, found);
        return null;
    }

    /// <summary>A resident's own page address.</summary>
    public static string LinkOf(PublicProfile p) => LinkOf(p.FirstName, p.LastName);

    /// <inheritdoc cref="LinkOf(PublicProfile)"/>
    public static string LinkOf(string first, string last) =>
        "/residents/" + Uri.EscapeDataString(first) + "/" + Uri.EscapeDataString(last);

    /// <summary>
    /// The first <see cref="CardAbout"/> characters of About on one line, cut at a word when it can be, with "…" when cut.
    /// </summary>
    public static string Short(string about)
    {
        var line = string.Join(' ', about.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length <= CardAbout) return line;
        var cut = line.LastIndexOf(' ', CardAbout);
        // Don't split a surrogate pair at the cut.
        var end = cut > CardAbout / 2 ? cut : char.IsHighSurrogate(line[CardAbout - 1]) ? CardAbout - 1 : CardAbout;
        return line[..end].TrimEnd() + "…";
    }

    /// <summary>
    /// A web address the profile page may link to: an absolute http or https address, else null (so no <c>javascript:</c>
    /// or other scheme reaches the page). A bare "example.com" is read as https.
    /// </summary>
    public static Uri? SafeWebLink(string url)
    {
        var text = url.Trim();
        if (text.Length == 0 || text.Length > 255) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Host.Length > 0 && uri.UserInfo.Length == 0
            ? uri
            : null;
    }

    private static bool Has(string text, string words) => text.Contains(words, StringComparison.CurrentCultureIgnoreCase);
}
