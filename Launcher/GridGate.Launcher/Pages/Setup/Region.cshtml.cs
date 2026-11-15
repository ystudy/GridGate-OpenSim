using System.Globalization;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Core.Setup;
using GridGate.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.Setup;

/// <summary>
/// Step 3: the region's name, content and map square (prefilled with the one the region would take; blank lets the
/// controller place it). A square off the map or taken is refused at its field with the Add page's words. Posting starts
/// the region in the background (one at a time) and shows its progress at /setup/region?working=1; a failure shows the
/// message and a log excerpt, and the form again to retry.
/// </summary>
public sealed class RegionModel(Func<LauncherSettings> settings, LauncherPaths paths, SetupProgress progress, SetupRunner runner, IGridController grid) : PageModel
{
    private const string WorkingUrl = "/setup/region?working=1";

    [BindProperty] public string? RegionName { get; set; }
    [BindProperty(Name = "Content")] public string? RegionContent { get; set; }
    [BindProperty] public string? X { get; set; }
    [BindProperty] public string? Y { get; set; }

    public bool StarterAvailable { get; private set; }
    public SetupProgressSnapshot Progress { get; private set; } = new("", 0, false, null, null);
    /// <summary>True to show the progress view (running, failed or ready) instead of only the form.</summary>
    public bool ShowProgress { get; private set; }
    /// <summary>True when the region exists (also after a restart, when the progress is empty) or this run just made it.</summary>
    public bool Ready => !Progress.Running && (RegionCreated || (ShowProgress && Progress.Error is null && Progress.Stage == SetupProgress.Ready));
    private bool RegionCreated { get; set; }
    /// <summary>True for a Grid Asset Server, whose owner may leave this computer without regions.</summary>
    public bool CanSkip { get; private set; }
    public bool ShowForm => !Progress.Running && !Ready;

    public async Task<IActionResult> OnGetAsync(string? working, CancellationToken ct)
    {
        if (Closed() is { } closed) return closed;
        Load();
        ShowProgress = working is not null || Progress.Running;
        // What an earlier attempt saved (its name and square), else the defaults.
        var earlier = Core.Settings.RegionModel.First(settings());
        RegionName = earlier?.Name ?? "Welcome";
        RegionContent = StarterAvailable ? "starter" : "island";
        if (ShowForm)
        {
            var spot = earlier is not null ? (earlier.X, earlier.Y) : await FreeSpotAsync(RegionName, ct);
            (X, Y) = (spot?.X.ToString(CultureInfo.InvariantCulture), spot?.Y.ToString(CultureInfo.InvariantCulture));
        }
        return Page();
    }

    /// <summary>The square the region would take; null (the fields stay blank) when the grid doesn't answer or has none near 1000,1000.</summary>
    private async Task<(int X, int Y)?> FreeSpotAsync(string name, CancellationToken ct)
    {
        try { return await grid.FirstRegionSpotAsync(name, ct); }
        catch (GridUnreachableException) { return null; }
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (Closed() is { } closed) return closed;
        if (progress.Running) return Redirect(WorkingUrl);
        Load();
        if (RegionCreated) return Redirect("/setup/region"); // shows "ready"; a second region is not set up here

        if (string.IsNullOrEmpty(RegionName))
            ModelState.AddModelError(nameof(RegionName), "Give your region a name.");
        else if (!GridController.IsValidRegionName(RegionName)) // the controller's own rule
            ModelState.AddModelError(nameof(RegionName),
                "Use 2 to 32 letters, digits, spaces, hyphens (-), underscores (_) or apostrophes ('), with no space at the start or end.");
        if (!(RegionContent == "island" || (RegionContent == "starter" && StarterAvailable)))
            ModelState.AddModelError(nameof(RegionContent), "Pick what your region should look like.");
        if (!ModelState.IsValid) return Page();

        // Both fields blank: the controller places the region, as before there were fields.
        (int X, int Y)? at = null;
        if (!string.IsNullOrWhiteSpace(X) || !string.IsNullOrWhiteSpace(Y))
        {
            if (!RegionPlacement.TryParse(X, out var x) || !RegionPlacement.TryParse(Y, out var y))
                ModelState.AddModelError(nameof(X), RegionPlacement.OffMap);
            else if (await SpotErrorAsync(RegionName!, x, y, ct) is { } error)
                ModelState.AddModelError(nameof(X), error);
            else
                at = (x, y);
            if (!ModelState.IsValid) return Page();
        }

        runner.TryStartRegion(RegionName!, RegionContent!, at); // false: one is already running, whose progress is shown
        return Redirect(WorkingUrl);
    }

    /// <summary>
    /// The Add page's refusal for the square (off the map, or taken here or on the joined grid's map), or null. A grid that
    /// doesn't answer lets the run start: it reaches the grid first, says so, and the controller checks the square again.
    /// </summary>
    private async Task<string?> SpotErrorAsync(string name, int x, int y, CancellationToken ct)
    {
        try { return await grid.FirstRegionSpotErrorAsync(name, x, y, ct); }
        catch (GridUnreachableException) { return null; }
    }

    /// <summary>A Grid Asset Server may have no regions of its own; other plans always need one.</summary>
    public IActionResult OnPostSkip()
    {
        if (Closed() is { } closed) return closed;
        var s = settings();
        if (s.Plan != OwnerPlan.GridAsset || progress.Running) return Redirect("/setup/region");
        if (!s.Setup.RegionCreated)
            grid.UpdateSettings(x => x with { Setup = x.Setup with { RegionSkipped = true } }, writeConfig: false);
        return Redirect("/setup/connect");
    }

    private IActionResult? Closed() => SetupSteps.CanOpen(settings(), "/setup/region") ? null : Redirect("/setup");

    private void Load()
    {
        StarterAvailable = StarterContent.StarterOar(paths) is not null;
        Progress = progress.Snapshot();
        RegionCreated = settings().Setup.RegionCreated;
        CanSkip = settings().Plan == OwnerPlan.GridAsset;
    }
}
