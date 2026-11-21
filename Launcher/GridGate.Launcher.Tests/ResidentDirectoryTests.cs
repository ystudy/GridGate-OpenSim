using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Guests;
using GridGate.Launcher.Residents;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// The directory the Residents page reads (spec 2026-11-21-resident-profiles-design, "Who shows"): only residents who chose it,
/// built at most once a minute, dropped on a change.
/// </summary>
public sealed class ResidentDirectoryTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-resdir-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private PublicProfileChoices Choices() => new(Path.Combine(_dir, "public-profiles.json"));

    [Fact]
    public async Task Asks_only_for_those_who_chose_it()
    {
        var (on, off) = (Guid.NewGuid(), Guid.NewGuid());
        var choices = Choices();
        choices.Choose(on, true);
        var profiles = new FakePublicProfiles();
        profiles.Add(ResidentQueryTests.Profile(on, "Ann", "Shown"));
        profiles.Add(ResidentQueryTests.Profile(off, "Bob", "Hidden"));

        var data = await new ResidentDirectory(profiles, choices, new FakeGridRegions(), new FakeTimeProvider()).CurrentAsync(Ct);

        Assert.True(data.Available);
        Assert.Equal([on], profiles.LastAsked);
        Assert.Equal("Ann Shown", Assert.Single(data.Profiles).Name);
    }

    [Fact]
    public async Task Built_once_a_minute_and_again_after_Invalidate()
    {
        var profiles = new FakePublicProfiles();
        var time = new FakeTimeProvider();
        var directory = new ResidentDirectory(profiles, Choices(), new FakeGridRegions(), time);

        await directory.CurrentAsync(Ct);
        await directory.CurrentAsync(Ct);
        Assert.Equal(1, profiles.Calls);

        directory.Invalidate();
        await directory.CurrentAsync(Ct);
        Assert.Equal(2, profiles.Calls);

        time.Advance(ResidentDirectory.Lifetime + TimeSpan.FromSeconds(1));
        await directory.CurrentAsync(Ct);
        Assert.Equal(3, profiles.Calls);
    }

    [Fact]
    public async Task Unavailable_when_profiles_unavailable_but_not_when_only_regions_are()
    {
        var id = Guid.NewGuid();
        var choices = Choices();
        choices.Choose(id, true);
        var profiles = new FakePublicProfiles();
        profiles.Add(ResidentQueryTests.Profile(id, "Ann", "Shown"));

        var regionsDown = await new ResidentDirectory(profiles, choices, new FakeGridRegions { Result = GridRegionList.Unavailable },
            new FakeTimeProvider()).CurrentAsync(Ct);
        Assert.True(regionsDown.Available);
        Assert.Single(regionsDown.Profiles);
        Assert.Empty(regionsDown.Regions);

        profiles.Down = true;
        var down = await new ResidentDirectory(profiles, choices, new FakeGridRegions(), new FakeTimeProvider()).CurrentAsync(Ct);
        Assert.False(down.Available);
        Assert.Empty(down.Profiles);
    }

    [Fact]
    public async Task Regions_are_own_ones_by_name()
    {
        var bay = new GridRegion(Guid.NewGuid(), "Sunny Bay", 1000, 1000);
        var link = new GridRegion(Guid.NewGuid(), "Far Away", 2000, 2000, IsLink: true);
        var data = await new ResidentDirectory(new FakePublicProfiles(), Choices(),
            new FakeGridRegions { Result = new([bay, link], true) }, new FakeTimeProvider()).CurrentAsync(Ct);

        Assert.Same(bay, data.Regions["sunny bay"]);
        Assert.False(data.Regions.ContainsKey("Far Away"));
    }
}

/// <summary>The residents' profiles, in memory: <see cref="Add"/> puts one in; <see cref="ReadAsync"/> answers only for those asked.</summary>
internal sealed class FakePublicProfiles : IPublicProfiles
{
    private readonly List<PublicProfile> _all = [];

    public bool Down { get; set; }
    public int Calls { get; private set; }
    public IReadOnlyCollection<Guid> LastAsked { get; private set; } = [];

    public void Add(PublicProfile p)
    {
        lock (_all) _all.Add(p);
    }

    public Task<PublicProfileList> ReadAsync(IReadOnlyCollection<Guid> principals, CancellationToken ct)
    {
        Calls++;
        LastAsked = [.. principals];
        if (Down) return Task.FromResult(PublicProfileList.Unavailable);
        lock (_all)
            return Task.FromResult(new PublicProfileList([.. _all.Where(p => principals.Contains(p.Id))], true));
    }
}
