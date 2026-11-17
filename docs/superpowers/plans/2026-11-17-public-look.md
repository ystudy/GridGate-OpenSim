# Public Side Shared Look Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every page that uses `_GuestLayout` a dark, bold look with a top bar (logo, account buttons, ☰ menu) and a full footer, without touching staff pages or the viewer splash page.

**Architecture:** A new `wwwroot/css/guest.css`, loaded only by `_GuestLayout` after `site.css`, holds the colours as CSS variables and the public-side rules. `_GuestLayout` always renders dark, and it builds the top bar and footer itself from `ThemeStore` (the logo), the guest cookie (the resident's name) and the staff cookie (Back to staff). All words come from `ResidentText`.

**Tech Stack:** ASP.NET Core Razor Pages (.NET), Bootstrap 5 (no Bootstrap JS on guest pages), xUnit v3 with AngleSharp page tests (`LauncherFactory`).

**Spec:** `docs/superpowers/specs/2026-11-17-public-look-design.md`

## Global Constraints

- Only `_GuestLayout` loads `guest.css`. `_Layout` (staff) and `_SplashLayout` (viewer splash) must not change.
- The public side is always dark: `<html data-bs-theme="dark">`, and `/js/theme.js` is not loaded by `_GuestLayout`.
- Headings use weight 800, UPPERCASE, letter spacing `.04em`. Body text stays normal case.
- Main button: yellow (`.btn-primary` on the public side) with dark words. Second button: blue. Outline buttons: white.
- WCAG AA: body text, muted text, the yellow button and the blue button each have a contrast ratio of at least 4.5 against their background.
- Right-to-left still works: `dir="rtl"` plus `bootstrap.rtl.min.css`. `guest.css` uses logical properties (`margin-inline-*`, `inset-inline-*`), never `left`/`right`.
- Every new visitor-facing word goes in `Resources/ResidentText.resx` **and** the five translations (`.de`, `.es`, `.fr`, `.pt`, `.ru`). `ResidentTextScanTests` must pass.
- The ☰ menu needs no script and works by keyboard: a `<details>`/`<summary>`.
- Copy the style only. Never Alife's name, photos or words.
- Test command: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release` (one class: append `-- --filter-class "*PublicLookTests"`).

## Review Focus

1. **The logo is set but its file is gone, or there is no logo.** The grid name alone shows, with no broken image. `ThemeStore.Logo()` already returns null when the file is gone. Test in Task 2.
2. **A staff member who is also signed in as a resident in the same browser.** Back to staff shows instead of Login/Register, and the resident's name does not show. Test in Task 2.
3. **Signing out from the top bar.** It is a POST with the antiforgery token; a link must never sign out. Test in Task 2.
4. **The language picker inside the closed menu.** It still posts to `/Language` with the current page as `returnUrl`. The existing `LanguagePickerTests` must keep passing unchanged, apart from selectors that named the header's old button row. Test in Task 2.
5. **Contrast regressions when someone edits a colour later.** A test reads the variables from `guest.css` and checks the ratios. Test in Task 1.

---

## File Structure

- Create `Launcher/GridGate.Launcher/wwwroot/css/guest.css`: the colour variables and every public-side rule (page, headings, buttons, cards, top bar, menu, footer).
- Modify `Launcher/GridGate.Launcher/Pages/Shared/_GuestLayout.cshtml`: always dark, load `guest.css`, the new top bar and footer.
- Modify `Launcher/GridGate.Launcher/Pages/Home.cshtml`: remove its `HeaderActions` section, because the staff sign-in moves to the footer.
- Modify `Launcher/GridGate.Launcher/Resources/ResidentText*.resx` (6 files): new `Layout.*` keys.
- Create `Launcher/GridGate.Launcher.Tests/PublicLookTests.cs`: the new tests.
- Modify `FrontPageTests.cs`, `GuestPagesHubTests.cs`, `ButtonIconsTests.cs`: the staff sign-in link's new place.

---

### Task 1: Always-dark public stylesheet

**Files:**
- Create: `Launcher/GridGate.Launcher/wwwroot/css/guest.css`
- Modify: `Launcher/GridGate.Launcher/Pages/Shared/_GuestLayout.cshtml:15-25`
- Test: `Launcher/GridGate.Launcher.Tests/PublicLookTests.cs`

**Interfaces:**
- Produces: `<body class="guest-site ...">` on every `_GuestLayout` page (any `page-wide`/`page-full` class stays as well). These CSS variables on `.guest-site` are used by Tasks 2 and 3:

| Variable | Value | Used for |
|---|---|---|
| `--gg-bg` | `#00001a` | page |
| `--gg-surface` | `#0b1236` | cards, top bar, menu |
| `--gg-border` | `#2a3a7a` | card and bar borders |
| `--gg-text` | `#f8f9fa` | body text |
| `--gg-muted` | `#c9cfdb` | secondary words |
| `--gg-yellow` | `#ffc107` | main button |
| `--gg-yellow-text` | `#111111` | main button words |
| `--gg-blue` | `#0b5ed7` | second button, links on hover |
| `--gg-blue-text` | `#ffffff` | second button words |
| `--gg-link` | `#9ec5fe` | links |
| `--gg-glow` | `0 0 1.25rem rgba(13,110,253,.35)` | card glow |

  Map them onto Bootstrap inside `.guest-site`: `--bs-body-bg`, `--bs-body-color`, `--bs-secondary-color`, `--bs-border-color`, `--bs-link-color`, `--bs-tertiary-bg`. Make `.guest-site .btn-primary` yellow and `.guest-site .btn-secondary` / `.btn-info` blue. Make `.guest-site .card` the surface with the border, `--gg-glow` and `border-radius: 1rem`. Make `.guest-site h1, h2, h3, .h4` weight 800, uppercase, `.04em`; `h1` is `clamp(1.8rem, 4vw, 2.6rem)`.
  Keep each variable on its own line in the form `--gg-name: value;`, because the contrast test reads them with a regex.

- [ ] **Step 1: Write the failing tests** in a new `PublicLookTests` class. Reuse `FrontPageTests.Factory`'s settings (GridName "Avalon", setup finished).
  - `Guest_pages_are_always_dark_and_load_guest_css`: for each of `/home`, `/signup`, `/support` and `/guest/sign-in`:
    - `html[data-bs-theme]` is `"dark"`;
    - there is a `link[href='/css/guest.css']`, and it comes after `link[href='/css/site.css']`;
    - there is no `script[src='/js/theme.js']`;
    - `body` has class `guest-site`.
  - `Staff_pages_do_not_load_guest_css`: as admin, `/profile/sessions` has no `/css/guest.css` link and still has `/js/theme.js`.
  - `Guest_css_is_served`: `GET /css/guest.css` returns 200 with content type `text/css`.
  - `Guest_colours_meet_contrast`:
    - Read `guest.css` from the source tree; find the file the way `ResidentTextScanTests.PagesDirectory()` finds `Pages/`.
    - Parse `--gg-*: #hex;` with a regex and compute WCAG contrast (relative luminance).
    - Assert ≥ 4.5 for `text/bg`, `muted/bg`, `text/surface`, `muted/surface`, `yellow-text/yellow`, `blue-text/blue` and `link/bg`.
  - `Splash_page_is_unchanged`: `/welcome` has no `/css/guest.css` link.
- [ ] **Step 2: Run them and see them fail.** Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release -- --filter-class "*PublicLookTests"`. Expected: FAIL, because `guest.css` is missing and the theme is not dark.
- [ ] **Step 3: Implement.**
  - Write `guest.css` with the variables and mappings above.
  - In `_GuestLayout`, put `data-bs-theme="dark"` on `<html>` and drop the `theme.js` script.
  - Add `<link rel="stylesheet" href="/css/guest.css">` after `site.css`.
  - Prepend `guest-site` to the body class. Today the attribute can be null, so build the class string from `guest-site` plus the optional `page-wide`/`page-full`.
- [ ] **Step 4: Fix the one existing assertion this breaks.** `ButtonIconsTests` asserts `Assert.Null(page.Body!.GetAttribute("class"))` for "the narrow, centred column". Change it to assert that the class is exactly `"guest-site"`.
- [ ] **Step 5: Run the whole suite.** Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release`. Expected: all pass.
- [ ] **Step 6: Commit.** `git add` the css, the layout and the tests, then `git commit -m "feat(public-look): always-dark public stylesheet"`.

### Task 2: Top bar with logo, account buttons and ☰ menu

**Files:**
- Modify: `Launcher/GridGate.Launcher/Pages/Shared/_GuestLayout.cshtml:1-46`
- Modify: `Launcher/GridGate.Launcher/wwwroot/css/guest.css` (top bar and menu rules)
- Modify: `Launcher/GridGate.Launcher/Resources/ResidentText.resx` and the five translations
- Test: `Launcher/GridGate.Launcher.Tests/PublicLookTests.cs`

**Interfaces:**
- Consumes: `ThemeStore.Logo()` (returns `string?`), injected with `@inject GridGate.Launcher.Core.Theme.ThemeStore Theme`, and `GridGate.Launcher.Splash.SplashSnapshot.LogoUrl(string)`.
- Consumes: the resident's name from `(await AuthenticateAsync(Context, AuthSchemes.Guest)).Principal?.Identity?.Name`, built the same way the layout already reads the staff cookie.
- Produces these element ids (tests and later pages rely on them):
  - `header a#site-home` (href `/home`), with `img#site-logo` (`alt` = grid name) when there is a logo, and always `span.grid-name`.
  - Not signed in at all:
    - `a#login-link`: href `/guest/sign-in`, text `Common.SignIn`, class `btn btn-sm btn-outline-secondary`, icon `sign-in`.
    - `a#register-link`: href `/signup`, text `Layout.Register`, class `btn btn-sm btn-primary` (yellow), icon `plus`.
  - Resident signed in, no staff cookie:
    - `a#resident-link`: href `/guest`, text = the avatar name, icon `user`. If the sprite has no `user` icon, use whichever person icon `icons.svg` has.
    - `form#sign-out-form`: method `post`, action `/guest/sign-out`, `asp-antiforgery="true"`, with a submit button whose text is `Guest.SignOut`.
  - Staff cookie: `a#staff-side-link` exactly as today, and neither of the two sets above. The page's `HeaderActions` section is ignored, as today.
  - `details#site-menu` with `summary` (aria-label `Layout.Menu`; the visible content is a ☰ icon plus visually hidden `Layout.Menu` text). Inside is `nav[aria-label=Layout.Menu]` listing, in order:
    - `a#menu-home` → `/home` (`Layout.MenuHome`)
    - `a#menu-connect` → `/guest/connect` (`Layout.MenuConnect`)
    - `a#help-link` → `/support` (`Layout.Help`, icon `help`; same id as today)
    - `a#menu-reset` → `/guest/forgot-password` (`Layout.MenuResetPassword`)
    - then `<partial name="_LanguagePicker" />`.
  - `HeaderActions` keeps rendering for non-staff, between the account buttons and the menu. Task 3 removes Home's use of it; the section stays for future pages.

New `ResidentText` keys (English values; translate the five others in the same style as their neighbouring `Layout.*` entries):

| Key | English |
|---|---|
| `Layout.Register` | `Register` |
| `Layout.Menu` | `Menu` |
| `Layout.MenuHome` | `Home` |
| `Layout.MenuConnect` | `How to connect` |
| `Layout.MenuResetPassword` | `Reset password` |

- [ ] **Step 1: Write the failing tests** in `PublicLookTests`:
  - `Visitor_top_bar_has_login_register_and_menu`: on `/home`:
    - `#login-link` href is `/guest/sign-in` and its text is "Sign in"; `#register-link` href is `/signup` and its text is "Register";
    - `header details#site-menu > summary` exists;
    - the menu link hrefs, in order, are `["/home", "/guest/connect", "/support", "/guest/forgot-password"]`;
    - `header details#site-menu form#language-picker` exists when two languages are offered (reuse the setup in `LanguagePickerTests`);
    - there is no `#resident-link` and no `img#site-logo`;
    - `header a#site-home .grid-name` text is "Avalon".
  - `Top_bar_shows_the_logo_when_one_is_set`:
    - Arrange: save a picture through the theme `PictureStore` and call `ThemeStore.SetLogo(name)`, following how the splash editor tests set a logo (search `SetLogo` in `GridGate.Launcher.Tests`).
    - Assert: `img#site-logo` src is `/theme/images/{name}` and its alt is "Avalon".
  - `Top_bar_hides_a_logo_whose_file_is_gone`: set a logo, delete its picture file, and assert there is no `img#site-logo` while `.grid-name` still says "Avalon".
  - `Signed_in_resident_sees_name_and_sign_out`:
    - Sign in with `/__test/guest-signin`, open `/home`, and read the expected avatar name from that test endpoint's setup (search `guest-signin` in the test helpers).
    - Assert that `#resident-link` text equals that name, `form#sign-out-form` has method post and action `/guest/sign-out` and contains `input[name=__RequestVerificationToken]`, and there is no `#login-link` or `#register-link`.
  - `Staff_with_resident_cookie_sees_only_back_to_staff`: as admin and also `/__test/guest-signin`, `/home` has `#staff-side-link` and none of `#login-link`, `#register-link` or `#resident-link`.
  - `Get_sign_out_does_not_sign_out`: `GET /guest/sign-out` returns 405 (today's behaviour, pinned so a link can never sign out).
- [ ] **Step 2: Run them and see them fail.** Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release -- --filter-class "*PublicLookTests"`. Expected: the Task 2 tests fail.
- [ ] **Step 3: Implement.**
  - Rebuild the header of `_GuestLayout` to the ids above. Add the five keys to all six resx files.
  - In `guest.css`, the top bar has a black background (`#000`), a `--gg-border` bottom border and is sticky at the top. The logo is at most `2.25rem` high.
  - The menu panel opens below `summary`, aligned to the inline end (`inset-inline-end: 0`), on `--gg-surface`. Links are full-width rows and `summary` has no default marker.
  - The account buttons are uppercase with weight 700.
- [ ] **Step 4: Run the whole suite.** Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release`. Expected: all pass. This includes `LanguagePickerTests`, `SupportPagesTests` (`header #help-link` is still in the header, now inside the menu) and `ResidentTextScanTests`. If a test selected `header .guest-actions` for the help link or the language picker, change only that selector to `header #site-menu`.
- [ ] **Step 5: Commit.** `git commit -m "feat(public-look): top bar with logo, account buttons and menu"`.

### Task 3: Footer with copyright and staff sign-in

**Files:**
- Modify: `Launcher/GridGate.Launcher/Pages/Shared/_GuestLayout.cshtml:50-54`
- Modify: `Launcher/GridGate.Launcher/Pages/Home.cshtml:45-47` (remove the `HeaderActions` section)
- Modify: `Launcher/GridGate.Launcher/wwwroot/css/guest.css` (footer rules)
- Modify: the six `ResidentText*.resx` files
- Modify: `FrontPageTests.cs:60-61`, `GuestPagesHubTests.cs:132,140`, `ButtonIconsTests.cs:94`
- Test: `Launcher/GridGate.Launcher.Tests/PublicLookTests.cs`

**Interfaces:**
- Consumes: `_LegalLinks` (unchanged) and the grid name from `Settings().GridName`.
- Produces:
  - `footer#site-footer` containing, in order: `<partial name="_LegalLinks" />`, `p#site-copyright` with `Layout.Copyright` formatted with the grid name, then `a#staff-link` (href `/login`, icon `shield`, text `Home.StaffSignIn`, class `small`).
  - `a#staff-link` is left out when the staff cookie is present, so it never shows next to Back to staff.
  - The optional `Footer` section still renders after these.

New key: `Layout.Copyright` = `© {0}. All rights reserved.` Translate it into the five others.

- [ ] **Step 1: Write the failing tests and move the old assertions.**
  - In `PublicLookTests`:
    - `Footer_has_legal_copyright_and_staff_sign_in`: on `/home`, `/signup` and `/support`:
      - `footer#site-footer p#site-copyright` text is `"© Avalon. All rights reserved."`;
      - `footer#site-footer a#staff-link` href is `/login`;
      - there is no `header #staff-link`.
    - `Footer_hides_staff_sign_in_for_staff`: as admin, `/home` has no `#staff-link`.
  - Edit the existing assertions so they look in the footer:
    - `FrontPageTests.cs:60`: change the selector to `footer a#staff-link[href='/login']`.
    - `GuestPagesHubTests.cs:140`: change it to `footer a#staff-link`.
    - `ButtonIconsTests.cs:94`: change it to `footer a#staff-link svg use`.
- [ ] **Step 2: Run them and see them fail.** Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release -- --filter-class "*PublicLookTests"`. Expected: the Task 3 tests fail.
- [ ] **Step 3: Implement.**
  - Build the footer in `_GuestLayout`, remove the `@section HeaderActions` block from `Home.cshtml`, and add the key to all six resx files.
  - In `guest.css`, the footer is full width on `#000` with a `--gg-border` top border and centred `--gg-muted` text. Legal links are white and sit on one wrapping row.
- [ ] **Step 4: Run the whole suite.** Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release`. Expected: all pass.
- [ ] **Step 5: Commit.** `git commit -m "feat(public-look): footer with copyright and staff sign-in"`.

### Task 4: See it in the browser

**Files:** none unless a fix is needed. Any fix goes in `guest.css` or `_GuestLayout`, with a test if it changes markup.

- [ ] **Step 1:** Run the launcher from the worktree (use the `run` skill or the project's own launch settings) and open `/home`, `/signup`, `/support` and `/guest/sign-in` in the browser pane at desktop width and at the mobile preset.
- [ ] **Step 2:** Check by eye:
  - the page is dark navy and the headings are bold uppercase;
  - the yellow Register button and the white outline Sign in button are in the top bar;
  - the ☰ menu opens by click and by keyboard (Tab to it, Enter), and its links and the language picker work;
  - the footer shows the legal links, the copyright and Staff sign in;
  - nothing overflows on mobile.
- [ ] **Step 3:** Open a staff page (`/` as owner) and confirm it looks exactly as before.
- [ ] **Step 4:** If anything was fixed, run the whole suite again and commit: `git commit -m "fix(public-look): <what>"`.
