# Public world map

Date: 2026-11-20. Part of the public website restyle (the owner's local `docs/GOALS.md`, "Public website"), after the
shared look (PR 97) and the front page (PR 99).

## Intent

A world map on the public website, in the guest side's dark look, so residents can find the grid's regions and jump
straight into one. It works like the "World map" page on the site the owner is styling after, in style only: no names,
photos or words copied.

The owner's decisions:

- **Only signed-in people see the regions.** Visitors who haven't signed in see a page that invites them to sign in, with
  the live numbers. They see no regions.
- **Teleport links.** Clicking a region gives a button that opens the resident's viewer there.
- **No staff features.** No placing regions, no settings panel, no start/stop states.
- **Every region shows.** There is no hide setting. Hypergrid links aren't this grid's regions and don't show.
- **Approach A:** the staff map's drawing code is shared with the public map, not copied (see Approaches).

## Goals

- A page at `/map` in `_GuestLayout` and `guest.css`, reached from the ☰ menu ("World map") for everyone.
- Signed out: a heading, the live numbers (regions online and in-world now, from `FrontPageSnapshot`, each hidden when it
  can't be counted, as on the front page), and a box: "Sign in to explore the map", with **Sign in** and, when sign-up is
  open (`SignUpRules.IsOpen`), **Join**. Sign in comes back to `/map` afterwards (see "Coming back after sign-in").
- Signed in (a resident on the guest cookie, or staff on the staff cookie): the drag-and-zoom map, built from Robust's map
  pictures, region names on top, zoom buttons, first view fitted to all regions.
- Clicking a region opens a card: the region's name, how many people are there now, and **Teleport here**, a `hop://`
  link to the clicked spot. Under it, the same link as text with a **Copy** button, for viewers that don't open links.
- In all six resident languages (`ResidentText`).
- Works on a phone: the map fills the width; the card sits at the bottom of the map.

## Non-goals

- Hiding regions, private regions, or per-region "listed" settings.
- Search, region descriptions or photos, owner names, events, a region directory.
- Showing other grids' regions (hypergrid links) or teleporting to them.
- Anything on a Region Server or a Connected Server, where there is no guest side (`GuestSideOnly` gives 404).

## Anti-goals

- **Leaking the map to signed-out visitors.** The page, the region list and the map pictures must all refuse anyone who
  isn't signed in, not just hide the map on the page.
- **Breaking the staff map.** It must behave exactly as before after the drawing code moves.
- **Showing staff-only facts to residents.** No region states, programs, reserved spots, ports, paths, or region ids
  beyond what the card needs.
- **Letting the public map load the database.** Many residents polling must not mean many queries per second.

## Constraints

- CSP: `script-src 'self'; style-src 'self'; img-src 'self' data:`. No inline scripts or styles; scripts may set
  `element.style`. The page's words for the script come from `data-` attributes (translated on the server).
- Hand-written ES5 like `map.js`, no library.
- `ResidentTextScanTests`: no hard-coded English on resident pages. `LanguageFile.Check`: translations keep the English's
  `{0}` fill-ins.
- Map pictures come only through the launcher (never Robust's port in the browser), with the same z/x/y checks as
  `/api/map/tile`.

## Approaches

- **A. Share the drawing code (chosen).** Split `wwwroot/js/map.js` into a drawing part (tile grid, drag, wheel and button
  zoom, region outlines with names, fitting the view, the coordinate maths) and the staff part (placing, the panel, states,
  move mode, "Back to my regions"). The staff Regions page loads both; the public map loads the drawing part and its own
  small file. One copy of the drawing code, so fixes reach both maps. Cost: the staff code moves, and it has no automatic
  JS tests, so it's checked by hand in the browser before and after.
- **B. Copy the drawing part.** No risk to the staff map, but about 300 lines twice that drift apart. Rejected for that.
- **C. A fixed picture with clickable regions, no drag or zoom.** Quickest, but a bigger grid becomes tiny or cut off.
  Rejected.
- A map library (Leaflet) was dropped: it adds an outside dependency when the launcher's own code already does the job.

## How it works

### Who can see it

- `/map` is open to everyone (like `/Home`), with `GuestSideOnly`. The page decides what to show: the map when the
  request carries a valid resident sign-in (guest scheme, guest role) or staff sign-in (staff scheme, any staff role);
  the invitation otherwise.
- A resident who hasn't accepted the current Terms is sent to the Terms page first, as `TermsGate` does for guest pages.
- `/map` and `/world/` are added to the public paths in `Security/AdminNetworkFilter.cs`, like `/home`, so residents
  outside the staff network reach them.
- The two data links accept the same two kinds of sign-in, through a new policy that allows either scheme, and have
  `GuestSideOnly`. Anyone else gets 401 (no redirect: the script handles it by showing the sign-in box).

### The data links

- **`GET /world/regions`**: `{ regions: [{ name, x, y, w, h, people }], teleport }`.
  - From `IGridRegions` (registered with Robust, whichever machine runs them), hypergrid links left out.
  - `w` and `h` are the region's size in map squares. `GridRegions` reads `sizeX, sizeY` from Robust's regions table as
    well (metres / 256, at least 1).
  - `people` from `IOnlineUsers` grouped by region; left out (null) when the online list can't be read.
  - `teleport` is the start of the `hop://` link: `hop://{host}:{port}/` from `GridEndpoints.LoginUri(settings,
    HostAddress.Effective(settings.Hostname))`.
  - When the regions can't be read: `{ regions: [], available: false }`, and the page says the map can't be shown
    right now.
  - Kept for 30 seconds and shared by every caller, so polling residents cost at most one database read each per 30 s
    for the whole site. The page asks again every 30 s.
- **`GET /world/tile/{z}/{x}/{y}`**: the same picture and checks as `/api/map/tile`, through shared code (one helper both
  endpoints call). `Cache-Control: private, max-age=300`.
  - No shared server-side copy of the pictures (my call). Only signed-in people can ask for them, each browser keeps its
    own copy for 5 minutes, and Robust serves them as ready-made files, so the cost is small. A shared picture cache
    would need its own size limit and a way to refresh a picture after a region starts. It can be added later if a
    busy grid needs it.

### The teleport link

- `hop://{host}:{port}/{region name, URL-escaped}/{x}/{y}/{z}` with `x` and `y` the clicked spot in metres inside the region
  (0 to size × 256), and `z` 25. The viewer puts the avatar on the ground if that is higher.
- The resident's viewer must be set up for this grid; the How to connect page already explains that, and the card links
  to it in small print ("Viewer didn't open? How to connect").
- A region's own access rules still apply: OpenSim refuses a teleport into an estate the resident may not enter.

### Coming back after sign-in

Today `Pages/Guest/SignIn.cshtml.cs` and `SignInCode.cshtml.cs` always go to `/guest` afterwards. The map's Sign in button
links to `/guest/sign-in?returnUrl=/map`. The address is carried through the code step (and two-factor, if asked) and
used only when it is a local path on this site (`Url.IsLocalUrl`, starting with a single `/`). Anything else goes to
`/guest` as today. This is my call, kept small: only the map uses it for now, but any page may.

### The page's look

```
┌ WORLD MAP ────────────────────────────── [+] [−] ┐
│  ░░░░▓▓▓▓░░░░                                    │
│  ░░ Sandbox ░░   ┌───────────────────────┐       │
│  ░░░░▓▓▓▓░░░░    │ Welcome Island        │       │
│        ▓▓▓▓▓▓▓▓  │ 3 people here now     │       │
│        ▓ Welcome │ [ TELEPORT HERE ]     │       │
│        ▓▓▓▓▓▓▓▓  │ hop://…  [Copy]       │       │
└──────────────────┴───────────────────────┴───────┘
```

Region outlines in the guest look: a thin bright border, the name in bold white with a dark shadow, the clicked region's
border yellow (`--gg-yellow`). The card is a `--gg-surface` box with the yellow button. Water where Robust has no picture
is the page's dark navy.

### Sharing the drawing code

- `map-core.js` (new): `startMapView(element, options)` with the view, the tiles, pan, zoom (wheel and the two buttons),
  outlines with names (`w` × `h`), `fitBox`, `toWorld`, and hooks: `onClick(point, region)`, `markerClass(region)`,
  `onRegions(list)`. It takes the tile and region URLs and the poll interval from `options`.
- `map.js` keeps everything staff-only and calls `startMapView`. Its behaviour, markup and endpoints don't change.
- `world-map.js` (new): the card, the hop link, Copy, and the 401 → sign-in box.
- Staff check: before and after the split, in the browser preview: pan, zoom, wheel zoom, placing by drag and by click,
  move mode, opening and closing the panel, the phone offcanvas, striped regions without a picture, tiles refreshing after
  a region starts.

## Tests

- `/map` signed out: the invitation and the numbers, no map element, no region names. Join only when sign-up is open.
- `/map` as a resident and as staff: the map element with its data links. A resident who hasn't accepted the Terms goes
  to Terms first.
- `/map`, `/world/regions` and `/world/tile` on a Region Server or Connected Server: 404.
- `/world/regions`: 401 signed out; for a resident, own regions only (no hypergrid links), sizes, people counts; the
  `teleport` start from the grid's login address (host and Robust port; a changed hostname or port shows at once); the
  unavailable case;
  the 30-second sharing (a second call inside 30 s doesn't read again; `f.Time.Advance` past it does).
- `/world/tile`: 401 signed out; the same bad-z/x/y 400s as the staff tile link.
- `/map` and `/world/regions` reachable from outside the staff network (`AdminNetworkFilter`).
- Sign in from the map comes back to `/map`; a `returnUrl` to another site, `//host` or `/\host` goes to `/guest`.
- `GridRegions` reads sizes (integration test, MariaDB in CI).
- `ResidentTextScanTests` and `LanguageFile.Check` pass with the new keys in all six languages.
- The ☰ menu has "World map" for everyone.

## Left to the builder

- The exact names of the new policy, snapshot class and resource keys.
- How `map-core.js` exposes its hooks, as long as the staff map behaves the same.
- The card's exact layout and where it sits on a phone.
- Whether the 401 from the data links clears the page to the invitation box or reloads `/map`.
