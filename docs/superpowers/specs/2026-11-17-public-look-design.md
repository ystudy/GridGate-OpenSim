# Public side: shared look (design)

Date: 2026-11-17. Size: a small change. First step of the public-website list in the owner's goals.

## Intent

The owner wants the public side of the grid website to look like alifevirtual.com: dark, bright words, bold headings, a real menu and footer. This step builds only the shared frame and colours every public page sits in. The front page, shop, market, featured, profiles, map and avatar page each get their own design later.

We copy the style only: never Alife's name, photos or words.

## Approaches

- **A. A separate public stylesheet (picked by the owner).** A new `wwwroot/css/guest.css` loaded only by `_GuestLayout`. Staff pages and `site.css` are untouched. Colours are CSS variables at the top of the file, so an owner colour picker can be added later.
- B. Restyle `site.css` for everything. Rejected: the staff work pages (tables, forms) were tuned for readability and would get worse.
- C. Owner theme picker now. Deferred: three times the work, and it fits on top of A later.

## Goals

- Every page using `_GuestLayout` gets the new look. That covers `/home`, `/signup`, the legal pages, `/coins`, `/support/*` and `/guest/*`.
- **Always dark** on the public side: a dark navy page, near-white text, light grey for secondary words (high contrast, as on Alife). `theme.js` is not used on `_GuestLayout`; the layout sets `data-bs-theme="dark"` itself.
- **Type:** the same system fonts. Headings are heavy (800), UPPERCASE with a little letter spacing. Body text stays normal case.
- **Buttons:** yellow for the main action (dark words), blue for the second. Outline buttons are white.
- **Cards:** dark blue panels with a soft border and glow; rounded corners.
- **Top bar** on every public page:
  `[logo] Grid name ......... LOGIN  REGISTER  ☰`
  - The logo is the owner's logo from the splash editor (`ThemeStore`). With no logo, the grid name alone shows.
  - A signed-in resident sees their avatar name (linking to `/guest`) and Sign out instead of Login and Register.
  - A staff member viewing the public side keeps the "Back to staff" button.
  - The ☰ menu lists only pages that exist today: Home, How to connect, Help, Reset password, plus the language picker. New pages join it as they are built.
  - The menu works without JavaScript and by keyboard.
- **Footer** on every public page: the written legal pages (as now), then "© Grid name. All rights reserved.", then a small "Staff sign in" link. The "Staff sign in" button leaves the front page's top bar.

## Non-goals

- No new pages, and no content changes to existing pages beyond what the frame needs.
- No changes to staff pages, `site.css` rules used by staff, or the viewer splash page (`_SplashLayout`).
- No owner colour picker (added to the goals list for later).

## Anti-goals

- Anything harder to read than today: contrast must meet WCAG AA for body text and buttons.
- Right-to-left languages breaking: the frame must work with `bootstrap.rtl.min.css` and `dir="rtl"`.
- New visitor-facing words outside the localizer: every new string goes through `ResidentText` (en, de, es, fr, pt, ru).

## Left to the builder

- The exact colour values and glow, within the brief above.
- How the ☰ menu is built (for example a `<details>` element), as long as it needs no script and works by keyboard.
- Where the resident's name comes from for the top bar.
- Adjusting existing guest page markup only where the new frame needs it (for example the front page's "Staff sign in" button moving to the footer).
- Tests: follow the existing page tests for `_GuestLayout` and Home.
