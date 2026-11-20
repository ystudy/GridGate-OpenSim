using GridGate.Launcher.Core.Front;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Pictures;
using GridGate.Launcher.Core.Splash;
using GridGate.Launcher.Endpoints;
using GridGate.Launcher.Front;
using GridGate.Launcher.Resident;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.FrontPage;

/// <summary>
/// The Front page staff editor (<c>/front-page</c>; spec 2026-11-19-front-page-design, "The staff page"): the photo, which live
/// numbers show, where the places and news come from, and the front page's own lists, kept whichever source is chosen. Plain
/// forms; every save is live at once and drops the cached live data, so the public page shows it on the next visit. AdminPolicy
/// by the folder rule; 404 where there is no guest side (<c>GuestSideOnly</c>). AuditMiddleware logs each change. A request may be
/// one picture at the picture store's cap plus room for the form around it, like <c>Pages/Guest/Upload</c>.
/// </summary>
[RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
[RequestSizeLimit(MaxRequestBytes)]
public sealed class IndexModel(FrontPageStore store, [FromKeyedServices("front")] PictureStore pictures, IGridRegions regions,
    IGridController grid, IHttpClientFactory http, FrontPageSnapshot snapshot, OfferedLanguages offered) : PageModel
{
    /// <summary>
    /// Names a news item for the editor's buttons: its date and a short hash of its text, so a button made for one item never
    /// changes another after the list changed in another window.
    /// </summary>
    public static string NewsKey(SplashNewsItem item)
    {
        var text = string.Join('\n', item.Text.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => t.Key + "=" + t.Value));
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return $"{item.Date:yyyy-MM-dd}-{Convert.ToHexStringLower(hash.AsSpan(0, 6))}";
    }

    /// <summary>The multipart framing, the token, and a place's line in every language around the picture.</summary>
    private const long FormAllowance = 64 * 1024;
    private const long MaxRequestBytes = PictureStore.MaxUploadBytes + FormAllowance;

    /// <summary>How much of a news item's text the list shows.</summary>
    public const int NewsPreviewLength = 80;

    /// <summary>The start of a news item's text (English, else its first language) for the list, cut at a word.</summary>
    public static string NewsPreview(SplashNewsItem item)
    {
        var text = string.Join(' ', (item.Text.GetValueOrDefault("en") ?? item.Text.Values.FirstOrDefault() ?? "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length <= NewsPreviewLength) return text;
        var cut = text.LastIndexOf(' ', NewsPreviewLength);
        return text[..(cut > NewsPreviewLength / 2 ? cut : NewsPreviewLength)].TrimEnd() + "…";
    }

    private const string ListChanged = "The list changed in another window, so nothing was changed. Look again and try once more.";

    public FrontPageSettings Saved { get; private set; } = FrontPageSettings.Default;

    /// <summary>
    /// The grid's own regions (never hypergrid links) not on the list yet, for the Add a place picker; empty when the list can't
    /// be read.
    /// </summary>
    public IReadOnlyList<GridRegion> OwnRegions { get; private set; } = [];

    public bool RegionsUnavailable { get; private set; }

    /// <summary>The languages residents can pick: one line box per language.</summary>
    public IReadOnlyList<string> Languages { get; private set; } = [];

    public string? Notice { get; private set; }
    public string? Problem { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Notice = TempData["FrontNotice"] as string;
        Problem = TempData["FrontProblem"] as string;
        Saved = store.Load();
        Languages = offered.Current();
        var list = await regions.ListAsync(ct);
        RegionsUnavailable = !list.Available;
        OwnRegions = [.. list.Regions.Where(r => !r.IsLink && Saved.Places.All(p => p.RegionId != r.Id))];
    }

    public async Task<IActionResult> OnPostPhotoAsync(IFormFile? file, CancellationToken ct)
    {
        if (await AddPictureAsync(file, ct) is not { } name) return Back();
        return Apply(s => s with { Photo = name }, "The photo is saved.");
    }

    public IActionResult OnPostRemovePhoto() => Apply(s => s with { Photo = null }, "The photo is removed.");

    public IActionResult OnPostNumbers(bool inWorld, bool regions, bool registered, bool active) =>
        Apply(s => s with { Numbers = new FrontNumbers(inWorld, regions, registered, active) }, "The numbers are saved.");

    public IActionResult OnPostPlacesSource(FrontPlacesSource source) =>
        Enum.IsDefined(source) ? Apply(s => s with { PlacesSource = source }, "The places' source is saved.") : BadRequest();

    public async Task<IActionResult> OnPostAddPlaceAsync(Guid regionId, CancellationToken ct)
    {
        var list = await regions.ListAsync(ct);
        if (list.Regions.FirstOrDefault(r => r.Id == regionId && !r.IsLink) is not { } region)
            return Fail("That region isn't one of this grid's regions.");
        if (store.Load().Places.Any(p => p.RegionId == region.Id)) return Fail($"{region.Name} is already on the list.");
        // Checked again under the lock: another window may have added it since (then nothing changes).
        return Apply(s => s.Places.Any(p => p.RegionId == region.Id) ? null
                : s with { Places = [.. s.Places, new SplashPlace(region.Id, region.Name, new Dictionary<string, string>(), null)] },
            $"{region.Name} is added.");
    }

    public async Task<IActionResult> OnPostSavePlaceAsync(int index, Guid regionId, [FromForm(Name = "line")] Dictionary<string, string>? line, IFormFile? file,
        bool fromMap, CancellationToken ct)
    {
        if (PlaceAt(store.Load(), index, regionId) is not { } place) return Fail(ListChanged);
        var picture = place.Picture;
        if (file is { Length: > 0 })
        {
            if (await AddPictureAsync(file, ct) is not { } uploaded) return Back();
            picture = uploaded;
        }
        else if (fromMap)
        {
            if (await MapPictureAsync(place, ct) is not { } tile) return Back();
            picture = tile;
        }
        var lines = (line ?? []).Where(l => offered.Current().Contains(l.Key))
            .Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToDictionary(l => l.Key, l => l.Value.Trim());
        return Apply(s => PlaceAt(s, index, regionId) is { } still
            ? s with { Places = Replace(s.Places, index, still with { Line = lines, Picture = picture }) }
            : null, $"{place.RegionName} is saved.");
    }

    public IActionResult OnPostMovePlace(int index, Guid regionId, int by) => Apply(s =>
    {
        var to = index + Math.Sign(by);
        if (PlaceAt(s, index, regionId) is null || to < 0 || to >= s.Places.Count) return null;
        var list = s.Places.ToList();
        (list[index], list[to]) = (list[to], list[index]);
        return s with { Places = list };
    }, "The places' order is saved.");

    public IActionResult OnPostRemovePlace(int index, Guid regionId) =>
        Apply(s => PlaceAt(s, index, regionId) is null ? null : s with { Places = [.. s.Places.Where((_, i) => i != index)] },
            "The place is removed.");

    public IActionResult OnPostNewsSource(FrontNewsSource source) =>
        Enum.IsDefined(source) ? Apply(s => s with { NewsSource = source }, "The news' source is saved.") : BadRequest();

    public IActionResult OnPostAddNews(DateOnly date, [FromForm(Name = "text")] Dictionary<string, string>? text)
    {
        var words = (text ?? []).Where(t => offered.Current().Contains(t.Key) && !string.IsNullOrWhiteSpace(t.Value))
            .ToDictionary(t => t.Key, t => t.Value.Trim());
        if (words.Count == 0) return Fail("Write the news in at least one language.");
        if (date == default) date = DateOnly.FromDateTime(DateTime.Today);
        return Apply(s => s with { News = [.. s.News, new SplashNewsItem(date, words, false)] }, "The news is added.");
    }

    public IActionResult OnPostToggleNews(int index, string? key) =>
        Apply(s => NewsAt(s, index, key) is { } item ? s with { News = Replace(s.News, index, item with { Hidden = !item.Hidden }) } : null,
            "The news is saved.");

    public IActionResult OnPostRemoveNews(int index, string? key) =>
        Apply(s => NewsAt(s, index, key) is null ? null : s with { News = [.. s.News.Where((_, i) => i != index)] }, "The news is removed.");

    /// <summary>The place at <paramref name="index"/> when it is still the one for <paramref name="regionId"/>; null otherwise.</summary>
    private static SplashPlace? PlaceAt(FrontPageSettings s, int index, Guid regionId) =>
        index >= 0 && index < s.Places.Count && s.Places[index].RegionId == regionId ? s.Places[index] : null;

    /// <summary>The news item at <paramref name="index"/> when it is still the one <paramref name="key"/> names; null otherwise.</summary>
    private static SplashNewsItem? NewsAt(FrontPageSettings s, int index, string? key) =>
        index >= 0 && index < s.News.Count && NewsKey(s.News[index]) == key ? s.News[index] : null;

    private static List<T> Replace<T>(IReadOnlyList<T> list, int index, T item)
    {
        var copy = list.ToList();
        copy[index] = item;
        return copy;
    }

    /// <summary>
    /// Saves the change, read and written under the store's lock; <paramref name="change"/> returns null when the item it was
    /// asked to change isn't where the page showed it any more. On success the cached live data is dropped, so the public page
    /// shows it at once.
    /// </summary>
    private IActionResult Apply(Func<FrontPageSettings, FrontPageSettings?> change, string done)
    {
        FrontUpdate result;
        try
        {
            result = store.Update(change);
        }
        catch (IOException e)
        {
            return Fail(e.Message);
        }
        if (result.ListChanged) return Fail(ListChanged);
        if (result.Problems.Count > 0) return Fail(string.Join(" ", result.Problems));
        snapshot.Invalidate();
        TempData["FrontNotice"] = done;
        return Back();
    }

    private RedirectResult Fail(string problem)
    {
        TempData["FrontProblem"] = problem;
        return Back();
    }

    private RedirectResult Back() => Redirect("/front-page");

    /// <summary>The uploaded picture's name in the front store; null (and the problem kept for the page) when it isn't one.</summary>
    private async Task<string?> AddPictureAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is not { Length: > 0 })
        {
            TempData["FrontProblem"] = "Choose a picture to upload.";
            return null;
        }
        await using var stream = file.OpenReadStream();
        return Keep(await pictures.AddAsync(stream, Path.GetFileName(file.FileName), User.Identity?.Name ?? "staff", ct));
    }

    /// <summary>The place's region map tile, kept as a front picture; null (with the problem kept) when there is none.</summary>
    private async Task<string?> MapPictureAsync(SplashPlace place, CancellationToken ct)
    {
        var list = await regions.ListAsync(ct);
        if (list.Regions.FirstOrDefault(r => r.Id == place.RegionId) is not { } region)
        {
            TempData["FrontProblem"] = "That region isn't registered with the grid right now, so it has no map picture.";
            return null;
        }
        var (bytes, _) = await MapTiles.FetchAsync(region, grid, http, ct);
        if (bytes.Length == 0)
        {
            TempData["FrontProblem"] = "The grid has no map picture for that region yet.";
            return null;
        }
        return Keep(await pictures.AddAsync(new MemoryStream(bytes), $"Map of {region.Name}", User.Identity?.Name ?? "staff", ct));
    }

    private string? Keep(PictureResult result)
    {
        if (result is PictureResult.Added added) return added.Picture.Name;
        TempData["FrontProblem"] = result is PictureResult.Refused refused ? refused.Message : "That picture is too large.";
        return null;
    }
}
