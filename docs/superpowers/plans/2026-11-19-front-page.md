# Public Front Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn `/home` into a shop-window front page (photo, live numbers, featured places, news, call to action), fed by a new "Front page" staff editor that has a source choice per section.

**Architecture:**
- **Settings:** the front page's own settings live in `Data/front/front.json` (`FrontPageStore`) and its pictures in a third keyed `PictureStore` (`"front"`).
- **Live data:** numbers and the automatic places list come from a once-a-minute `FrontPageSnapshot` over the existing `IOnlineUsers` and `IGridRegions`, plus a new `IResidentCounter`.
- **Pages:** the public page resolves each section from its chosen source (splash published design, own list, or automatic) at request time. A plain-form staff page edits the settings.

**Tech Stack:** ASP.NET Core Razor Pages (.NET 10), Bootstrap 5 with the shared public look (`guest.css`), MySqlConnector, xUnit v3 with AngleSharp page tests (`LauncherFactory`).

**Spec:** `docs/superpowers/specs/2026-11-19-front-page-design.md`

## Global Constraints

- Every visitor-facing word goes through `ResidentText`, in all six built-in languages (en, de, es, fr, pt, ru); `ResidentTextScanTests` must pass. Staff page words stay in English.
- Built on the shared look (`guest.css` variables and classes). It works right-to-left (logical CSS properties only) and at 375 px wide; every word meets WCAG AA, including over the photo.
- An empty section or an unknown (null) number is hidden; never show "0" for missing data, an empty heading or a broken image.
- Public traffic causes at most one stats build a minute (`Lifetime = 60 s`), and every database query keeps a short timeout (3 s, as `GridRegions` does).
- Hypergrid links (Robust `regions.flags & 512`) never appear in the front page's numbers, automatic places or map tiles.
- Front pictures are **served** only while the page shows them, and **kept** (not cleaned up) while any saved setting mentions them. The splash and front cleanups never touch each other's store.
- `/` still shows the front page to everyone who isn't staff. Region Server and Connected plans still return 404 for `/home`, `/front-page` and the `/front/*` endpoints.
- The staff page has the same access as the splash editor (admin role, `GuestSideOnly`). Changes are live on save; there is no draft.
- Copy the reference site's style only: never its name, photos or words.
- Test command: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release`. One class: append `-- --filter-class "*Name"`. Integration (MariaDB): `dotnet test --project Launcher/GridGate.Launcher.IntegrationTests -c Release -- --filter-class "*GridRegionsTests"`.

## Review Focus

1. **Own list saved, source switched to Splash or Automatic.** The own pictures stop being served (404) but survive the cleanup; switching back shows them again. Tests in Tasks 2 and 4.
2. **No splash ever published** (a built-in splash design), or a published design with no Places or News blocks. "Same as the splash page" shows nothing and the section is hidden. Test in Task 5.
3. **The database is down.** The numbers row and the automatic places are hidden, and the page still answers 200 with the photo, buttons and closing band. Test in Task 5.
4. **A visitor asks `/front/map/{id}.jpg` for a hypergrid link, a region not in the busiest six, or any region while the source isn't Automatic.** The answer is 404 with no fetch from Robust. Test in Task 4.
5. **A visitor's language has no line for a place or news item.** The English text shows; with no English either, the line is left out (the news item is skipped). Test in Task 5.

---

## File Structure

| File | Responsibility |
|---|---|
| `Launcher/GridGate.Launcher.Core/Grid/GridRegions.cs` (modify) | `GridRegion.IsLink` from the `flags` column |
| `Launcher/GridGate.Launcher.Core/Dashboard/ResidentCounter.cs` (create) | Registered and active-in-30-days counts |
| `Launcher/GridGate.Launcher.Core/LauncherPaths.cs` (modify) | `Front`, `FrontImages` |
| `Launcher/GridGate.Launcher.Core/Front/FrontPageSettings.cs` (create) | The settings records and enums |
| `Launcher/GridGate.Launcher.Core/Front/FrontPageStore.cs` (create) | Load and save `front.json`, checks limits |
| `Launcher/GridGate.Launcher.Core/Front/FrontPictures.cs` (create) | `Shown` and `Kept` picture sets |
| `Launcher/GridGate.Launcher/Front/FrontPictureCleanup.cs` (create) | Hourly cleanup of unused front pictures |
| `Launcher/GridGate.Launcher/Front/FrontPageSnapshot.cs` (create) | The once-a-minute numbers and busiest regions |
| `Launcher/GridGate.Launcher/Front/FrontPageView.cs` (create) | Resolves sections for the public page |
| `Launcher/GridGate.Launcher/Endpoints/MapTiles.cs` (create) | Fetching a region's map tile from Robust (moved out of `SplashApi`) |
| `Launcher/GridGate.Launcher/Endpoints/FrontApi.cs` (create) | `/front/images/{name}` and `/front/map/{id}.jpg` |
| `Launcher/GridGate.Launcher/Pages/Home.cshtml(.cs)` (modify) | The new public page |
| `Launcher/GridGate.Launcher/Pages/FrontPage/Index.cshtml(.cs)` (create) | The staff editor |
| `Launcher/GridGate.Launcher/wwwroot/css/guest.css` (modify) | Front page sections |
| `Launcher/GridGate.Launcher/Program.cs`, `Navigation/StaffMenu.cs`, `Pages/GuestPages/Index.cshtml` (modify) | Wiring, menu, hub |
| `Launcher/GridGate.Launcher/Resources/ResidentText*.resx` (modify) | New and changed words |

---

### Task 1: Data sources (hypergrid flag, resident counts)

**Files:**
- Modify: `Launcher/GridGate.Launcher.Core/Grid/GridRegions.cs`
- Create: `Launcher/GridGate.Launcher.Core/Dashboard/ResidentCounter.cs`
- Modify: `Launcher/GridGate.Launcher/Program.cs` (register `IResidentCounter`)
- Modify: `Launcher/GridGate.Launcher.Tests/FakeGridController.cs`, `LauncherFactory.cs` (fake and swap)
- Test: `Launcher/GridGate.Launcher.IntegrationTests/GridRegionsTests.cs`, `Launcher/GridGate.Launcher.IntegrationTests/ResidentCounterTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record GridRegion(Guid Id, string Name, int X, int Y, bool IsLink = false)`: the query becomes `SELECT uuid, regionName, locX, locY, flags FROM regions ORDER BY regionName`, and `IsLink = (flags & 512) != 0`. `ListAsync` still returns links, so the splash count is unchanged.
  - `public sealed record ResidentCounts(long? Registered, long? ActiveSince)`, with `Unknown = new(null, null)`.
  - `public interface IResidentCounter { Task<ResidentCounts> CountAsync(DateTimeOffset activeSince, CancellationToken ct); }`
  - `public sealed class ResidentCounter(Func<LauncherSettings> settings, ILogger<ResidentCounter>? log = null) : IResidentCounter`:
    - Registered: `SELECT COUNT(*) FROM UserAccounts u WHERE {WorldCounter.Person}`.
    - Active: `SUM(CAST(g.Login AS UNSIGNED) >= @since)` over `GridUser g JOIN UserAccounts u`, as `WorldCounter`'s signed-in-this-month query, with `@since` = Unix seconds of `activeSince`.
    - Robust database, 3 s timeout. Each count is null on failure or when `!AdminRule.HasStaffPages(settings())`.
  - Test double `FakeResidentCounter : IResidentCounter` with a settable `ResidentCounts Counts`. It is exposed as `LauncherFactory.Residents` and swapped in like `Counts` (the world counter).

- [ ] **Step 1: Write the failing integration tests.**
  - In `GridRegionsTests`: `Hypergrid_links_are_marked`. Insert one region row with `flags = 512` and one with `flags = 0`, then assert that the first has `IsLink == true`, the second `false`, and both are listed.
  - In a new `ResidentCounterTests`, using the same rig as `WorldCounter`'s integration tests (search `WorldCounter` in `GridGate.Launcher.IntegrationTests`):
    - `Counts_registered_and_active_since`: three residents with `GridUser.Login` 5 days ago, 40 days ago and never. Assert `Registered == 3` and `ActiveSince(now - 30 days) == 1`.
    - `Starter_models_and_the_services_account_are_not_counted`: assert that neither is counted in either number.
- [ ] **Step 2: Run them and see them fail** (compile error or assertion). Command: the integration command above with `--filter-class "*GridRegionsTests" --filter-class "*ResidentCounterTests"`.
- [ ] **Step 3: Implement** the record change, the query, `ResidentCounter`, its registration in `Program.cs` next to `IWorldCounter`, and the test double.
- [ ] **Step 4: Run** the same command and expect a pass. Then run the unit suite: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release`, all pass.
- [ ] **Step 5: Commit** `feat(front-page): mark hypergrid links; count registered and recently active residents`.

