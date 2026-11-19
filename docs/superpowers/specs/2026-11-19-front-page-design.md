# Public front page (design)

Date: 2026-11-19. Size: a project. The second step of the public-website list in the owner's goals, after the shared look (spec `2026-11-17-public-look-design.md`, merged in PR 97).

## Intent

The front page (`/home`, also shown at `/` to visitors who are not staff) becomes the grid's shop window for newcomers. It is styled after a bold marketing page: a big in-world photo with a headline, live grid numbers, featured places and news. Today it is two sign-in cards.

**What a visitor should do:** make an account and get in-world. Signing in comes second; the top bar already has Sign in.

GridGate is installed by many grid owners, so every section has to work for a small or new grid as well as a busy one, and the owner chooses what feeds it.

We copy the style of the reference site only: never its name, photos or words.

## The page, top to bottom

```
[ front page photo, darkened so the words read ]
   GRID NAME                                   ← the grid name, huge, uppercase
   Home wording (Home.Intro)                   ← the owner's own line, per language
   [ CREATE AN ACCOUNT ]  [ HOW TO CONNECT ]   ← yellow main, blue second
──────────────────────────────────────────────
 12 in-world now │ 9 regions │ 1,204 avatars │ 87 active this month
──────────────────────────────────────────────
 FEATURED PLACES   [photo card] [photo card] [photo card]
 NEWS              newest items first
──────────────────────────────────────────────
 READY TO JOIN?    [ CREATE AN ACCOUNT ]
 (the written legal pages stay in the footer, as on every public page)
```

### 1. The top section

- **Photo:** the front page's own photo, uploaded in the new staff page below. It covers the full width behind the words, cropped to fill and darkened enough that white words meet WCAG AA. With no photo, the section uses a dark navy gradient made from the shared look's colours, so it never looks broken.
- **Headline:** the grid name.
- **Line under it:** the existing `Home.Intro` wording, which the owner already edits per language on the Wording page.
  - Its built-in text changes in all six languages, because today's default ("Sign in with your avatar to see how to connect…") contradicts the new buttons. The new English default is "Make an avatar and explore {grid} with us." An owner who has already reworded it keeps their own line.
  - *Designer's call.*
- **Buttons:**
  - While sign-up is open (`SignUpRules.IsOpen`): **Create an account** (yellow, `/signup`) and **How to connect** (blue, `/guest/connect`).
  - While sign-up is closed: **Sign in** (yellow, `/guest/sign-in`) and **How to connect** (blue).
  - A signed-in resident sees **Your account** (yellow, `/guest`) instead of Create an account or Sign in.

### 2. Live numbers

Up to four tiles: **In-world now**, **Regions online**, **Registered avatars** and **Active this month** (signed in during the last 30 days).

