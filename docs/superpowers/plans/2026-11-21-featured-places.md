# Featured Places Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A public `/featured` directory of the parcels residents put in search, with visits counted per parcel by a new region add-on.

**Architecture:** A region add-on (`addon-modules/GridGateTraffic`) becomes OpenSim's dwell module and keeps each parcel's fading visit count in `land.Dwell`. The launcher reads listed parcels from `opensim.land` (+ `regionsettings`), keeps only those on online regions of this grid, caches the result for a minute, and renders `/featured` server-side with a GET filter form. Cards use region map tiles through the front page's tile cache.

**Tech Stack:** OpenSim region modules (Mono.Addins, Nini), xunit v3 + OpenSim.Tests.Common; ASP.NET Core Razor Pages, MySqlConnector, AngleSharp tests.

**Spec:** `docs/superpowers/specs/2026-11-21-featured-places-design.md`

## Global Constraints

- Listed = `(LandFlags & 0x1000) != 0` (`ParcelFlags.ShowDirectory`); MaturePublish = `0x40000`; Adult category = 2; None = 0; categories 0..13.
- Only parcels on regions in `IGridRegions.ListAsync()` with `IsLink == false`.
- Rating = region maturity (0 General, 1 Moderate, 2 Adult), raised to 2 for category 2, to at least 1 for MaturePublish. Not signed in: ratings 0 and 1 only — in cards, counts, stats and the map tile endpoint.
- Directory built at most once a minute (`FeaturedDirectory.Lifetime = 60 s`), shared; database timeout 3 s; failures give "unavailable", never an error page.
- Every visitor-facing word in `ResidentText` (en, de, es, fr, pt, ru). Residents' text shown as text only.
- Traffic: once per avatar per parcel per UTC day; not NPCs, child agents, the parcel's own owner, or while the estate has BlockDwell; fade `exp(-Δt / 30 days)`; `[Dwell] DwellModule = GridGateTraffic`.
- No new NuGet packages; no OpenSim source file edits (add-on only).

## Review Focus

- **Adult places leaking to strangers** through counts, stats, category counts, paging or `/featured/map/{id}.jpg`. Tests in Task 4/5.
- **Unknown/odd rows:** NULL name/description/category/maturity, a category outside 0..13, a region in `land` but not online, a parcel on a hypergrid link id. Tests in Task 4.
- **Search and page query strings** that are empty, huge, negative or non-numeric (`page=-3`, `page=abc`, `category=99`, a 5,000-char `q`) must give a normal page. Test in Task 5.
- **A region restart** mid-day: the in-memory day set is lost; a visitor may count twice that day (accepted, documented). Pure-counter tests pin the decay and once-a-day rules (Task 1).
- **Database down or tables missing** (fresh grid before OpenSim made `land`): the page says unavailable, never 500. Test in Task 4.

---

### Task 1: The traffic counter add-on