### Task 2: Front page settings, store, pictures and cleanup

**Files:**
- Modify: `Launcher/GridGate.Launcher.Core/LauncherPaths.cs`: `Front => Path.Combine(Data, "front")`, `FrontImages => Path.Combine(Front, "images")`.
- Create: `Core/Front/FrontPageSettings.cs`, `Core/Front/FrontPageStore.cs`, `Core/Front/FrontPictures.cs`, `GridGate.Launcher/Front/FrontPictureCleanup.cs`
- Modify: `Program.cs`: keyed `"front"` `PictureStore` over `paths.FrontImages`, the `FrontPageStore` singleton, the hosted cleanup, and `LauncherPaths.CreateOwnerOnly(paths.Front)`.
- Test: `Launcher/GridGate.Launcher.Tests/FrontPageStoreTests.cs`, `FrontPictureCleanupTests.cs`

**Interfaces:**
- Produces, in namespace `GridGate.Launcher.Core.Front`:
  - `[JsonConverter(typeof(JsonStringEnumConverter<FrontPlacesSource>))] public enum FrontPlacesSource { Splash, Own, Automatic }`
  - `[JsonConverter(...)] public enum FrontNewsSource { Splash, Own }`
  - `public sealed record FrontNumbers(bool InWorld = true, bool Regions = true, bool Registered = true, bool Active = true)`
  - `public sealed record FrontPageSettings(string? Photo, FrontNumbers Numbers, FrontPlacesSource PlacesSource, IReadOnlyList<SplashPlace> Places, FrontNewsSource NewsSource, IReadOnlyList<SplashNewsItem> News)`, with `static FrontPageSettings Default` = `(null, new(), Splash, [], Splash, [])`. It reuses `SplashPlace` and `SplashNewsItem`.
  - `public sealed class FrontPageStore(LauncherPaths paths, ILogger<FrontPageStore>? log = null)`:
    - `FrontPageSettings Load()` returns `Default` when the file is missing or unreadable.
    - `IReadOnlyList<string> Save(FrontPageSettings s)` returns problems; when the list is empty the file is saved. It throws `IOException` when the write fails.
    - Limits: at most `SplashRules.MaxPlaces` own places, at most `SplashRules.MaxNews` own news items, a place line ≤ `SplashRules.MaxPlaceLine`, news text ≤ `SplashRules.MaxNewsText`, picture names must pass `PictureStore.IsName`.
    - Storage is a `JsonStateFile<FrontPageSettings>` at `Path.Combine(paths.Front, "front.json")`, like `ThemeStore`.
  - `public static class FrontPictures`:
    - `IReadOnlySet<string> Shown(FrontPageSettings s)`: the photo, plus own place pictures when `PlacesSource == Own`.
    - `IReadOnlySet<string> Kept(FrontPageSettings s)`: the photo, plus every own place picture.
  - `public sealed class FrontPictureCleanup([FromKeyedServices("front")] PictureStore pictures, FrontPageStore store, AuditLog audit, TimeProvider time, ILogger<FrontPictureCleanup> log) : BackgroundService`: the same timing and `RunOnceAsync(CancellationToken)` shape as `SplashPictureCleanup`, using `FrontPictures.Kept` and the audit action `"front-image-removed"`.

