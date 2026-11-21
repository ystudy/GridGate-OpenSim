# Residents' public profiles

Date: 2026-11-21. Status: the owner handed over the design ("show newcomers real people live here, you run with it and make it
happen"), so every call below except the purpose is the designer's and marked *designer's call*.

## Why

The public website is the grid's shop window (front page, Featured places, map). A newcomer deciding whether to join wants to see
that real people live here. The owner's page list named "photo, bio, stats, achievements, login-streak leaderboard"; asked what
the page is for, the owner chose **showing newcomers real people live here** over giving residents something to show off.

## Goals

- `/residents`: a public directory of residents who chose to be on it: big title, one line, stats row, search, cards.
- `/residents/{first}/{last}`: one resident's public profile: photo, name, member since, About, interests, picks.
- Residents decide: nobody is listed until they turn it on themselves.
- Staff can take a profile off the page (moderation), and the resident can't put it back.

## Non-goals

- Login streaks, achievements, leaderboards (they serve "something to show off"; the grid doesn't track streaks, so they would
  start from zero; a later design if wanted).
- Editing the profile on the website: residents edit it in their viewer, as today.
- Online status, last seen, groups, partner, "first life" text and picture (*designer's call*: presence and real-life details are
  the privacy risks of a public page; none is needed to show real people live here).
- Hypergrid visitors' profiles (they have no account here).
- Places on Region Server computers (no public side there).

## Anti-goals

- Publishing anyone who didn't ask: a resident must turn it on, on their own Account page.
- A page that lets a stranger follow someone: no presence, no "where are they now".
- A way to reach any texture or any account by id: pictures answer only for what the page shows.

## What the grid has

- Robust's database: `UserAccounts` (name, `Created`, `UserLevel`), `userprofile` (`profileImage`, `profileAboutText`,
  `profileWantToText`, `profileSkillsText`, `profileLanguages`, `profileURL`, …), `userpicks` (name, description, snapshot,
  `simname`, `posglobal`, `enabled`, `toppick`, `sortorder`). OpenSim adds an empty `userprofile` row the first time anyone opens
  a profile.
- `profileAllowPublish` exists but **OpenSim never saves it**: the row is created with false and neither UPDATE writes it, so the
  viewer's "show in search" can't be used as consent. Same for `profileMaturePublish`.
- The launcher already converts textures to JPEG through Robust (`ParcelPictures`, `POST /gridgate/j2k`).

## Approaches

1. **Opt-in on the website Account page (chosen).** A switch "Show my profile on the Residents page", kept by the launcher like
   the resident's language (`<data>/guests/public-profiles.json`). Works with every viewer, says plainly what becomes public, no
   OpenSim change.
2. Fix OpenSim to save `profileAllowPublish` and use the viewer's checkbox. Three database back-ends to change, every existing
   resident reads false until they find a checkbox some viewers label for in-world search only; consent would be unclear.
3. Everyone with a filled-in profile, with an opt-out. Fills the page on day one, but publishes people to the web who never asked:
   ruled out by the anti-goals.

*Designer's call*: 1, because consent is the point of the anti-goals and it needs no OpenSim edit.

## Who shows

A resident shows when all hold:

- their account is a person on this grid (`WorldCounter.Person`: not the services account, not a starter-avatar model) with
  `UserLevel >= 0` (a blocked account never shows);
- they turned the switch on, and staff haven't taken them off.

An empty profile still shows (name, member since, a plain picture). The switch's state per account: **off** (default), **on**,
**held** (staff took it off: the resident sees why and can't turn it on; staff can release it, which sets it back to off).

## The directory page

- Look as Featured: `_GuestLayout`, big title "Residents", one line, stats row, a GET form (search by name or About text; sort
  "newest members" (default) or "name"), 24 cards a page, Previous/Next.
- Stats row (each tile hidden at 0): registered residents and active in the last 30 days (the front page's live numbers), profiles
  on this page, in-world now. These are grid totals, not about anyone.
- Card: the profile picture (else a plain card), the name, "Member since November 2026", the first 140 characters of About.
  The whole card links to the profile.
- Empty states: database down; nobody listed yet ("Residents choose to be shown here from their Account page." plus a link to
  sign in); no match.
- Everyone may see it; no Adult split: OpenSim keeps no maturity for profiles, and the resident chose to publish
  (*designer's call*). Staff moderation covers abuse.
- `<meta name="robots" content="noindex">` on both pages (*designer's call*: residents agreed to the grid's website, not to
  search engines remembering their name).

## The profile page

- Big picture (512 px JPEG), name as the title, "Member since …", About (line breaks kept), then when filled in: "I want to",
  "Skills", "Languages", a "Web page" link (only http/https; `rel="nofollow ugc noopener"`, opens in a new tab).
- Picks: enabled ones, top picks first then `sortorder`, at most 10: snapshot, name, description, region name; a Teleport link
  (`hop://`, to the pick's spot) when the region is one of this grid's own regions online now.
- A resident who isn't shown (off, held, unknown name) gets 404, the same answer as no such resident.
- Back link to `/residents`.

## Pictures

`/residents/picture/{principal}.jpg` (profile picture) and `/residents/pick/{pick}.jpg` (pick snapshot), through
`ParcelPictures` (same limits, cache and converter). 404 unless the resident is shown and the texture is theirs. Public cache
headers (5 minutes).

## The Account page switch

A card on `/guest/account`: "Residents page", a checkbox "Show my profile on the Residents page", the line "Anyone can see your
name, profile picture, About, interests and picks there. You change them in your viewer's profile.", Save, and when on a link to
"See my page". When held: "Staff took your profile off the Residents page." and no checkbox.

## Staff

On the staff person page (`/people/person/{id}`): a line "Residents page: shown / not shown / taken off by staff" and a button
"Take off the Residents page" (when shown or off) or "Allow again" (when held); each is written to the audit log.

## Load and safety

- The directory is built at most once a minute and shared (as `FeaturedDirectory`); reads the accounts and profiles of only the
  residents who turned it on (ids in chunks of 500), then their picks, as robustuser, 3 s timeouts, failures give "unavailable".
- Every text is HTML-encoded by Razor; About is shown as text with line breaks, never as HTML. Search is cut to 100 characters.
- Not on a Region Server or Connected Server (`GuestSideOnly`, like Featured); open to every address (`AdminNetworkFilter`).

## Menu and words

- Menu link "Residents" (`menu-residents`, icon "user") after Featured. *Designer's call*: "Residents", not the list's "Profile",
  which reads as "my profile".
- All words in `ResidentText` in the six languages.

## Left to the builder

Class and file names, CSS, the exact cut of About on cards, test layout.
