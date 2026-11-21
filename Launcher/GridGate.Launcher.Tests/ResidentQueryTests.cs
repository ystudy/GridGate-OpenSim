using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Residents;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>One visitor's Residents page (spec 2026-11-21-resident-profiles-design, "The directory page").</summary>
public sealed class ResidentQueryTests
{
    internal static PublicProfile Profile(Guid id, string first, string last, string about = "", int joinedDaysAgo = 10,
        IReadOnlyList<PublicPick>? picks = null) =>
        new(id, first, last, new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero).AddDays(-joinedDaysAgo), Guid.Empty, about,
            "", "", "", "", picks ?? []);

    private static ResidentData Data(params PublicProfile[] profiles) => new(profiles, new Dictionary<string, GridRegion>(), true);

    [Fact]
    public void Filter_parses_harmlessly()
    {
        var f = ResidentFilter.Parse("  " + new string('a', 150) + " ", "sideways", "-3");
        Assert.Equal(ResidentFilter.MaxSearch, f.Q!.Length);
        Assert.Equal(ResidentFilter.SortNewest, f.Sort);
        Assert.Equal(1, f.Page);
        Assert.Null(ResidentFilter.Parse("   ", "name", "2").Q);
        Assert.Equal(ResidentFilter.SortName, ResidentFilter.Parse(null, "name", null).Sort);
        Assert.Equal(2, ResidentFilter.Parse(null, null, "2").Page);
    }

    [Fact]
    public void Newest_members_first_by_default_and_by_name_on_request()
    {
        var data = Data(
            Profile(Guid.NewGuid(), "Cara", "Old", joinedDaysAgo: 300),
            Profile(Guid.NewGuid(), "Abe", "New", joinedDaysAgo: 1),
            Profile(Guid.NewGuid(), "bea", "Middle", joinedDaysAgo: 50));

        Assert.Equal(["Abe New", "bea Middle", "Cara Old"],
            ResidentQuery.View(data, ResidentFilter.Parse(null, null, null)).Page.Select(p => p.Name));
        Assert.Equal(["Abe New", "bea Middle", "Cara Old"],
            ResidentQuery.View(data, ResidentFilter.Parse(null, "name", null)).Page.Select(p => p.Name));

        var older = Data(Profile(Guid.NewGuid(), "Zed", "First", joinedDaysAgo: 1), Profile(Guid.NewGuid(), "Amy", "Later", joinedDaysAgo: 9));
        Assert.Equal(["Zed First", "Amy Later"], ResidentQuery.View(older, ResidentFilter.Parse(null, null, null)).Page.Select(p => p.Name));
        Assert.Equal(["Amy Later", "Zed First"], ResidentQuery.View(older, ResidentFilter.Parse(null, "name", null)).Page.Select(p => p.Name));
    }

    [Fact]
    public void Search_matches_name_or_about_and_counts_stay_whole()
    {
        var data = Data(
            Profile(Guid.NewGuid(), "Ann", "Sailor", about: "I build boats."),
            Profile(Guid.NewGuid(), "Bob", "Builder", about: "Dancer."),
            Profile(Guid.NewGuid(), "Cid", "Quiet"));

        var boats = ResidentQuery.View(data, ResidentFilter.Parse("BOATS", null, null));
        Assert.Equal(["Ann Sailor"], boats.Page.Select(p => p.Name));
        Assert.Equal(3, boats.Shown);

        Assert.Equal(["Bob Builder"], ResidentQuery.View(data, ResidentFilter.Parse("bob b", null, null)).Page.Select(p => p.Name));

        var none = ResidentQuery.View(data, ResidentFilter.Parse("nobody", null, null));
        Assert.False(none.Matched);
        Assert.Equal(3, none.Shown);
    }

    [Fact]
    public void Pages_of_24_and_a_page_past_the_end_is_the_last()
    {
        var data = Data([.. Enumerable.Range(0, 30).Select(i => Profile(Guid.NewGuid(), "R" + i.ToString("D2"), "Res", joinedDaysAgo: i))]);
        var first = ResidentQuery.View(data, ResidentFilter.Parse(null, null, null));
        Assert.Equal(ResidentQuery.PageSize, first.Page.Count);
        Assert.Equal(2, first.PageCount);

        var past = ResidentQuery.View(data, ResidentFilter.Parse(null, null, "9"));
        Assert.Equal(2, past.PageNumber);
        Assert.Equal(6, past.Page.Count);
    }

    [Fact]
    public void Find_by_name_ignores_case_and_only_finds_shown_residents()
    {
        var ann = Profile(Guid.NewGuid(), "Ann", "Sailor");
        var data = Data(ann);
        Assert.Same(ann, ResidentQuery.Find(data, "ann", "SAILOR"));
        Assert.Null(ResidentQuery.Find(data, "Bob", "Builder"));
        Assert.Null(ResidentQuery.Find(data, "Ann", null));
    }

    [Fact]
    public void FindPick_finds_only_a_shown_residents_pick()
    {
        var pick = new PublicPick(Guid.NewGuid(), "My shop", "", Guid.NewGuid(), "Sunny Bay", 256128, 256130, 22);
        var ann = Profile(Guid.NewGuid(), "Ann", "Sailor", picks: [pick]);
        var found = ResidentQuery.FindPick(Data(ann), pick.Id);
        Assert.NotNull(found);
        Assert.Same(ann, found.Value.Owner);
        Assert.Null(ResidentQuery.FindPick(Data(ann), Guid.NewGuid()));
    }

    [Fact]
    public void Short_cuts_at_a_word_on_one_line()
    {
        Assert.Equal("Hello there friend", ResidentQuery.Short("Hello\r\n  there\tfriend "));
        var longText = string.Join(' ', Enumerable.Repeat("word", 60));
        var cut = ResidentQuery.Short(longText);
        Assert.EndsWith("word…", cut);
        Assert.True(cut.Length <= ResidentQuery.CardAbout + 1);

        var noSpaces = new string('x', 139) + "😀" + "tail";
        var at = ResidentQuery.Short(noSpaces);
        Assert.Equal(new string('x', 139) + "…", at);
    }

    [Theory]
    [InlineData("https://example.com/me", "https://example.com/me")]
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("example.com/shop", "https://example.com/shop")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("data:text/html,hi", null)]
    [InlineData("ftp://example.com", null)]
    [InlineData("https://user:pw@example.com", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void SafeWebLink_allows_only_http_and_https(string url, string? expected)
    {
        Assert.Equal(expected, ResidentQuery.SafeWebLink(url)?.AbsoluteUri);
    }

    [Fact]
    public void LinkOf_escapes_the_name()
    {
        Assert.Equal("/residents/Ann/O%27Neil%20X", ResidentQuery.LinkOf(Profile(Guid.NewGuid(), "Ann", "O'Neil X")));
    }
}