- [ ] **Step 1: Write the failing tests.**
  - `FrontPageStoreTests`:
    - `Missing_file_loads_the_defaults`: `Load() == Default` by value (compare the fields).
    - `Saves_and_loads_every_field`: a full round trip.
    - `Refuses_more_places_than_the_limit`: 9 places give one problem, and nothing is saved.
    - `Refuses_a_bad_picture_name`.
  - `FrontPictures` tests, in the same file:
    - `Own_pictures_are_shown_only_while_the_source_is_own`: `Shown` contains the place picture for `Own` and not for `Splash` or `Automatic`; `Kept` contains it for all three.
    - `Photo_is_always_shown_and_kept`.
  - `FrontPictureCleanupTests`, following the existing `SplashPictureCleanup` tests (search them):
    - `Keeps_own_list_pictures_while_the_source_is_splash`: after 31 days of `RunOnceAsync` with a fake clock, the picture still exists.
    - `Removes_a_picture_no_setting_mentions_after_30_days`.
    - `Never_touches_splash_pictures`: a splash-store picture survives the front cleanup, and a front picture survives `SplashPictureCleanup`.
- [ ] **Step 2: Run them and see them fail.** Command: `... -- --filter-class "*FrontPageStoreTests" --filter-class "*FrontPictureCleanupTests"`. Expected: a compile failure (the types are missing).
- [ ] **Step 3: Implement** the files and the `Program.cs` wiring.
- [ ] **Step 4: Run** the same command and expect a pass; then the whole unit suite passes.
- [ ] **Step 5: Commit** `feat(front-page): front page settings, store, pictures and cleanup`.