**Files:**
- Create: `addon-modules/GridGateTraffic/GridGateTraffic.csproj` (copy GridGateMoney's shape; AssemblyName `GridGateTraffic`, RootNamespace `GridGate.Region.Traffic`, references OpenSim.Framework + OpenSim.Region.Framework + OpenMetaverse/Types, Nini, log4net, Mono.Addins)
- Create: `addon-modules/GridGateTraffic/AssemblyInfo.cs` (`[assembly: Addin("GridGateTraffic", …)]`, dependency, `InternalsVisibleTo("GridGate.Traffic.Tests")`)
- Create: `addon-modules/GridGateTraffic/ParcelTraffic.cs` — pure counting, no OpenSim types beyond Guid/UUID
- Create: `addon-modules/GridGateTraffic/GridGateTrafficModule.cs` — `INonSharedRegionModule, IDwellModule`, `[Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "GridGateTraffic")]`
- Create: `tests/GridGate.Traffic.Tests/` (csproj like GridGate.Money.Tests), `ParcelTrafficTests.cs`, `TrafficModuleTests.cs`
- Modify: `GridGate.sln` (both projects), `.github/workflows/gridgate-ci.yml` (run the new tests on Linux and Windows next to Money)

**Interfaces:**
- Produces: `internal sealed class ParcelTraffic(TimeSpan meanLife)` with
  - `bool Visit(Guid parcel, Guid agent, DateTimeOffset now, ref double value, ref DateTimeOffset since)` — decays `value` from `since` to `now`, adds 1 unless this agent already counted on this parcel this UTC day; returns whether it counted; sets `since = now`.
  - `static double Decayed(double value, DateTimeOffset since, DateTimeOffset now, TimeSpan meanLife)`.
- Produces: `GridGateTrafficModule.ModuleName = "GridGateTraffic"`; `Enabled` when `[Dwell] DwellModule` equals it.

- [ ] **Step 1: Failing tests (`ParcelTrafficTests`)**: `First_visit_counts_one` (value 0 → 1, returns true); `Same_agent_same_day_counts_once`; `Same_agent_next_utc_day_counts_again`; `Other_agent_counts`; `Value_fades_with_30_day_mean_life` (`Decayed(100, t, t+30d, 30d)` ≈ 36.79 ± 0.01); `Visit_decays_before_adding` (value 10 since t, visit at t+30d → ≈ 4.68).
- [ ] **Step 2: Failing tests (`TrafficModuleTests`)**: `Disabled_without_dwell_setting` (no `[Dwell]` → `Enabled` false; `AddRegion(null!)` safe); `Enabled_by_dwell_setting`; in a SceneHelpers region with the land module and this module: `Avatar_arriving_counts_on_parcel` (add a presence for a non-owner, trigger `OnAvatarEnteringNewParcel` for the default parcel → `GetDwell(land)` == 1 and the module is the scene's `IDwellModule`); `Owner_does_not_count`; `Block_dwell_stops_counting` (`RegionInfo.EstateSettings.BlockDwell = true`).
- [ ] **Step 3: Run** `dotnet test --project tests/GridGate.Traffic.Tests -c Release` → FAIL (types missing).
- [ ] **Step 4: Implement** `ParcelTraffic` (day set: `HashSet<(Guid, Guid)>` + current UTC date, cleared on day change) and the module: `Initialise` reads `[Dwell] DwellModule`; `AddRegion` registers `IDwellModule`; `RegionLoaded` hooks `OnNewClient` (dwell reply like DefaultDwellModule) and `OnAvatarEnteringNewParcel`; on a counted visit set `LandData.Dwell = (float)value` and `scene.SimulationDataService.StoreLandObject(land)`; a 1-hour `System.Threading.Timer` decays every parcel and stores those whose rounded value changed; `RemoveRegion` decays and stores all, then disposes the timer. Exact values per parcel kept in a `Dictionary<UUID, (double Value, DateTimeOffset Since)>` seeded from `LandData.Dwell` on first touch. Lock around state.
- [ ] **Step 5: Run** the tests → PASS; `dotnet build GridGate.sln -c Release` succeeds and `bin/GridGateTraffic.dll` exists.
- [ ] **Step 6: Commit** `feat(traffic): regions count each parcel's recent visits (once per avatar a day, fading over 30 days)`

### Task 2: Regions load the traffic counter

**Files:** Modify `Launcher/GridGate.Launcher.Core/Config/GridConfigWriter.cs` (after RegionReady: `os.Set("Dwell", "DwellModule", TrafficModuleName)`, `public const string TrafficModuleName = "GridGateTraffic"`); Snapshots `*.OpenSim.ini`; Test `GridConfigWriterTests.cs`.

- [ ] **Step 1: Failing test** `Region_ini_turns_on_the_traffic_counter`: write a local grid, read the region `OpenSim.ini` → `[Dwell] DwellModule = GridGateTraffic`.
- [ ] **Step 2: Run** `--filter-class "*GridConfigWriterTests"` → new test FAILS.
- [ ] **Step 3: Implement**, regenerate the three `*.OpenSim.ini` snapshots the way the snapshot test documents.
- [ ] **Step 4: Run** `--filter-class "*GridConfigWriterTests"` → PASS.
- [ ] **Step 5: Commit** `feat(config): every region uses GridGate's traffic counter`

### Task 3: Words

**Files:** `Launcher/GridGate.Launcher/Resources/ResidentText*.resx` (6 files).

Keys (English): `Featured.Title` "Featured places"; `Featured.Intro` "Places residents chose to show. Teleport straight in with your viewer."; `Featured.StatPlaces` "Places"; `Featured.StatVisits` "Recent visits"; `Featured.StatCategories` "Categories"; `Featured.StatRegions` "Regions"; `Featured.TeleportNote` "Teleport links open your viewer."; `Featured.Search` "Search places"; `Featured.AnyCategory` "Every category"; `Featured.AnyRating` "Every rating"; `Featured.SortVisits` "Most visited"; `Featured.SortName` "By name"; `Featured.Show` "Show"; `Featured.Teleport` "Teleport"; `Featured.Visits` "{0} recent visits"; `Featured.Unnamed` "Unnamed place"; `Featured.None` "No places are listed yet. To list yours: in your viewer, About Land → Options → Show in search."; `Featured.NoMatch` "No places match. Try other words or another category."; `Featured.Unavailable` "The directory can't be read right now. Try again in a minute."; `Featured.Previous` "Previous"; `Featured.Next` "Next"; `Featured.Page` "Page {0} of {1}"; `Featured.RatingGeneral` "General"; `Featured.RatingModerate` "Moderate"; `Featured.RatingAdult` "Adult"; `Layout.MenuFeatured` "Featured"; `Featured.Category1`…`Featured.Category13`: Grid location, Adult, Arts & culture, Business, Educational, Gaming, Hangout, Newcomer friendly, Parks & nature, Residential, Shopping, Stage, Other.

- [ ] Add to all six files (translated). Covered by the existing keys tests once Task 5's page uses them.

### Task 4: The directory data

**Files:**
- Create: `Launcher/GridGate.Launcher.Core/Grid/ListedParcels.cs` — `public sealed record ListedParcel(Guid Id, Guid RegionId, string Name, string Description, int Category, int Rating, int Visits, bool HasLandingPoint, float X, float Y, float Z)`; `public sealed record ListedParcelList(IReadOnlyList<ListedParcel> Parcels, bool Available)`; `public interface IListedParcels { Task<ListedParcelList> ListAsync(CancellationToken ct); }`; `ListedParcels(Func<LauncherSettings>, ILogger?)` reading `opensim` (3 s, `GridDb.OpenOpensimAsync`), missing tables → `([], true)`.
- Create: `Launcher/GridGate.Launcher/Featured/FeaturedDirectory.cs` — `public sealed record FeaturedPlace(ListedParcel Parcel, GridRegion Region)`; `public sealed record FeaturedData(IReadOnlyList<FeaturedPlace> Places, bool Available)`; `FeaturedDirectory(IListedParcels, IGridRegions, TimeProvider)` with `Task<FeaturedData> CurrentAsync(CancellationToken)` (built ≤ once a minute, shared, like `WorldMapSnapshot`), places only on online non-link regions.
- Create: `Launcher/GridGate.Launcher/Featured/FeaturedQuery.cs` — pure: `public static FeaturedView View(FeaturedData data, FeaturedFilter filter, bool signedIn)` returning visible places after rating gate, stats (`Places, Visits, Categories, Regions`), category counts, the filtered+sorted page (24), page number and count. `FeaturedFilter(string? Q, int? Category, int? Rating, string? Sort, int Page)` with `Parse` clamping everything.
- Modify: `LauncherServices.cs` registrations; `LauncherFactory` gets `FakeListedParcels Listed`.
- Test: `Launcher/GridGate.Launcher.Tests/FeaturedQueryTests.cs`, `FeaturedDirectoryTests.cs`.

- [ ] **Step 1: Failing tests** — `FeaturedQueryTests`: `Strangers_never_see_adult_places` (region rating 2, Adult category, all in stats/category counts/cards → absent when `signedIn: false`, present when true); `Mature_content_flag_counts_as_moderate` (via the rating `ListedParcels` computes — test the static `ListedParcels.RatingOf(regionMaturity, category, flags)`: (0,0,0)→0, (0,2,0)→2, (0,0,0x40000)→1, (1,0,0)→1, (null,…)→0); `Search_matches_name_description_and_region_ignoring_case`; `Category_filter_and_counts`; `Sort_by_visits_then_name_and_by_name`; `Paging_24_and_clamped` (`page=99` → last page; `page=-3` → 1); `Odd_filter_values_are_ignored` (`category=99`, rating 7, sort "x", 5,000-char q truncated to 100); `Stats_skip_none_category`. `FeaturedDirectoryTests`: `Only_online_own_regions` (parcel on a link id and on an absent region dropped); `Built_once_a_minute` (two calls, one parcel read; after 61 s, two); `Unavailable_when_regions_or_parcels_unavailable`.
- [ ] **Step 2: Run** `--filter-class "*FeaturedQueryTests" --filter-class "*FeaturedDirectoryTests"` → FAIL.
- [ ] **Step 3: Implement.** SQL: `SELECT l.UUID, l.RegionUUID, l.Name, l.Description, l.Category, l.LandFlags, l.Dwell, l.LandingType, l.UserLocationX, l.UserLocationY, l.UserLocationZ, rs.maturity FROM land l LEFT JOIN regionsettings rs ON rs.regionUUID = l.RegionUUID WHERE (l.LandFlags & 4096) <> 0`. Landing point = `LandingType == 1` and any coordinate ≠ 0. Category outside 0..13 → 13 (Other). Error handling exactly as `GridRegions.ListAsync`; MySQL error 1146 (no such table) → empty available list.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(featured): the directory of listed places on the grid's online regions`

### Task 5: The page, its pictures and the menu

**Files:**
- Create: `Launcher/GridGate.Launcher/Pages/Featured.cshtml` + `.cshtml.cs` (`@page "/featured"`, `Layout = "_GuestLayout"`, 404 where `!AdminRule.HasStaffPages(s)` like the map); signed-in = `WorldMapAccess.CheckAsync(ctx) == MapAccess.Allowed`.
- Create: `Launcher/GridGate.Launcher/Endpoints/FeaturedApi.cs` — `GET /featured/map/{id:guid}.jpg`: 404 unless the region has a place in `FeaturedQuery.View(..., signedIn)`'s visible set; tile via `FrontMapTiles.TileAsync(GridRegion, ct)` (new overload; the old one calls it); empty tile → 404; `Cache-Control: public, max-age=300` for General/Moderate-only regions, `private` when the asker is signed in.
- Modify: `Pages/Shared/_GuestLayout.cshtml` (menu link `id="menu-featured" href="/featured"` after Map, icon "star" if `ButtonIcons` has it, else "flag"); `PublicLookTests` menu list; `wwwroot/css/guest.css` (`.featured-*` rules reusing `.front-numbers`/`.front-card` look); `Program.cs`/endpoint mapping next to `FrontApi.Map`.
- Test: `Launcher/GridGate.Launcher.Tests/FeaturedPageTests.cs`.

- [ ] **Step 1: Failing tests:** `Visitor_sees_listed_places_with_teleport` (card name, region, category word, "N recent visits", `a[href^='hop://']` = `TeleportStart + Uri.EscapeDataString(region) + "/128/64/25"` for a landing point, region-only link without); `Stats_row_and_hidden_zero_tiles`; `Adult_place_hidden_from_visitor_but_shown_to_resident`; `Map_picture_only_for_shown_regions` (shown region 200 image/jpeg; region with only an Adult place → 404 for visitor; unknown id 404); `Empty_directory_says_how_to_list`; `Unavailable_says_so`; `Odd_query_strings_give_a_page` (`?page=abc&category=99&q=<5000 chars>` → 200); `Description_is_text_not_html` (`<b>x</b>` appears escaped); `Menu_lists_featured` (update `PublicLookTests.Visitor_top_bar_has_login_register_and_menu` list).
- [ ] **Step 2: Run** `--filter-class "*FeaturedPageTests" --filter-class "*PublicLookTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** those plus `*SignInPagesKeysTests`/any ResidentText key-coverage tests → PASS.
- [ ] **Step 5: Commit** `feat(featured): the public Featured places page`

### Task 6: Whole check

- [ ] Full `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release` and `dotnet test --project tests/GridGate.Traffic.Tests -c Release` → 0 failed.
- [ ] `dotnet build GridGate.sln -c Release` → 0 errors (CI builds the whole solution).
- [ ] Browser: `/featured` on the scratch launcher (no Robust there, so expect the "unavailable" or empty state), at 1280 and 375 px; the menu link.
- [ ] `bash .github/scripts/check-old-name.sh` clean.