- The owner turns each tile on or off. All four are on by default.
- A number the database can't give right now (null) hides its own tile. If no tile is left, the whole row is hidden.
- Sources:
  - In-world now: `OnlineUsers`.
  - Regions online: the grid's own regions in Robust's `regions` table, **without hypergrid links** (flag 512, which `WorldCounter` already leaves out).
    - `GridRegions` (which the splash page's status block uses) counts links too, so the front page needs its own filtered count.
    - The splash page's number stays as it is, because changing the splash page is a non-goal; aligning it is a possible later fix.
    - *Designer's call.*
  - Registered avatars: the residents total `WorldCounter` already counts.
  - Active this month: a new count of residents who signed in during the last 30 days, a sibling of `WorldCounter`'s signed-in-this-month query. That one uses the calendar month, so it can't be reused as is.
- Public traffic must not drive heavy queries: the numbers are built at most once a minute and shared by all visitors (the same pattern as `SplashSnapshot`), each query has a short timeout, and only these four queries run (not the whole dashboard).

### 3. Featured places

The owner picks one source:

| Source | What shows |
|---|---|
| **Same as the splash page** (default) | The places in the *published* splash design's Places blocks, top to bottom and then left to right as the splash page shows them, with their pictures and lines. |
| **Its own list** | Places picked on the Front page staff page: a region, one line per language and a picture. The picture is either uploaded or made from the region's map tile, as in the splash editor. |
| **Automatic** | The grid's own running regions (never hypergrid links), at most 6, the ones with the most people first, then by name. Each card shows the region's map tile and its name and has no line. A region the grid has no map tile for gets the blank card. |

- Each card shows the picture, the region name and the line, if there is one, in the visitor's language, falling back to English.
- Cards have no teleport links yet; nothing in GridGate makes them.
- If the chosen source has no places, the whole section is hidden. For example: no published splash places, an empty own list, or no regions running.

### 4. News

The owner picks one source:

| Source | What shows |
|---|---|
| **Same as the splash page** (default) | The news items of the published splash design, newest first, without the hidden ones. |
| **Its own list** | News items edited on the Front page staff page: a date, the text per language and a hidden switch. |

- At most 5 items show, newest first, rendered through `LegalMarkdown` as on the splash page, in the visitor's language with English as the fallback. Their headings start at h3, under the section's own h2.
- If there's nothing to show, the section is hidden.

### 5. Ready to join

- A closing band with a heading ("Ready to join?") and the same main button as the top section: Create an account, Sign in, or Your account.
- It is hidden for a signed-in resident, who has nowhere further to go from here.

## The staff page: "Front page"

A new staff page at `/front-page`. It is listed in the staff menu next to "Splash page" and on the Guest pages hub (`/guest-pages`), with the same access rules as the splash editor: the admin role. Like the splash editor, it is hidden on plans with no public side (the `GuestSideOnly` filter).

It is an ordinary form page, not a canvas editor, in four parts:

1. **Photo:** upload, replace or remove the front page photo, with a small preview.
2. **Numbers:** the four on/off switches.
3. **Featured places:**
   - The source choice: Same as the splash page, Its own list, or Automatic.
   - The own-list editor:
     - add a place by picking a region from the grid's regions (each region once);
     - write its line in each offered language;
     - upload a picture or make one from the map tile;
     - move it up or down, or remove it.
   - The own-list editor is shown, and kept, whichever source is chosen, so switching back never loses the list. Its pictures show there through a staff-only address (`/api/front/pictures/{name}`, admin role), since the public one answers only while the list is showing.
4. **News:**
   - The source choice: Same as the splash page, or Its own list.
   - The own-list editor: add an item (date and text per language), hide or show it, or remove it.

Changes are live once saved; there is no draft. A "See the front page" link opens `/home`.

## Constraints

- **Pictures:**
  - Front page pictures (the photo, own-list place pictures and own-list map-tile pictures) live in their own `PictureStore`, keyed `"front"`. They are checked and redrawn like every upload.
  - Two separate "in use" sets (*designer's call*):
    - **Served:** pictures are served at `/front/images/{name}` only while the page **shows** them: the photo, plus the own-list place pictures while the places source is "Its own list". That works like `/welcome/images`, which serves only pictures the published splash uses.
    - **Kept:** the cleanup counts every picture the saved settings **mention** as in use, including an own list that isn't showing right now. Switching the source away and back, even after months, never loses a picture.
  - A cleanup pass removes front pictures that no saved setting has mentioned for 30 days, like `SplashPictureCleanup`. The splash cleanup must never see or remove front pictures, and the front cleanup must never touch splash pictures.
- **Automatic map tiles:** served through a public endpoint that returns the map tile of a region **only while that region is on the automatic list** (so never a hypergrid link's tile). They are fetched from Robust and cached for the same length of time as the stats build; visitors who come while a tile is missing or expired share one fetch. A visitor can't use the endpoint to fetch any other tile.
- **Storage:** the front page settings live in `Data/front/front.json` (a `JsonStateFile`, like `ThemeStore`), with owner-only file permissions like the other data folders. They are read once and kept in memory until the next save, since every public visit needs them.
- **Words:**
  - Every visitor-facing word goes through `ResidentText`, in all six built-in languages.
  - The owner's own text (place lines, news) is stored per language, like the splash design's text.
  - Staff page words stay in English, like the other staff pages.
- **Look:** built on the shared look (`guest.css` variables and classes). It must work right-to-left and at 375 px wide, and every word meets WCAG AA, including over the photo.
- **Wording:** `Home.Intro` stays the owner's line (Wording page). The sign-in card's strings are no longer used on the front page; remove them from `Home.cshtml` only.
- **Old behaviour kept:**
  - `/` still shows the front page to everyone who isn't staff, and staff still go to their own start page.
  - Region Server and Connected plans still return 404 for `/home`.

## Approaches

- **A. Reuse the splash editor's content.** Places and news come from the published splash design; the only new thing is a front page photo. It is cheap and the two pages always match, but the website can't feature anything different.
- **B. A separate front-page editor.** It has its own photo, places and news. That gives full control, but the owner keeps two copies up to date.
- **C. Fully automatic.** Running regions with map tiles and a fixed photo. There's nothing to edit, but there's no curation, and map tiles look flat.

**The owner chose a hybrid of all three:** a choice per section, with "Same as the splash page" (A) as the default, "Its own list" (B), and "Automatic" (C) for places.

## Goals

- A newcomer lands on a page that shows the world (photo, places, live numbers) and makes **Create an account** the obvious next step.
- The owner decides what each section shows, at the cost of one extra staff page. If they do nothing, the page shows the splash content they already publish.
- Nothing on the page ever looks broken or empty: a missing photo falls back to the gradient, and an empty section or an unknown number is hidden.

## Non-goals

- Events, region-rental offers, total visits and teleport links. None of that data exists yet; each comes with its own later design (search service, shop and money review, visit tracking).
- A draft or preview mode for the front page.
- Changing the viewer splash page or its editor, apart from the shared reading of published places and news.
- The other public pages on the list (shop, market, featured, profiles, map, avatar page).

## Anti-goals

- **A front page that tells strangers more than the owner chose to show.** Automatic places expose running region names and map tiles; that's acceptable only because the owner picks Automatic. No region or tile is reachable that the page doesn't show.
- **Public visits that can slow the grid:** at most one stats build a minute, with short timeouts. Map tiles are cached.
- **A section or number that shows "0", is empty, or has a broken image** because data is missing.
- **Deleting a picture the other page still uses:** the splash and front cleanups each keep to their own store.

## Left to the builder

- The exact markup, card sizes, gradient and darkening of the photo, within the shared look and the contrast rule.
- How the own-list editors are built: plain forms are enough. A little script for moving items up and down is fine, but the page must still work without it.
- Whether the four numbers share a snapshot class with `SplashSnapshot` or get their own, as long as public traffic causes at most one build a minute.
- How the front page's model and store are split into classes and files.
- Tests: follow the existing tests for the splash pictures, `SplashSnapshot`, the Home page and `PublicLookTests`.
