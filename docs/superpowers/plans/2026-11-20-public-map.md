# Public World Map Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A `/map` page on the guest side: signed-out visitors get the live numbers and a sign-in box; signed-in residents and staff get a drag-and-zoom map of the grid's own regions with a card per region and a `hop://` Teleport here link.

**Architecture:** Two new data links (`/world/regions`, kept 30 s and shared; `/world/tile/{z}/{x}/{y}`, the staff tile proxy's code shared) that answer only signed-in residents and staff. The staff `map.js` is split: its drawing part becomes `map-core.js` (`startMapView`), used by the staff map and by a new `world-map.js`. Sign in learns a local return address.

**Tech Stack:** ASP.NET Core Razor Pages and minimal APIs (.NET 10), hand-written ES5, Bootstrap 5, xUnit v3 with AngleSharp.

**Spec:** `docs/superpowers/specs/2026-11-20-public-map-design.md`

## Global Constraints

- CSP `script-src 'self'; style-src 'self'; img-src 'self' data:`: no inline scripts or styles; scripts may set `element.style`.
- Resident words only through `ResidentText` (6 files: `ResidentText.resx`, `.de`, `.es`, `.fr`, `.pt`, `.ru`); translations keep the English's `{0}` fill-ins. Edit resx with Python keeping the file's CRLF (never `sed -i`).
- No hard-coded English on `/map` (`ResidentTextScanTests`).
- Region names on the page and in the script go through `textContent`, never `innerHTML`.
- Only the grid's own regions (`!IsLink`); no region ids, states, programs or ports in `/world/regions`.
- `/map` and `/world/*` exist only with a guest side (`GuestSideOnly`, or `AdminRule.HasStaffPages` in endpoints): 404 on a Region Server or Connected Server.
- Test command: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release -- --filter-class "*<Class>"`; the whole suite (about 10 min) runs in the background.

## Review Focus

1. **A region name with spaces, an apostrophe, `<b>` or non-Latin letters**: the card shows it as text, the hop link escapes it (`encodeURIComponent`). Pinned by the Task 6 browser check with a stubbed list.
2. **The sign-in ends while the map is open** (cookie expired or revoked): the next `/world/regions` poll gets 401 and the page reloads into the signed-out view, no endless errors. Task 4 test `Regions_need_a_sign_in` plus the Task 6 browser check.
3. **Staff outside the staff networks**: the staff cookie is ignored there, so they get the signed-out view, not the map. Task 6 test `Staff_outside_the_staff_networks_see_the_invitation`.
4. **Robust's database down**: `/world/regions` says unavailable and the page shows the "can't be shown right now" line; a missing map picture leaves navy water. Task 4 test `Regions_unavailable_says_so`.
5. **A large region (2×2 or bigger)**: its outline covers all its squares and a click anywhere in it gives coordinates up to its full size in metres. Task 3 test for sizes and the Task 6 browser check.

---

### Task 1: Open the public picture and map paths outside the staff networks

The front page's pictures (`/front/images/…`, `/front/map/…`) are missing from `AdminNetworkFilter.PublicPaths`, so with a staff network list set, visitors outside it get 404 for the front page photo and place pictures. Fix it here together with the map's two paths.

**Files:**
- Modify: `Launcher/GridGate.Launcher/Security/AdminNetworkFilter.cs` (`PublicPaths`, and the `Exempt` summary)
- Test: `Launcher/GridGate.Launcher.Tests/FrontApiTests.cs`

**Interfaces:**
- Produces: `/front`, `/map` and `/world` are exempt paths (Tasks 4 and 6 rely on it).

- [ ] **Step 1: Write the failing test** `Front_pictures_reachable_outside_the_staff_networks` in `FrontApiTests`, built like `ThemeLogoTests.Theme_images_reachable_with_admin_network_list`: every caller from `192.168.1.50`, a front photo saved (through the `"front"` `PictureStore` and `FrontPageStore.Save`), then `AdminNetworks = ["10.0.0.0/8"]`.
  - `GET FrontApi.PictureUrl(photo)` → 200.
  - `GET /login` → 404.
- [ ] **Step 2: Run it**, `--filter-class "*FrontApiTests"`. Expected: FAIL, 404 for the picture.
- [ ] **Step 3: Add `"/front"`, `"/map"`, `"/world"` to `PublicPaths`** and name them in the summary ("the front page's pictures, the world map and its data").
- [ ] **Step 4: Run it again.** Expected: PASS.
- [ ] **Step 5: Commit** `fix(front): front page pictures show outside the staff networks; the map's paths open too`.

### Task 2: Sign in comes back to a local return address

**Files:**
- Create: `Launcher/GridGate.Launcher/Guests/GuestReturn.cs`
- Modify: `Launcher/GridGate.Launcher/Pages/Guest/SignIn.cshtml.cs`, `Launcher/GridGate.Launcher/Pages/Guest/SignInCode.cshtml.cs`
- Test: `Launcher/GridGate.Launcher.Tests/GuestReturnTests.cs` (new), plus page tests in `GuestSignInTests.cs` and `TwoFactorSignInTests.cs`

**Interfaces:**
- Produces: `public static class GuestReturn { public static string Pick(string? returnUrl); }`. It gives `returnUrl` when it starts with `/` and its second character is neither `/` nor `\`, and it has no control characters; `"/guest"` otherwise. Task 6 links to `/guest/sign-in?returnUrl=%2Fmap`.

- [ ] **Step 1: Write the failing tests.**
  - `GuestReturnTests.Pick_keeps_local_paths`: `/map` → `/map`; `/guest/inventory?x=1` → same.
  - `Pick_refuses_other_places` (Theory): `null`, `""`, `"map"`, `"//evil.example"`, `"/\\evil.example"`, `"https://evil.example/"`, `"/\r\nx"` → `/guest`.
  - Page tests:
    - `Sign_in_returns_to_the_map`: a POST to `/guest/sign-in?returnUrl=%2Fmap` with the right password → 302 to `/map`.
    - `Sign_in_ignores_another_site`: `returnUrl=%2F%2Fevil.example` → 302 to `/guest`.
    - `Two_factor_carries_the_return`: for a resident with two-factor on, the sign-in → 302 to `/guest/sign-in/code?returnUrl=%2Fmap`; the right code posted there → 302 to `/map`.
    - The approved-greeting redirect (`/guest/connect?approved=1`) still wins over a return address.
- [ ] **Step 2: Run them.** Expected: FAIL (no `GuestReturn`; redirects go to `/guest`).
- [ ] **Step 3: Implement.**
  - `GuestReturn.Pick`.
  - `SignInModel` and `SignInCodeModel` get `[BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }`. The forms post to the page's own address, query included, so it carries through.
  - SignIn's success path: `LocalRedirect(GuestReturn.Pick(ReturnUrl))`.
  - The code step: `"/guest/sign-in/code" + (GuestReturn.Pick(ReturnUrl) is var r && r != "/guest" ? "?returnUrl=" + Uri.EscapeDataString(r) : "")`.
  - SignInCode's success: `LocalRedirect(GuestReturn.Pick(ReturnUrl))`.
  - Side effect, intended: the guest cookie's own challenge sends `ReturnUrl=`, so any guest page now comes back to itself after sign-in.
- [ ] **Step 4: Run them** plus `"*GuestSignInTests"`, `"*TwoFactorSignInTests"` and `"*TermsAcceptanceTests"`. Expected: PASS.
- [ ] **Step 5: Commit** `feat(guest): sign-in comes back to the page that asked for it (local addresses only)`.

### Task 3: Region sizes from Robust

**Files:**
- Modify: `Launcher/GridGate.Launcher.Core/Grid/GridRegions.cs`
- Test: `Launcher/GridGate.Launcher.Tests/GridRegionsTests.cs` (new)

**Interfaces:**
- Produces:
  - `public sealed record GridRegion(Guid Id, string Name, int X, int Y, bool IsLink = false, int SizeX = 1, int SizeY = 1);` with sizes in map squares. Existing callers keep compiling.
  - `public static int GridRegions.Squares(object? metres)`: `max(1, metres / 256)`; null, `DBNull` or 0 gives 1.

- [ ] **Step 1: Write the failing test** `Squares_turns_metres_into_map_squares` (Theory): `0→1`, `256→1`, `512→2`, `768→3`, `DBNull.Value→1`, `null→1`, `(long)1024→4`.
- [ ] **Step 2: Run it.** Expected: FAIL to compile.
- [ ] **Step 3: Implement.**
  - The query becomes `SELECT uuid, regionName, locX, locY, flags, sizeX, sizeY FROM regions ORDER BY regionName`.
  - Fill `SizeX` and `SizeY` with `Squares(reader.GetValue(5))` and `Squares(reader.GetValue(6))`.
  - Update the record's `<param>` docs.
- [ ] **Step 4: Run it, and run `FrontPageSnapshotTests`.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(grid): the grid's region list knows each region's size`.

### Task 4: The world map's data links

**Files:**
- Create: `Launcher/GridGate.Launcher/World/WorldMapAccess.cs`, `Launcher/GridGate.Launcher/World/WorldMapSnapshot.cs`, `Launcher/GridGate.Launcher/Endpoints/WorldMapApi.cs`
- Modify:
  - `Launcher/GridGate.Launcher/Endpoints/MapTiles.cs`: add the shared tile proxy.
  - `Launcher/GridGate.Launcher/Endpoints/RegionsApi.cs`: the staff tile route calls it.
  - `Launcher/GridGate.Launcher/Guests/TermsGate.cs`: pull its check into a static.
  - `Launcher/GridGate.Launcher/Program.cs`: register the snapshot and `WorldMapApi.Map(app)`.
- Test: `Launcher/GridGate.Launcher.Tests/WorldMapApiTests.cs` (new); `RegionsApiTests` must stay green.

**Interfaces:**
- Consumes: `GridRegion.SizeX/SizeY` (Task 3); the exempt `/world` (Task 1).
- Produces:
  - `public enum MapAccess { SignedOut, NeedsTerms, Allowed }`.
  - `public static class WorldMapAccess { public static Task<MapAccess> CheckAsync(HttpContext ctx); }`:
    - **Allowed** for a valid staff sign-in (`AuthenticateAsync(AuthSchemes.Admin)`, `StaffSignIn.RoleOf(...) != StaffRole.None`).
    - **NeedsTerms** for a resident (`AuthenticateAsync(AuthSchemes.Guest)`, `GuestSignIn.Read`) who `TermsGate.MustAccept` says hasn't accepted.
    - **Allowed** for any other resident.
    - **SignedOut** otherwise.
  - `public static bool TermsGate.MustAccept(HttpContext http, Guid principal)`: the existing Terms check, which `OnPageHandlerExecutionAsync` now calls.
  - `public sealed record WorldRegion(string Name, int X, int Y, int W, int H, int? People);`
  - `public sealed record WorldMapData(IReadOnlyList<WorldRegion> Regions, bool Available);`
  - `public sealed class WorldMapSnapshot(IOnlineUsers online, IGridRegions regions, TimeProvider time)`:
    - `public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);`
    - `public Task<WorldMapData> CurrentAsync(CancellationToken ct);`
    - Built like `FrontPageSnapshot`: one build at a time, shared. `People` is null when the online list is unavailable.
  - `public static string WorldMapApi.TeleportStart(LauncherSettings s)`: `GridEndpoints.LoginUri(s, HostAddress.Effective(s.Hostname))` parsed as a `Uri`, giving `$"hop://{uri.Host}:{uri.Port}/"`.
  - `public static Task<IResult> MapTiles.TileAsync(int z, int x, int y, IGridController grid, IHttpClientFactory http, HttpContext ctx, string cacheControl)`: the body of today's `/api/map/tile` route, with the same 400 and 404 rules. The staff route passes `"private, max-age=60"`; the world route passes `"private, max-age=300"`.
  - Routes:
    - `GET /world/regions` → `{ regions: [{ name, x, y, w, h, people }], available, teleport }`, with `Cache-Control: no-store`.
    - `GET /world/tile/{z:int}/{x:int}/{y:int}`.
    - Both are `.AllowAnonymous()`. They return 404 when `!AdminRule.HasStaffPages(settings)`, and 401 unless `CheckAsync` is `Allowed`.

- [ ] **Step 1: Write the failing tests** in `WorldMapApiTests`. Use the `TermsAcceptanceTests.Factory` shape: Avalon, setup finished, `TestAuth.Apply`; residents via `/__test/guest-signin`, staff via `/__test/signin?role=supervisor`.
  - `Regions_need_a_sign_in`: `/world/regions` and `/world/tile/1/1000/1000` → 401 with no `Location` header.
  - `Regions_list_own_regions_with_sizes_and_people`:
    - Fake regions: `Welcome` at (1000, 1000) size 2×2; `Sandbox` at (1002, 1000) size 1×1; a link `Other` (`IsLink: true`).
    - Two online avatars in Welcome.
    - Expect, as a resident: exactly `Sandbox` (people 0) and `Welcome` (w 2, h 2, people 2). No `id` or `state` property anywhere in the JSON.
  - `Staff_can_read_the_regions`: supervisor → 200.
  - `People_unknown_when_the_online_list_is_down`: `OnlineUsers.Result = OnlineList.Unavailable` → `people` null.
  - `Regions_unavailable_says_so`: `GridRegions.Result = GridRegionList.Unavailable` → `available: false`, empty list.
  - `The_list_is_kept_30_seconds`: change the fake regions after a first call; a second call shows the old list; after `f.Time.Advance(TimeSpan.FromSeconds(31))` it shows the new one.
  - `Teleport_starts_at_the_login_address`: `Hostname = "grid.example.com"`, `RobustPublicPort = 8002` → `teleport == "hop://grid.example.com:8002/"`. After saving port 9000, the next call (no wait) shows `:9000/`.
  - `World_tiles_check_their_numbers`: as a resident, `/world/tile/0/0/0` and `/world/tile/2/1/0` → 400.
  - `Not_there_without_a_guest_side`: plan `RegionServer` → both routes 404, even as staff.
  - `A_resident_who_owes_the_Terms_gets_401`: Terms version 1 saved, not accepted → 401.
  - `Regions_reachable_outside_the_staff_networks`: caller `192.168.1.50`, `AdminNetworks = ["10.0.0.0/8"]`, resident → 200.
- [ ] **Step 2: Run them.** Expected: FAIL (404s: no routes).
- [ ] **Step 3: Implement** the interfaces above.
  - Move the staff tile body into `MapTiles.TileAsync`; `RegionsApi` keeps its route, policy and behaviour.
  - Order regions by name, as `GridRegions` returns them.
  - Register `WorldMapSnapshot` as a singleton next to `FrontPageSnapshot`.
- [ ] **Step 4: Run** `--filter-class "*WorldMapApiTests"`, then `"*RegionsApiTests"`, `"*TermsAcceptanceTests"` and `"*FrontPageSnapshotTests"`. Expected: PASS.
- [ ] **Step 5: Commit** `feat(map): the world map's region list and pictures, for signed-in residents and staff`.

### Task 5: Split the staff map's drawing code into map-core.js

**Files:**
- Create: `Launcher/GridGate.Launcher/wwwroot/js/map-core.js`
- Modify: `Launcher/GridGate.Launcher/wwwroot/js/map.js`, `Launcher/GridGate.Launcher/Pages/Regions/Index.cshtml` (load `/js/map-core.js` before `/js/map.js`)
- Test: `Launcher/GridGate.Launcher.Tests/RegionsPageTests.cs`: assert both script tags, core first.

**Interfaces:**
- Produces: `function startMapView(map, options)`, a global in `map-core.js`. It returns `api`.
  - **options:**
    - `tilesUrl`, `regionsUrl`, `pollMs`.
    - `load(url) → Promise<json>`.
    - `regionsOf(json) → array`.
    - `keyOf(r) → string`.
    - `markerClass(r) → string`: extra classes after `map-marker`.
    - `markerTitle(r) → string`.
    - `zoomIn`, `zoomOut`: button elements.
    - `startZoom`: default 3.
    - `onClick(point, region)`: point is `{ x, y }` in fractional squares; region is the clicked one or null.
    - `onHover(point)`, `onLeave()`: mouse/pen only, not while panning.
    - `onRegions(list, first)`.
    - `onRelayout()`: after a zoom, so extra elements can be placed again.
    - `onError(e)`: a failed poll.
  - A region's box is `r.x, r.y, (r.w || r.size), (r.h || r.size)`.
  - Tiles are refreshed only when a region's `x, y, w/h/size` or `state` changed, so people counts never refetch pictures.
  - **api:** `layer`, `view`, `place(el, x, y, w, h)`, `toWorld(px, py)`, `pointIn(e)`, `fitBox(x0, y0, x1, y1)`, `fitAll(list)`, `update()`, `regions()`, `markerOf(key)`, `restyle()` (applies `markerClass` again), `start()` (first layout and polling).
- What moves into the core: view/origin/layer, `scale`, `span`, `toWorld`, `pointIn`, `place`, `moveLayer`, `relayout`, `update`, the tile loading and refresh, `markTiles`, `zoomTo`, `fitBox`, the markers, `refresh`, the first fit, `every`, the pointer pan/click handling, the wheel, the zoom buttons and `ResizeObserver`.
- What stays in `map.js`: `mapFetch`/`getJson`/`getText`, the panel, the URL `?region=`, placing (ghost, `ask`, `answers`, `chooseSpot`), move mode, types, Escape, "Back to my regions" (via `api.fitAll` over live regions), and `markSelected` (via `api.restyle`). The first fit for a `selectedId` uses `onRegions(list, first)`.

- [ ] **Step 1: Before touching code**, open the staff Regions page in the browser preview (`preview_start` with a launch config for this worktree, Debug, a scratch `--data` folder, set up as owner with a couple of regions saved in settings). Note each behaviour on the checklist below.
- [ ] **Step 2: Write the failing page test** `Regions_page_loads_the_map_core_first`. Run it. Expected: FAIL.
- [ ] **Step 3: Split the file** as above. Behaviour, markup, CSS classes and endpoints are unchanged.
- [ ] **Step 4: Run the page test.** Expected: PASS.
- [ ] **Step 5: Browser checklist**, with no console errors after each item:
  - Drag pans the map.
  - The wheel zooms about the pointer, and the + and − buttons zoom and disable at the ends.
  - The first view fits the regions; `?region=<id>` opens that one.
  - Clicking a region opens the panel, Escape closes it, and the phone width shows the offcanvas.
  - Clicking a type and then a spot shows the ghost and the placement advice, then goes to the add page. Dragging a type does the same.
  - `?move=<id>` gives move mode.
  - "Back to my regions" works.
  - A region with no picture is striped.
  - Water is the staff blue.
- [ ] **Step 6: Commit** `refactor(map): the map's drawing part is its own file, shared next`.

### Task 6: The /map page

**Files:**
- Create: `Launcher/GridGate.Launcher/Pages/Map.cshtml`, `Launcher/GridGate.Launcher/Pages/Map.cshtml.cs`, `Launcher/GridGate.Launcher/wwwroot/js/world-map.js`
- Modify:
  - `Launcher/GridGate.Launcher/Program.cs`: add `/Map` to the `AllowAnonymousToPage` list and the `GuestSideOnly` public pages.
  - `Launcher/GridGate.Launcher/Pages/Shared/_GuestLayout.cshtml`: `#menu-map` after `#menu-home`, with icon `map-pin`.
  - `Launcher/GridGate.Launcher/wwwroot/css/guest.css`.
  - The six `ResidentText` files.
- Test: `Launcher/GridGate.Launcher.Tests/WorldMapPageTests.cs` (new); `ResidentTextScanTests` and the language-file tests stay green.

**Interfaces:**
- Consumes: `WorldMapAccess.CheckAsync`, the `/world/*` routes (Task 4), `GuestReturn` (Task 2), `startMapView` (Task 5), `FrontPageSnapshot.CurrentAsync` (`InWorld`, `RegionsOnline`).
- Produces: `MapModel` with:
  - `public bool ShowMap { get; }`;
  - `public IReadOnlyList<FrontNumber> Numbers { get; }` (`Home.InWorld` and `Home.RegionsOnline`, each left out when null; formatted `N0` in the current culture, like `FrontPageView`);
  - `public bool SignUpOpen { get; }`.
  - `OnGetAsync`: `NeedsTerms` → redirect to `/guest/terms?ReturnUrl=%2Fmap`.
- New `ResidentText` keys (English; the five translations written in the same plain tone):

| Key | English |
|-----|---------|
| `Layout.MenuMap` | World map |
| `Map.Title` | World map |
| `Map.SignInTitle` | Sign in to explore the map |
| `Map.SignInText` | See every region of our world and teleport straight into one. |
| `Map.Unavailable` | The map can't be shown right now. Try again in a minute. |
| `Map.NeedsJs` | The map needs JavaScript. |
| `Map.PeopleHere` | People here now: {0} |
| `Map.Teleport` | Teleport here |
| `Map.ViewerHelp` | Viewer didn't open? How to connect |
| `Map.Close` | Close |
| `Map.ZoomIn` | Zoom in |
| `Map.ZoomOut` | Zoom out |

  Reuse `Common.SignIn`, `Common.CreateAccount`, `Connect.Copy`, `Connect.Copied`, `Connect.CopySelected`, `Home.InWorld`, `Home.RegionsOnline` and `Home.NumbersLabel`.

- Markup:
  - **Signed out:** `section#map-invite` holds `h1` (Map.Title), `section.front-numbers#map-numbers` (the front page's number tiles), and `div.map-invite-box` with `h2` (Map.SignInTitle), `p` (Map.SignInText), `a#map-sign-in.btn` → `/guest/sign-in?returnUrl=%2Fmap`, and `a#map-join.btn` → `/signup` only when `SignUpOpen`. There is no `#world-map` element.
  - **Signed in:** `h1`, the zoom buttons `#zoom-in` and `#zoom-out` (aria-labels from Map.ZoomIn and Map.ZoomOut), and `p#map-unavailable` (hidden).
    - `div#world-map.region-map.world-map` carries `data-tiles="/world/tile"`, `data-regions="/world/regions"` and the translated words for the script: `data-people`, `data-copied`, `data-copy-selected`.
    - `div#world-card.world-card` (hidden) holds `button#world-card-close`, `h2#world-card-name`, `p#world-card-people`, `a#world-teleport.btn` (Map.Teleport), `code#world-link`, `button#world-copy` (Connect.Copy), and `a` → `/guest/connect` (Map.ViewerHelp).
    - `<noscript>` with Map.NeedsJs. Then `/js/map-core.js` and `/js/world-map.js`.
- `world-map.js`:
  - `startMapView` with `pollMs` 30000. `load` is a fetch with credentials; a 401 → `location.reload()`.
  - `keyOf` is the name; `markerTitle` is the name; `markerClass` gives `selected` for the open card.
  - `onRegions`: when `available` is false, show `#map-unavailable`; on the first list, fit all.
  - `onClick(point, region)`:
    - On a region, fill the card with `textContent`; people is `data-people` with `{0}` replaced, and the line is hidden when `people` is null.
    - The link is `teleport + encodeURIComponent(name) + "/" + mx + "/" + my + "/25"`. `mx = clamp(round((point.x - r.x) * 256), 0, w*256 - 1)`, and `my` the same with `y` and `h`.
    - Show the card. A click off any region closes it; so do Escape and the close button.
  - Copy uses `navigator.clipboard.writeText`, then the button says `data-copied` for 2 s. Without clipboard access, select the `code` text and say `data-copy-selected`.
- `guest.css`:
  - `.world-map`: background `var(--gg-bg)`, border `1px solid var(--gg-border)`, `min-height: max(24rem, calc(100vh - 16rem))`.
  - `.world-map .map-marker`: border `2px solid rgba(255,255,255,.8)`, background `rgba(255,255,255,.06)`.
  - `.world-map .map-marker.selected`: border-color `var(--gg-yellow)`.
  - `.map-marker-name`: bold, white, `text-shadow: 0 1px 3px #000`.
  - `.world-card`: absolute in the map's corner (top right; full width at the bottom below 576 px), `var(--gg-surface)`, border `var(--gg-border)`, z-index above the markers.
  - `#world-teleport`: the yellow button (`--gg-yellow` / `--gg-yellow-text`).
  - `.map-invite-box`: a `--gg-surface` card, centred, max-width 36rem.

- [ ] **Step 1: Write the failing tests** in `WorldMapPageTests`:
  - `Signed_out_sees_the_invitation`:
    - 200.
    - `#map-sign-in` href is `/guest/sign-in?returnUrl=%2Fmap`.
    - `#map-join` href is `/signup` while sign-up is open.
    - No `#world-map`.
    - With fake regions `Welcome` and `Sandbox`, neither name appears anywhere in the HTML.
    - `#map-numbers` shows the regions-online count.
  - `No_join_while_sign_up_is_closed`.
  - `A_resident_sees_the_map`: `#world-map` with `data-regions="/world/regions"` and `data-tiles="/world/tile"`; `script[src='/js/map-core.js']` before `script[src='/js/world-map.js']`; no `#map-invite`.
  - `Staff_see_the_map`: supervisor.
  - `Staff_outside_the_staff_networks_see_the_invitation`: caller `192.168.1.50`, `AdminNetworks = ["10.0.0.0/8"]`, staff cookie → `#map-invite`.
  - `A_resident_who_owes_the_Terms_goes_to_them_first` → 302 to `/guest/terms?ReturnUrl=%2Fmap`.
  - `Not_there_without_a_guest_side` → 404 on a Region Server.
  - `Menu_has_the_world_map`: on `/home`, `#menu-map` href is `/map`.
  - `The_page_speaks_the_residents_language`: with the `gg-lang=de` cookie, the `h1` is the German `Map.Title`.
- [ ] **Step 2: Run them.** Expected: FAIL (404).
- [ ] **Step 3: Implement** the page, the menu link, the resx keys in all six files, `world-map.js` and the CSS.
- [ ] **Step 4: Run** `--filter-class "*WorldMapPageTests"`, `"*ResidentTextScanTests"`, the language-file test class, `"*FrontPagePublicTests"` and `"*SignUpPageTests"`. Expected: PASS.
- [ ] **Step 5: Browser check** in the preview:
  - Signed out: the invitation in the dark look, on desktop and at phone width. The Sign in button comes back to `/map` after sign-in.
  - Signed in: the map with no console errors. The scratch launcher has no Robust, so `#map-unavailable` shows.
  - Then the Review Focus items 1, 2 and 5. Replace `window.fetch` in the page with a stub that returns three regions: `Welcome` (2×2), `Jack's <b>Place</b>` and `Сад`. Call `startMapView` again from the console the way `world-map.js` does. Check:
    - outlines and names (the `<b>` shows as text);
    - the card, the people line, and the hop link (escaped name, coordinates up to 511 on the 2×2);
    - Copy, Escape, and the close button;
    - a stubbed 401 reloads into the invitation.
  - Screenshot both views for the owner.
- [ ] **Step 6: Commit** `feat(map): the public world map page`.

### Task 7: Whole-branch check

- [ ] **Step 1:** Run the whole suite in the background: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release`. Expected: all pass, integration tests skipped locally.
- [ ] **Step 2:** Run `bash .github/scripts/check-old-name.sh` (see the CI secret-scan memory) and gitleaks 8.30.1 on the branch diff. Expected: clean.
- [ ] **Step 3:** Run the final whole-branch review by a fresh reviewer on the most capable model, against the spec and this plan. Fix what it confirms.
- [ ] **Step 4:** Finish with superpowers:finishing-a-development-branch.
