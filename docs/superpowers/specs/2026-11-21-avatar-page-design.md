# The Avatar page

Date: 2026-11-21. Status: the owner chose the purpose of the Avatar tab ("show their avatar") and handed over the rest ("you run
with it"); every other call is the designer's and marked *designer's call*.

## Why

The website list asks for an "Avatar page": one page with tabs World Map, Avatar, Inventory, Profile, Download. Today a signed-in
resident lands on `/guest`, a plain list of connection steps and links. The Avatar page makes that their home on the website, in
the public look, with their avatar at the top.

## Goals

- `/guest` becomes the **Avatar** tab: the resident's avatar (profile picture, name, member since, in-world now or not) and
  what they are wearing, read from the grid.
- A tab bar on the five pages, so they read as one page: **Avatar** `/guest`, **World Map** `/map`, **Inventory**
  `/guest/inventory`, **Profile** `/guest/account`, **Download** `/guest/connect`. Shown only to a resident signed in at the guest
  door.
- A menu link "Avatar" (to `/guest`; a visitor is asked to sign in first).

## Non-goals

- A 3D view of the avatar. It would need the viewer's avatar mesh files and a WebGL renderer, and most avatars today are mesh
  bodies (attachments) such a view wouldn't draw. *Designer's call.*
- Changing the look, taking items off or browsing the whole inventory on the website (the Inventory tab stays the backup page).
- Showing anything about anyone else: every tab is the signed-in resident's own.

## Anti-goals

- A tab that leads nowhere: Inventory shows only where that page is open to the resident (the same rule as its link today).
- A picture route that reaches another account's texture: the avatar picture route answers only with the asker's own.

## Approaches

1. **Tab bar as a shared partial on the existing pages (chosen).** Each tab stays its own page and address, the bar marks the
   current one. Nothing moves, no script, every page keeps its tests and links.
2. One new page with client-side tabs loading the others. Needs script and a second copy of each page's forms.
3. Only restyle `/guest` and link the others. Misses "one page with tabs".

*Designer's call*: 1.

## The Avatar tab (`/guest`)

- **Avatar card**: the profile picture (`/guest/avatar.jpg`, 4:3; else a plain picture with "Choose a profile picture in your
  viewer's profile and it shows here."), the name as the big title, "Member since …", and "In-world now in {region}" or "Not
  in-world now" (their own presence; a region on another computer reads as in-world without a name). A link "See my public
  page" when they are on the Residents page, else "Show me on the Residents page" (to the Account page switch).
- **What you're wearing** (Robust's stored appearance, `Avatars`, as the starter-avatar check reads it; names and creators from
  their inventory): four groups, each left out when empty: **Body** (shape, skin, hair, eyes), **Clothes** (the other wearable
  types, each with its type: "Shirt", "Alpha", …), **Attached** (attachments), **HUDs** (attachment points 31–38). Each line:
  the item's name and "by {creator}". An item no longer in their inventory reads "An item no longer in your inventory".
  No stored look yet: "Your avatar's look is saved the first time you go in-world." Database down: "Your avatar's look can't be
  read right now."
- **Get in-world**: the login address and a link to the Download tab (the full steps live there).
- **Your things**: the links `/guest` has today (my regions, upload models, my coins, two-factor), each only when shown today,
  and Sign out.

## The tab bar

A partial in `_GuestLayout`, drawn at the top of the page when the page sets `ViewData["AvatarTab"]` and a resident is signed in
at the guest door. Links, not buttons; the current one has `aria-current="page"`. It scrolls sideways on a narrow screen.

## Pictures

`/guest/avatar.jpg`: GuestPolicy; the signed-in resident's own profile picture through `ParcelPictures`; `Cache-Control:
private`. 404 when they have none.

## Words

All in `ResidentText`, six languages; wearable types use the viewer's names.

## Left to the builder

Class names, CSS, test layout.