### Task 3: Live numbers and busiest regions snapshot

**Files:**
- Create: `Launcher/GridGate.Launcher/Front/FrontPageSnapshot.cs`
- Modify: `Program.cs` (singleton)
- Test: `Launcher/GridGate.Launcher.Tests/FrontPageSnapshotTests.cs`

**Interfaces:**
- Consumes: `IOnlineUsers`, `IGridRegions` (`GridRegion.IsLink`) and `IResidentCounter` from Task 1.
- Produces:
  - `public sealed record FrontAutoPlace(Guid RegionId, string Name, int X, int Y, int People)`
  - `public sealed record FrontLive(int? InWorld, int? RegionsOnline, long? Registered, long? ActiveThisMonth, IReadOnlyList<FrontAutoPlace> Busiest)`
  - `public sealed class FrontPageSnapshot(IOnlineUsers online, IGridRegions regions, IResidentCounter residents, TimeProvider time)`, with:
    - `static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60)`
    - `Task<FrontLive> CurrentAsync(CancellationToken ct)`: concurrent callers share one build, the same pattern as `SplashSnapshot.CurrentAsync`.
    - `void Invalidate()`.
  - What it computes:
    - `InWorld` is the online count (null when unavailable).
    - `RegionsOnline` is the count of regions with `!IsLink` (null when the list is unavailable).
    - `Registered` and `ActiveThisMonth` come from `CountAsync(now - 30 days)`.
    - `Busiest` lists the non-link regions ordered by people in them (from `OnlineAvatar.RegionId`) descending, then by name (`StringComparer.OrdinalIgnoreCase`), taking at most 6. It is empty when the regions list is unavailable.

