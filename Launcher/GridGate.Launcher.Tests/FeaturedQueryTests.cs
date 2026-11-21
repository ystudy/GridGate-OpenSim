using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Featured;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// What the Featured page shows from the directory (spec 2026-11-21-featured-places-design): the rating gate for visitors,
/// search, category, sort, paging and the stats, with odd query values made harmless.
/// </summary>
public sealed class FeaturedQueryTests
{
    internal static readonly GridRegion Bay = new(Guid.Parse("0f000000-0000-0000-0000-0000000000b1"), "Sunny Bay", 1000, 1000);
    internal static readonly GridRegion Club = new(Guid.Parse("0f000000-0000-0000-0000-0000000000c2"), "Night Club", 1001, 1000);

    internal static ListedParcel Parcel(int n, GridRegion region, string name, int category = 11, int rating = 0, int visits = 0,
        string description = "") =>
        new(Guid.Parse($"0e000000-0000-0000-0000-{n:D12}"), region.Id, name, description, category, rating, visits, false, 0, 0, 0);

    private static FeaturedData Data(params (ListedParcel Parcel, GridRegion Region)[] places) =>
        new([.. places.Select(p => new FeaturedPlace(p.Parcel, p.Region))], true);

    private static FeaturedView View(FeaturedData data, string? q = null, string? category = null, string? rating = null,
        string? sort = null, string? page = null, bool signedIn = false) =>
        FeaturedQuery.View(data, FeaturedFilter.Parse(q, category, rating, sort, page), signedIn);

    [Fact]
    public void Strangers_never_see_adult_places()
    {
        var data = Data(
            (Parcel(1, Bay, "Beach shop", visits: 10), Bay),
            (Parcel(2, Club, "Red room", category: 2, rating: 2, visits: 50), Club));

        var visitor = View(data);
        var resident = View(data, signedIn: true);

        Assert.Equal(["Beach shop"], visitor.Page.Select(p => p.Parcel.Name));
        Assert.Equal(new FeaturedStats(1, 10, 1, 1), visitor.Stats);
        Assert.DoesNotContain(visitor.Categories, c => c.Category == 2);
        Assert.False(visitor.CanPickRating);
        Assert.Equal(["Red room", "Beach shop"], resident.Page.Select(p => p.Parcel.Name));
        Assert.Equal(new FeaturedStats(2, 60, 2, 2), resident.Stats);
        Assert.True(resident.CanPickRating);
        Assert.Equal(["Red room"], View(data, rating: "2", signedIn: true).Page.Select(p => p.Parcel.Name));
        Assert.Equal(["Beach shop"], View(data, rating: "2").Page.Select(p => p.Parcel.Name)); // ignored for visitors
    }

    [Theory]
    [InlineData(0, 0, 0u, 0)]
    [InlineData(0, 2, 0u, 2)]
    [InlineData(0, 11, 0x40000u, 1)]
    [InlineData(1, 11, 0u, 1)]
    [InlineData(2, 11, 0u, 2)]
    [InlineData(null, 11, 0u, 2)] // a region whose rating can't be read is treated as Adult: never shown to strangers
    [InlineData(1, 2, 0x40000u, 2)]
    public void Rating_comes_from_region_category_and_mature_flag(int? maturity, int category, uint flags, int rating) =>
        Assert.Equal(rating, ListedParcels.RatingOf(maturity, category, flags));

    [Fact]
    public void Search_matches_name_description_and_region_ignoring_case()
    {
        var data = Data(
            (Parcel(1, Bay, "Beach shop"), Bay),
            (Parcel(2, Bay, "Quiet garden", description: "Fresh FLOWERS"), Bay),
            (Parcel(3, Club, "Dance floor"), Club));

        Assert.Equal(["Beach shop"], View(data, q: "BEACH").Page.Select(p => p.Parcel.Name));
        Assert.Equal(["Quiet garden"], View(data, q: "flowers").Page.Select(p => p.Parcel.Name));
        Assert.Equal(["Dance floor"], View(data, q: "night").Page.Select(p => p.Parcel.Name));
        Assert.Empty(View(data, q: "nothing here").Page);
        Assert.Equal(3, View(data, q: "nothing here").Stats.Places); // the stats describe the whole directory
    }

    [Fact]
    public void Category_filter_and_counts()
    {
        var data = Data(
            (Parcel(1, Bay, "Beach shop", category: 11), Bay),
            (Parcel(2, Bay, "Surf shop", category: 11), Bay),
            (Parcel(3, Club, "Dance floor", category: 7), Club),
            (Parcel(4, Club, "Back room", category: 0), Club));

        var view = View(data, category: "11");

        Assert.Equal(["Beach shop", "Surf shop"], view.Page.Select(p => p.Parcel.Name).Order());
        Assert.Equal(11, view.Filter.Category);
        Assert.Equal([(7, 1), (11, 2)], view.Categories.Select(c => (c.Category, c.Count)));
        Assert.Equal(2, view.Stats.Categories); // None isn't a category
    }

    [Fact]
    public void Sort_by_visits_then_name_and_by_name()
    {
        var data = Data(
            (Parcel(1, Bay, "Beta", visits: 5), Bay),
            (Parcel(2, Bay, "alpha", visits: 5), Bay),
            (Parcel(3, Club, "Gamma", visits: 9), Club),
            (Parcel(4, Club, "Delta"), Club));

        Assert.Equal(["Gamma", "alpha", "Beta", "Delta"], View(data).Page.Select(p => p.Parcel.Name));
        Assert.Equal(["alpha", "Beta", "Delta", "Gamma"], View(data, sort: "name").Page.Select(p => p.Parcel.Name));
    }

    [Fact]
    public void Paging_24_and_clamped()
    {
        var data = Data([.. Enumerable.Range(1, 50).Select(i => (Parcel(i, Bay, $"Place {i:D2}"), Bay))]);

        var first = View(data, sort: "name");
        var last = View(data, sort: "name", page: "99");

        Assert.Equal(24, first.Page.Count);
        Assert.Equal((1, 3), (first.PageNumber, first.PageCount));
        Assert.Equal(["Place 49", "Place 50"], last.Page.Select(p => p.Parcel.Name));
        Assert.Equal(3, last.PageNumber);
        Assert.Equal(1, View(data, page: "-3").PageNumber);
        Assert.Equal(1, View(data, page: "abc").PageNumber);
    }

    [Fact]
    public void Odd_filter_values_are_ignored()
    {
        var data = Data((Parcel(1, Bay, "Beach shop"), Bay));

        var view = View(data, q: new string('x', 5000), category: "99", rating: "7", sort: "sideways", signedIn: true);

        Assert.Null(view.Filter.Category);
        Assert.Null(view.Filter.Rating);
        Assert.Equal(FeaturedFilter.SortVisits, view.Filter.Sort);
        Assert.Equal(FeaturedFilter.MaxSearch, view.Filter.Q!.Length);
        Assert.Empty(view.Page);
        Assert.Single(View(data, q: "   ").Page); // blank search shows everything
    }

    [Fact]
    public void Empty_directory_has_empty_stats()
    {
        var view = View(new FeaturedData([], true));

        Assert.Empty(view.Page);
        Assert.Equal(new FeaturedStats(0, 0, 0, 0), view.Stats);
        Assert.Equal((1, 1), (view.PageNumber, view.PageCount));
    }
}
