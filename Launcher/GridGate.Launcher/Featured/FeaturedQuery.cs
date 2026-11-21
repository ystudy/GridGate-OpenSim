using System.Globalization;

namespace GridGate.Launcher.Featured;

/// <summary>
/// The Featured page's choices, from its GET form, each made harmless: unknown or out-of-range values are dropped, the
/// search is trimmed and cut to <see cref="MaxSearch"/> characters, and the page is at least 1.
/// </summary>
public sealed record FeaturedFilter(string? Q, int? Category, int? Rating, string Sort, int Page)
{
    public const string SortVisits = "visits";
    public const string SortName = "name";
    public const int MaxSearch = 100;

    public static FeaturedFilter Parse(string? q, string? category, string? rating, string? sort, string? page)
    {
        var words = q?.Trim();
        if (words is { Length: > MaxSearch }) words = words[..MaxSearch];
        return new FeaturedFilter(
            string.IsNullOrEmpty(words) ? null : words,
            Number(category) is int c and >= 1 and <= Core.Grid.ListedParcels.LastCategory ? c : null,
            Number(rating) is int r and >= 0 and <= 2 ? r : null,
            sort == SortName ? SortName : SortVisits,
            Number(page) is int p and > 1 ? p : 1);
    }

    private static int? Number(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}

/// <summary>The stats row: places, their recent visits, categories in use (not "none") and regions with places.</summary>
public sealed record FeaturedStats(int Places, long Visits, int Categories, int Regions);

/// <summary>A category the visible places use, with how many.</summary>
public sealed record FeaturedCategory(int Category, int Count);

/// <summary>What one visitor's page shows.</summary>
/// <param name="CanPickRating">Signed-in residents and staff may see every rating and pick one; visitors see General and Moderate.</param>
public sealed record FeaturedView(FeaturedFilter Filter, IReadOnlyList<FeaturedPlace> Page, int PageNumber, int PageCount,
    FeaturedStats Stats, IReadOnlyList<FeaturedCategory> Categories, bool CanPickRating, bool Matched);

/// <summary>
/// Turns the directory into one visitor's page (spec 2026-11-21-featured-places-design): Adult places only for those signed
/// in (cards, stats, category counts and so pictures too), then search, category, rating, sort and 24 a page. The stats and
/// category counts describe every place the visitor may see, not just the ones the search matched.
/// </summary>
public static class FeaturedQuery
{
    public const int PageSize = 24;

    /// <summary>The places a visitor (<paramref name="signedIn"/> false) or a resident may see at all.</summary>
    public static IEnumerable<FeaturedPlace> Visible(FeaturedData data, bool signedIn) =>
        data.Places.Where(p => signedIn || p.Parcel.Rating < 2);

    public static FeaturedView View(FeaturedData data, FeaturedFilter filter, bool signedIn)
    {
        if (!signedIn) filter = filter with { Rating = null };
        var visible = Visible(data, signedIn).ToList();

        var stats = new FeaturedStats(visible.Count, visible.Sum(p => (long)p.Parcel.Visits),
            visible.Select(p => p.Parcel.Category).Where(c => c != 0).Distinct().Count(),
            visible.Select(p => p.Region.Id).Distinct().Count());
        var categories = visible.Where(p => p.Parcel.Category != 0).GroupBy(p => p.Parcel.Category).OrderBy(g => g.Key)
            .Select(g => new FeaturedCategory(g.Key, g.Count())).ToList();

        IEnumerable<FeaturedPlace> matched = visible;
        if (filter.Q is { } q)
            matched = matched.Where(p => Has(p.Parcel.Name, q) || Has(p.Parcel.Description, q) || Has(p.Region.Name, q));
        if (filter.Category is { } category) matched = matched.Where(p => p.Parcel.Category == category);
        if (filter.Rating is { } rating) matched = matched.Where(p => p.Parcel.Rating == rating);
        var byName = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
        var sorted = (filter.Sort == FeaturedFilter.SortName
            ? matched.OrderBy(p => p.Parcel.Name, byName)
            : matched.OrderByDescending(p => p.Parcel.Visits).ThenBy(p => p.Parcel.Name, byName))
            .ThenBy(p => p.Region.Name, byName).ThenBy(p => p.Parcel.Id).ToList();

        var pages = Math.Max(1, (sorted.Count + PageSize - 1) / PageSize);
        var page = Math.Min(filter.Page, pages);
        return new FeaturedView(filter with { Page = page }, [.. sorted.Skip((page - 1) * PageSize).Take(PageSize)], page, pages,
            stats, categories, signedIn, sorted.Count > 0);
    }

    private static bool Has(string text, string words) => text.Contains(words, StringComparison.CurrentCultureIgnoreCase);
}