- [ ] **Step 1: Write the failing tests** with the fakes (`FakeOnlineUsers` / `FakeGridRegions`; search their names in the tests project) and a `FakeTimeProvider`:
  - `Counts_leave_out_hypergrid_links`: 3 regions, 1 of them a link, gives `RegionsOnline == 2`.
  - `Busiest_orders_by_people_then_name_and_takes_six`: 8 regions with people counts → assert the exact six IDs in order, and that a link with people is not among them.
  - `Unavailable_sources_give_nulls_and_no_places`.
  - `Builds_at_most_once_a_minute`: two calls 30 s apart → the fakes were asked once; after 61 s, twice.
  - `Invalidate_rebuilds_on_the_next_call`.
- [ ] **Step 2: Run them and see them fail.** Command: `... -- --filter-class "*FrontPageSnapshotTests"`. Expected: a compile failure.
- [ ] **Step 3: Implement** the class and register it.
- [ ] **Step 4: Run** the same command and expect a pass.
- [ ] **Step 5: Commit** `feat(front-page): once-a-minute live numbers and busiest regions`.

### Task 4: Public picture and map-tile endpoints

**Files:**
- Create: `Launcher/GridGate.Launcher/Endpoints/MapTiles.cs`. Move the Robust tile fetch out of `SplashApi`'s from-region handler and make that handler call it.
- Create: `Launcher/GridGate.Launcher/Endpoints/FrontApi.cs`. Map it in `Program.cs` next to `SplashApi.Map`.
- Test: `Launcher/GridGate.Launcher.Tests/FrontApiTests.cs`

**Interfaces:**
- Consumes: `FrontPageStore`, `FrontPictures.Shown`, the `"front"` `PictureStore` and `FrontPageSnapshot.CurrentAsync().Busiest`.
- Produces:
  - `public static class MapTiles`, with `static Task<(byte[] Bytes, string ContentType)> FetchAsync(GridRegion region, IGridController grid, IHttpClientFactory http, CancellationToken ct)`. It returns empty bytes when there's no tile or the fetch fails, and uses the `RegionsApi.RobustMapClient` client, as today.
  - `public static class FrontApi`, with `static void Map(WebApplication app)`. Both routes below are `.AllowAnonymous()` and answer 404 unless `AdminRule.HasStaffPages(settings)`:
    - `GET /front/images/{name}` serves the file only when `FrontPictures.Shown(store.Load())` contains `name` and the file exists, with `Cache-Control: public, max-age=31536000, immutable`. Otherwise it answers 404.
    - `GET /front/map/{id:guid}.jpg` serves a tile only when the places source is `Automatic` and `id` is in `Busiest`. Tiles are cached in memory per region for `FrontPageSnapshot.Lifetime`, with `Cache-Control: public, max-age=60`. A missing tile answers 404.
  - `public static string FrontApi.PictureUrl(string name) => "/front/images/" + name` and `public static string FrontApi.MapUrl(Guid id) => $"/front/map/{id}.jpg"`.

- [ ] **Step 1: Write the failing tests.**
  - Set up pictures with `TestPictures.Png` and the store from Task 2; drive the map tile through the factory's existing Robust map fake. Search for how `SplashPicturesTests` tests from-region, and do the same.
  - Tests:
    - `Photo_is_served`.
    - `Own_place_picture_is_served_only_while_source_is_own`: 200 for `Own`, then 404 after saving `Splash`, then 200 again after saving `Own`.
    - `Unknown_name_is_404`.
    - `Map_tile_is_served_for_a_busiest_region_while_automatic`.
    - `Map_tile_is_404_for_a_link_a_region_outside_the_six_or_another_source`: assert the Robust fake was not asked.
    - `Region_server_plan_answers_404_for_both`.
  - The existing `SplashPicturesTests` from-region tests must keep passing unchanged.
- [ ] **Step 2: Run them and see them fail.** Command: `... -- --filter-class "*FrontApiTests" --filter-class "*SplashPicturesTests"`. Expected: the new tests fail.
- [ ] **Step 3: Implement** `MapTiles`, the `SplashApi` call to it, `FrontApi`, and the map call in `Program.cs`.
- [ ] **Step 4: Run** the same command and expect a pass.
- [ ] **Step 5: Commit** `feat(front-page): public front pictures and automatic map tiles`.

