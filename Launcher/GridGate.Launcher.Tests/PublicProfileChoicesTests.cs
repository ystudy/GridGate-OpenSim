using GridGate.Launcher.Core.Guests;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Each resident's Residents page choice (spec 2026-11-21-resident-profiles-design, "Who shows").</summary>
public sealed class PublicProfileChoicesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-public-profiles-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "guests", "public-profiles.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Everyone_starts_off_and_unlisted()
    {
        var store = new PublicProfileChoices(File_);
        Assert.Equal(PublicProfileState.Off, store.StateOf(Guid.NewGuid()));
        Assert.Empty(store.Shown());
    }

    [Fact]
    public void Choosing_on_lists_them_and_off_unlists_them()
    {
        var id = Guid.NewGuid();
        var store = new PublicProfileChoices(File_);
        Assert.True(store.Choose(id, true));
        Assert.Equal([id], store.Shown());
        Assert.True(store.Choose(id, false));
        Assert.Equal(PublicProfileState.Off, store.StateOf(id));
        Assert.Empty(store.Shown());
    }

    [Fact]
    public void A_held_resident_cannot_turn_it_on_until_staff_release_it()
    {
        var id = Guid.NewGuid();
        var store = new PublicProfileChoices(File_);
        store.Choose(id, true);
        store.Hold(id);
        Assert.Empty(store.Shown());
        Assert.False(store.Choose(id, true));
        Assert.Equal(PublicProfileState.Held, store.StateOf(id));

        store.Release(id);
        Assert.Equal(PublicProfileState.Off, store.StateOf(id));
        Assert.True(store.Choose(id, true));
        Assert.Equal([id], store.Shown());
    }

    [Fact]
    public void Release_leaves_a_resident_who_is_not_held_alone()
    {
        var id = Guid.NewGuid();
        var store = new PublicProfileChoices(File_);
        store.Choose(id, true);
        store.Release(id);
        Assert.Equal(PublicProfileState.On, store.StateOf(id));
    }

    [Fact]
    public void Choices_survive_a_restart()
    {
        var (on, held) = (Guid.NewGuid(), Guid.NewGuid());
        var store = new PublicProfileChoices(File_);
        store.Choose(on, true);
        store.Hold(held);

        var again = new PublicProfileChoices(File_);
        Assert.Equal(PublicProfileState.On, again.StateOf(on));
        Assert.Equal(PublicProfileState.Held, again.StateOf(held));
    }

    [Fact]
    public void An_unknown_value_in_the_file_reads_as_off()
    {
        var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, $"{{\"{id}\": 7}}");
        var store = new PublicProfileChoices(File_);
        Assert.Equal(PublicProfileState.Off, store.StateOf(id));
        Assert.Empty(store.Shown());
    }
}
