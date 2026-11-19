using GridGate.Launcher.Core.Pictures;
using GridGate.Launcher.Core.Splash;
using Microsoft.Extensions.Logging;

namespace GridGate.Launcher.Core.Front;

/// <summary>What <see cref="FrontPageStore.Update"/> did: saved, refused for its problems, or nothing because the list had changed.</summary>
public sealed record FrontUpdate(bool ListChanged, IReadOnlyList<string> Problems)
{
    public static readonly FrontUpdate Done = new(false, []);
    public static readonly FrontUpdate Changed = new(true, []);
    public bool Saved => !ListChanged && Problems.Count == 0;
}

/// <summary>
/// The front page's settings in <c>Data/front/front.json</c> (spec 2026-11-19-front-page-design). A save is live at once (no
/// draft). Checked against the splash editor's limits for the same fields, and against the front picture store, under one
/// lock with <see cref="RemovePictureIfUnused"/>, so a picture can't be removed between a save's check and its write.
/// </summary>
public sealed class FrontPageStore(LauncherPaths paths, ILogger<FrontPageStore>? log = null)
{
    /// <summary>Whether a picture is in the front picture store; the host sets it.</summary>
    public Func<string, bool> PictureExists { get; init; } = _ => true;

    private readonly Lock _gate = new();
    private readonly JsonStateFile<FrontPageSettings> _file = new(Path.Combine(paths.Front, "front.json"), () => FrontPageSettings.Default, log);

    /// <summary>What the file held when last read; dropped by every save. Only this store writes the file.</summary>
    private volatile FrontPageSettings? _loaded;

    /// <summary>
    /// The saved settings, or the defaults when there are none; fields a file lacks read as their defaults. Read from the file
    /// once and kept until the next save, since every public visit asks.
    /// </summary>
    public FrontPageSettings Load()
    {
        if (_loaded is { } kept) return kept;
        // Read under the save lock, so a read that began before a save can't keep the old settings after it.
        lock (_gate)
        {
            return _loaded ??= Read();
        }
    }

    private FrontPageSettings Read()
    {
        var s = _file.Load();
        // A field missing from the file comes back null from the JSON reader.
        return s with
        {
            Numbers = s.Numbers ?? new FrontNumbers(),
            Places = s.Places ?? [],
            News = s.News ?? [],
        };
    }

    /// <summary>Saves <paramref name="settings"/> when it passes its check; the problems otherwise (nothing saved).</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    public IReadOnlyList<string> Save(FrontPageSettings settings)
    {
        lock (_gate)
        {
            var problems = Check(settings);
            if (problems.Count > 0) return problems;
            // Dropped before the write, so a failed or partial write is read back from the file as it now is.
            _loaded = null;
            if (!_file.Save(settings)) throw new IOException("The front page settings could not be saved.");
            return [];
        }
    }

    /// <summary>
    /// Reads, changes and saves under one lock, so two editors can't each save over the other's change. <paramref name="change"/>
    /// returns null when what it was asked to change isn't there any more (another window changed the list): nothing is saved.
    /// </summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    public FrontUpdate Update(Func<FrontPageSettings, FrontPageSettings?> change)
    {
        lock (_gate)
        {
            if (change(Load()) is not { } next) return FrontUpdate.Changed;
            var problems = Save(next);
            return problems.Count == 0 ? FrontUpdate.Done : new FrontUpdate(false, problems);
        }
    }

    /// <summary>
    /// Deletes a picture (through <paramref name="delete"/>) only when no saved setting mentions it; checked under the save
    /// lock. True when it was deleted.
    /// </summary>
    public bool RemovePictureIfUnused(string name, Func<string, bool> delete)
    {
        lock (_gate)
        {
            return !FrontPictures.Kept(Load()).Contains(name) && delete(name);
        }
    }

    private List<string> Check(FrontPageSettings s)
    {
        var problems = new List<string>();
        if (s.Photo is { } photo) CheckPicture(photo, "The photo", problems);
        if (s.Places.Count > SplashRules.MaxPlaces) problems.Add($"At most {SplashRules.MaxPlaces} places.");
        foreach (var p in s.Places)
        {
            var who = $"The place {p.RegionName}";
            if (p.RegionName.Length is 0 or > SplashRules.MaxRegionName) problems.Add($"{who}: the region name is empty or too long.");
            CheckText(p.Line, SplashRules.MaxPlaceLine, who, problems);
            if (p.Picture is { } picture) CheckPicture(picture, who, problems);
        }
        if (s.News.Count > SplashRules.MaxNews) problems.Add($"At most {SplashRules.MaxNews} news items.");
        foreach (var n in s.News) CheckText(n.Text, SplashRules.MaxNewsText, $"The news of {n.Date:yyyy-MM-dd}", problems);
        return problems;
    }

    private void CheckPicture(string name, string who, List<string> problems)
    {
        if (!PictureStore.IsName(name)) problems.Add($"{who}: that isn't a picture name.");
        else if (!PictureExists(name)) problems.Add($"{who}: that picture is gone.");
    }

    private static void CheckText(IReadOnlyDictionary<string, string>? text, int max, string who, List<string> problems)
    {
        if (text is null) return;
        foreach (var (language, words) in text)
            if (words.Length > max) problems.Add($"{who}: the {language} text is longer than {max} characters.");
    }
}