### Task 5: The public front page

**Files:**
- Create: `Launcher/GridGate.Launcher/Front/FrontPageView.cs`
- Modify: `Launcher/GridGate.Launcher/Pages/Home.cshtml`, `Home.cshtml.cs`, `wwwroot/css/guest.css`, the six `ResidentText*.resx`
- Modify tests that assert the old page: `FrontPageTests.cs`, `ButtonIconsTests.cs` (the front sign-in button), `LegalPagesTests.cs` (the `#front-legal` buttons), and any other test the suite shows using `#front-sign-in` or `#front-legal`
- Test: `Launcher/GridGate.Launcher.Tests/FrontPagePublicTests.cs`

**Interfaces:**
- Consumes: Tasks 2 to 4, `SplashStore.Published()`, `SplashSnapshot.PictureUrl`, `SignUpRules.IsOpen` and `ResidentLanguageFilter.Current(HttpContext)`.
- Produces:
  - `public enum FrontMainButton { CreateAccount, SignIn, YourAccount }`
  - `public sealed record FrontCard(string Name, string? Line, string? PictureUrl)`
  - `public sealed record FrontNews(DateOnly Date, string Text)`
  - `public sealed record FrontNumber(string Key, string Value)`, where `Key` is the `ResidentText` key of its label.
  - `public sealed record FrontView(string? PhotoUrl, FrontMainButton Main, IReadOnlyList<FrontNumber> Numbers, IReadOnlyList<FrontCard> Places, IReadOnlyList<FrontNews> News)`
  - `public sealed class FrontPageView(FrontPageStore store, SplashStore splash, FrontPageSnapshot snapshot, [FromKeyedServices("front")] PictureStore pictures, Func<LauncherSettings> settings)`, with `Task<FrontView> BuildAsync(string language, bool residentSignedIn, CancellationToken ct)`.
- What `BuildAsync` puts in each part:
  - **Photo:** the photo's URL only if the picture exists in the store.
  - **Main button:** `YourAccount` when a resident is signed in, otherwise `CreateAccount` while sign-up is open, otherwise `SignIn`.
  - **Numbers:** only the switched-on, non-null ones, in the order In-world, Regions, Registered, Active. Values are formatted with `N0` in the page's culture.
  - **Places by source:**
    - *Splash*: the places of every Places block in the published design (block order, then place order), with `SplashSnapshot.PictureUrl`. Empty when nothing is published.
    - *Own*: the own list, with `FrontApi.PictureUrl`.
    - *Automatic*: `Busiest`, with `FrontApi.MapUrl` and no line.
  - **News by source:** the same split between splash and own; items are not hidden, sorted newest first and capped at 5.
  - **Text for the visitor's language:** text in the visitor's language, else `"en"`, else none. A news item with no text is skipped.

The page itself (`Home.cshtml`) has these sections and ids:
- `section#front-hero`: the photo as background with a dark overlay of at least `rgba(0,0,26,.6)`, or the navy gradient when there is no photo. Inside it:
  - `h1` with the grid name;
  - `p.tone-text` with `T["Home.Intro", gridName]`;
  - `a#front-main` pointing to `/signup`, `/guest/sign-in` or `/guest`, as `btn-primary`;
  - `a#front-connect` pointing to `/guest/connect`, as `btn-secondary`.
- `section#front-numbers`: one `.front-number` per number. Left out when there are no numbers.
- `section#front-places`: `h2` plus one `.front-card` per place. Left out when there are no places.
- `section#front-news`: `h2` plus one `article` per item. Left out when there's no news.
- `section#front-join`: `h2` plus `a#front-join-main`, the same target as `#front-main`. Left out for a signed-in resident.

The old sign-in card, create-account card and `#front-legal` buttons are removed. `HomeModel` keeps its guest-principal swap.

Changes to `ResidentText`, made in all six files:
- **Changed:** `Home.Intro` = `Make an avatar and explore {0} with us.`
- **New:**

