# Featured places: a directory of places residents put in search

The "Featured" page from the owner's GOALS.md public-website list. Decided with the owner on 2026-11-20/21; the rest was
decided by the designer overnight while the owner slept (marked *designer's call*) and is the owner's to correct.

## Why

The front page's "Featured places" are picked by the owner. This page is different: it lists the places **residents
themselves chose to show**, by ticking "Show in search" in About Land, so a newcomer can browse what the grid has before
making an account, and a resident can find somewhere to go and teleport straight in.

## What the owner decided

- **A directory of the parcels residents put in search** (not a bigger copy of the front page's list).
- **Everyone can see it**, signed in or not. Unlike the world map, only listed parcels show, never the whole grid.
- **Pictures:** each place's own snapshot (About Land), with the region's map tile when there is none.
- **Visits:** counted per parcel by the regions themselves, filling OpenSim's own parcel "Traffic" number (always 0
  today), which the directory reads and residents also see in About Land.

## Not in this change (left for the owner)

- **Parcel snapshots are not shown yet; every card shows its region's map tile.** Snapshots are JPEG 2000 textures. The
  converter OpenSim ships (`bin/CSJ2K.dll`) only returns `System.Drawing` images, which the launcher doesn't use (it
  draws with SkiaSharp and runs without libgdiplus). The two ways to convert are the owner's to pick: add the CoreJ2K
  NuGet package (a new third-party dependency) to the launcher, or a small Robust handler that converts with the
  CSJ2K and System.Drawing Robust already has. The page is built so the snapshot slots in where the tile is now.
- Showing other grids' places (hypergrid links), events, classifieds, or a staff editor for this page.
- In-world search (the viewer's Search window) reading the same directory.

## The page

`/featured`, on the public side (`_GuestLayout`), in the ☰ menu as **Featured** after Map. *Designer's call* for the
address and title.

```
FEATURED PLACES                                   ← h1
Places residents chose to show. Teleport straight in with your viewer.
──────────────────────────────────────────────────
 24 places │ 1,312 recent visits │ 7 categories │ 9 regions
──────────────────────────────────────────────────
[ search…        ] [ Category ▾ ] [ Rating ▾ ]* [ Most visited ▾ ] [ SHOW ]
[picture] [picture] [picture]
 Name      Name      Name
 Region · Shopping            (Moderate / Adult badge)
 two-line description…
 213 recent visits
 [ TELEPORT ]
            ‹ Previous   Page 2 of 3   Next ›
```
\* the rating choice shows only to signed-in residents and staff (see Ratings).

- **Stats row:** places listed, recent visits (the sum of the shown places' traffic), categories in use, regions with
  places. A tile whose number is 0 or unknown is hidden; with none left, the row is hidden (as on the front page).
- **Filters:** a plain GET form (works without script): search words (matched, ignoring case, in the place's name,
  description and region name), category (each with its count, only categories in use), rating (signed-in only), sort
  (most visited, the default, or name). 24 cards a page with Previous/Next links. *Designer's call.*
- **Card:** picture, name (the parcel's name; "Unnamed place" when empty), region and category, a rating badge for
  Moderate and Adult only, the description clipped to three lines, "N recent visits" when N ≥ 1, and **Teleport**.
- **Teleport** is a `hop://host:port/Region/x/y/z` link built like the map's (`WorldMapApi.TeleportStart`), to the
  parcel's landing point when it has one (landing type "landing point" and a point set), otherwise `hop://host:port/Region`
  so the region picks. A line under the stats says teleport links open the viewer.
- **Empty:** with no listed places, the page says how to list one ("In your viewer: About Land → Options → Show in
  search"). When the databases can't be read, it says the directory isn't available right now. Never a "0" or a broken
  picture.

## Which places show

- Parcels (`opensim.land`) with the **Show in search** flag (`ParcelFlags.ShowDirectory`, `0x1000`),
- on a region of this grid that is **online now**: registered in Robust's `regions` table and not a hypergrid link
  (`IGridRegions`). A stopped region's places drop out, so every Teleport link can work.
- A region's own access rules still apply at teleport time, as on the map.

## Ratings (*designer's call*)

- A place's rating is its region's maturity (`opensim.regionsettings.maturity`: 0 General, 1 Moderate, 2 Adult), raised to
  **Adult** for a parcel in the Adult category (2) and to at least **Moderate** for a parcel with "Mature content"
  (`ParcelFlags.MaturePublish`, `0x40000`).
- **Visitors who aren't signed in see General and Moderate places only.** Signed-in residents and staff (the map's
  `WorldMapAccess` rule) see all three and can pick one rating. Adult places are never in a stranger's page, its counts
  or its pictures.

## Categories

OpenSim's 14 parcel categories (`ParcelCategory`), each with its own words in the six languages: None (shown as no
category, and never counted), Grid location (OpenSim's "Linden Location"; *designer's call* for the name), Adult, Arts &
culture, Business, Educational, Gaming, Hangout, Newcomer friendly, Parks & nature, Residential, Shopping, Stage, Other.

## Visits: the region's traffic counter

A new region add-on, `addon-modules/GridGateTraffic`, beside GridGateMoney, so no OpenSim file changes.

- It is OpenSim's `IDwellModule` (`[Dwell] DwellModule = GridGateTraffic`, written by `GridConfigWriter` for every
  region), replacing `DefaultDwellModule`, so About Land's Traffic and the land table's `Dwell` column show its number.
- **A visit:** a root avatar arriving on a parcel (`EventManager.OnAvatarEnteringNewParcel`). Not counted: NPCs, child
  agents, the parcel's owner (or for a group-owned parcel, nobody is excluded), and any parcel while the estate has
  "Block dwell" set. Each avatar counts **once per parcel per UTC day** (kept in memory; a restart may count someone twice
  that day). *Designer's call.*
- **Fading:** the number is "recent visits": on each visit and on an hourly pass it is multiplied by `exp(-Δt / 30 days)`
  and a visit adds 1, so a steady stream of N visits a day settles near 30 N, and a place nobody visits fades toward 0.
  Δt counts only time the region runs (OpenSim keeps no "last changed" time for the column). *Designer's call.*
- **Saving:** a counted visit saves that parcel at once (`SimulationDataService.StoreLandObject`); the hourly pass saves
  each parcel whose whole number changed, and closing the region saves the rest. The column is an integer, so the stored
  value is rounded; the module keeps the exact value in memory while it runs.
- Parcel joins and splits keep OpenSim's own handling of `Dwell`.

## Constraints

- **Public traffic can't load the grid:** the directory is built at most once a minute and shared by every visitor, with
  short database timeouts (as `FrontPageSnapshot` and `WorldMapSnapshot`). Map tiles go through the front page's tile
  cache (`FrontMapTiles`, generalised to any region), served at `/featured/map/{regionId}.jpg` **only for a region that
  has a place on the directory the asker may see**; anything else is 404.
- **Words:** every visitor-facing word in `ResidentText`, all six built-in languages. Residents' own text (names,
  descriptions) is shown as plain text, never as HTML.
- **Look:** the public look (`guest.css` variables, `_GuestLayout`), cards like the front page's.

## Anti-goals

- A stranger seeing an Adult place, or any parcel its owner didn't put in search.
- A page or picture that reveals a region the directory doesn't show.
- Visitors driving database load.
- A traffic number that one avatar can pump by walking in and out.

## Left to the builder

The exact markup and CSS within the public look; how the add-on's counting is split into testable pieces; the test
doubles.