| Key | English |
|---|---|
| `Home.YourAccount` | `Your account` |
| `Home.HowToConnect` | `How to connect` |
| `Home.InWorld` | `In-world now` |
| `Home.RegionsOnline` | `Regions online` |
| `Home.Registered` | `Registered avatars` |
| `Home.Active` | `Active this month` |
| `Home.NumbersLabel` | `The grid right now` (aria-label) |
| `Home.FeaturedPlaces` | `Featured places` |
| `Home.News` | `News` |
| `Home.ReadyToJoin` | `Ready to join?` |

- **Removed:** `Home.SignInIntro`, `Home.NewHere` and `Home.AboutGrid`, from all six files, if no other page uses them. Check with a search before removing.

- [ ] **Step 1: Write the failing tests** in `FrontPagePublicTests`, using the `PublicLookTests` factory pattern plus the fakes from Tasks 1 to 3:
  - `Hero_has_name_intro_and_create_account_while_sign_up_is_open`: `#front-main` points to `/signup`, `#front-connect` to `/guest/connect`, and the intro text is `"Make an avatar and explore Avalon with us."`.
  - `Main_button_is_sign_in_while_sign_up_is_closed` and `Main_button_is_your_account_for_a_resident`, which also checks there is no `#front-join`.
  - `Hero_uses_the_photo_or_the_gradient`: the style contains `/front/images/{name}` when a photo is set. Without one, there is no `/front/images` in the hero.
  - `Numbers_show_only_switched_on_known_values`: switch one off and make one null; assert exactly the 2 expected tiles in order with `1,204`-style formatting (en).
  - `Numbers_section_is_hidden_when_the_database_is_down` (Review Focus 3): the page is still 200, with `#front-hero` and `#front-join` present.
  - `Places_come_from_the_published_splash_by_default`: publish a splash design with a Places block through `SplashStore`, then assert the card names in order.
  - `Places_section_is_hidden_with_no_published_splash` (Review Focus 2).
  - `Places_from_own_list_and_automatic`: an own list's picture URLs start with `/front/images/`; automatic picture URLs equal `/front/map/{id}.jpg` and there's no line.
  - `News_newest_first_without_hidden_and_at_most_five`.
  - `Text_falls_back_to_english_then_is_left_out` (Review Focus 5): with the visitor's language `de`, a place with only `en` shows the English line, and a news item with only `fr` text is skipped.
  - Update the old assertions in `FrontPageTests`, `ButtonIconsTests` and `LegalPagesTests` to the new ids (`#front-main` instead of `form#front-sign-in`; the legal links stay asserted in the footer).
- [ ] **Step 2: Run them and see them fail.** Command: `... -- --filter-class "*FrontPagePublicTests"`. Expected: they fail.
- [ ] **Step 3: Implement** `FrontPageView` (registered as a singleton), the page, the resx changes and the `guest.css` sections.
  - Hero at least `min(70vh, 36rem)` tall, cover-cropped.
  - Number tiles in a wrapping grid.
  - Place cards: `aspect-ratio: 16/9` pictures in a grid with `repeat(auto-fill, minmax(16rem, 1fr))`.
  - Logical properties only.
- [ ] **Step 4: Run the whole unit suite.** Expected: all pass, including `ResidentTextScanTests`, `LanguageCatalogTests` and `LanguagesPageTests`.
- [ ] **Step 5: Commit** `feat(front-page): the new public front page`.

### Task 6: The Front page staff editor

**Files:**
- Create: `Launcher/GridGate.Launcher/Pages/FrontPage/Index.cshtml`, `Index.cshtml.cs`
- Modify: `Program.cs` (add the `GuestSideOnly` filter for the `/FrontPage` folder, exactly like `/Splash`; and whatever makes `/splash` admin-role only, applied the same way), `Navigation/StaffMenu.cs` (`(admin, new("Front page", "/front-page", "/front-page"))` right after "Splash page"), `Pages/GuestPages/Index.cshtml` (the hint `"The public website's front page: photo, numbers, places and news."`)
- Test: `Launcher/GridGate.Launcher.Tests/FrontPageEditorTests.cs`; update the staff menu and hub tests that list the Guest pages items.

**Interfaces:**
- Consumes: `FrontPageStore`, the `"front"` `PictureStore`, `IGridRegions`, `MapTiles.FetchAsync`, `FrontPageSnapshot.Invalidate()`, and `OfferedLanguages` (which languages get line boxes).
- Produces: `@page "/front-page"` with these POST handlers. Each one is antiforgery-checked, saves through `FrontPageStore.Save`, calls `snapshot.Invalidate()`, and redirects back to `/front-page` with a saved or problem message (TempData, as other staff pages do):

| Handler | What it does |
|---|---|
| `Photo` (`IFormFile file`) | Uploads to the `"front"` store and sets `Photo` |
| `RemovePhoto` | Clears `Photo` |
| `Numbers` (`bool inWorld, regions, registered, active`) | Saves the four switches |
| `PlacesSource` (`FrontPlacesSource source`) | Saves the places source |
| `AddPlace` (`Guid regionId`) | Adds the region, if it is in the grid list and not a link |
| `SavePlace` (`int index`, `Dictionary<string,string> line`, `IFormFile? file`, `bool fromMap`) | Saves a place's lines and optionally its picture |
| `MovePlace` (`int index`, `int by`) | Moves a place up or down |
| `RemovePlace` (`int index`) | Removes a place |
| `NewsSource` (`FrontNewsSource source`) | Saves the news source |
| `AddNews` (`DateOnly date`, `Dictionary<string,string> text`) | Adds a news item |
| `ToggleNews` (`int index`) | Hides or shows a news item |
| `RemoveNews` (`int index`) | Removes a news item |

  An index out of range answers 400, and nothing is saved.
- The page body shows:
  - the current photo (served via `/front/images` when shown);
  - the own lists, even while another source is chosen;
  - a "See the front page" link to `/home`.

- [ ] **Step 1: Write the failing tests** in `FrontPageEditorTests`, signing in as admin as in `GuestPagesHubTests`:
  - `Admin_sees_the_editor_and_supervisor_does_not` (use the same status code `/splash` gives a supervisor).
  - `Region_server_plan_answers_404`.
  - `Uploading_a_photo_shows_it_on_the_front_page`.
  - `Switching_sources_keeps_the_own_list`: add a place, set `Splash`, then assert the own list is still listed on the editor and still in `store.Load().Places`.
  - `Add_place_refuses_a_hypergrid_link`.
  - `Place_from_map_tile_gets_a_front_picture`.
  - `Move_and_remove_places`.
  - `Add_hide_and_remove_news`.
  - `Saving_invalidates_the_snapshot`: switching a number off hides it on `/home` at once.
  - `Out_of_range_index_is_400`.
  - `Posts_without_a_token_are_refused`.
  - `Menu_and_hub_list_front_page_after_splash_page`.
- [ ] **Step 2: Run them and see them fail.** Command: `... -- --filter-class "*FrontPageEditorTests"`.
- [ ] **Step 3: Implement** the page and the wiring.
- [ ] **Step 4: Run the whole unit suite.** Expected: all pass.
- [ ] **Step 5: Commit** `feat(front-page): the Front page staff editor`.

### Task 7: See it in the browser

- [ ] **Step 1:** Run the launcher from this worktree in Debug, so it doesn't lock the Release test build. Use a throwaway data folder with setup marked finished. As owner, use `/front-page` to upload a photo, add two own places (one from a map tile) and a news item, and try each source.
- [ ] **Step 2:** Check `/home` at desktop width and at the mobile preset:
  - the words over the photo are readable;
  - nothing overflows;
  - hidden sections leave no gaps;
  - the buttons go to the right places.
- [ ] **Step 3:** Fix anything found (with a test if markup changes), run the whole suite, and commit `fix(front-page): <what>`.
