# Account Pages Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sign in, sign-in code, forgot password, choose a new password and sign up show a big headline on the left and the form card on the right (one column on a phone).

**Architecture:** One new set of rules in `guest.css` (`account-split`). Each of the five pages swaps its `sign-in-card` wrapper for the split markup by hand. No page model, route or text changes, apart from one "Back to sign in" link on the reset form.

**Tech Stack:** ASP.NET Core Razor Pages, Bootstrap 5, xUnit v3 + AngleSharp (`LauncherFactory`).

**Spec:** `docs/superpowers/specs/2026-11-20-account-pages-design.md`

## Global Constraints

- Pages: `Pages/Guest/SignIn.cshtml`, `Pages/Guest/SignInCode.cshtml`, `Pages/Guest/ForgotPassword.cshtml`, `Pages/Guest/ResetPassword.cshtml`, `Pages/SignUp.cshtml`. Nothing else changes look.
- Not changed: `/guest/two-factor`, staff `/login` and `/login/code`, `Guest/Confirm`, `Guest/ConfirmEmail`, `Support/*`. `.sign-in-card` in `site.css` stays.
- No new `ResidentText` keys. Headline = the page's existing title; left line = the page's existing intro, shown only in the form state.
- In any non-form state (sent, expired, done, closed, check email, waiting for staff) the left column has the headline alone; the message sits in the card.
- Every element id, form id, input name and existing class used by tests or `signup-steps.js` stays as it is (`#forgot`, `#reset`, `#sign-up`, `#step1`, `#step2`, `#resend`, `#reset-reply`, `#reset-done`, `#reset-expired`, `#reset-off`, `#sign-up-reply`, `.tone-text`, ...).
- Two columns from 768px up; one column below, headline first; no sideways scroll at 375px in any of the six languages.
- No photo behind the pages.

## The markup (every page)

```html
<div class="account-split">                      <!-- sign up: class="account-split account-split-wide" -->
    <div class="account-lead">
        <h1>@T["…Title"]</h1>
        <p class="tone-text account-line">@T["….Intro"]</p>   <!-- form state only -->
    </div>
    <div class="card card-body shadow-sm account-card">
        … everything the old sign-in-card held, minus the <h1> and the intro <p> …
    </div>
</div>
```

The test helper (Task 1) checks exactly this: `.account-split > .account-lead > h1`, `.account-split > .account-card.card`, `p.account-line` inside `.account-lead` present or absent, and no `.sign-in-card` on the page.

## Review Focus

- **Long translated headlines on a phone** ("Neues Passwort wählen", "Zwei-Faktor-Code"), shown at headline size, must wrap, not scroll sideways. `overflow-wrap: anywhere` on the headline; checked in the browser at 375px in German (Task 4).
- **A failed post** (wrong password, bot check failed, passwords don't match) re-renders the page. Its error box must sit in the card and the left line must still show. Test in Task 1 (sign in, wrong password).
- **Sign up step 2** (starter avatars) is driven by `signup-steps.js` through `#sign-up`, `#step1`, `#step2`, `#to-step1`, `#to-step2`. The existing `SignUpStepsPageTests` must pass unchanged (Task 3).
- **The staff `/login`** shares the old card class. It must not pick up the split. Test in Task 1.
- **Sign up closed** is the state a visitor sees on a private grid. Headline alone on the left, message in the card. Test in Task 3.

---

### Task 1: The split style, sign in and sign-in code

**Files:**
- Modify: `Launcher/GridGate.Launcher/wwwroot/css/guest.css` (append after the front page rules)
- Modify: `Launcher/GridGate.Launcher/Pages/Guest/SignIn.cshtml`
- Modify: `Launcher/GridGate.Launcher/Pages/Guest/SignInCode.cshtml`
- Create: `Launcher/GridGate.Launcher.Tests/AccountPagesLookTests.cs`
- Modify: `Launcher/GridGate.Launcher.Tests/ResidentTwoFactorTests.cs` (one test)

**Interfaces:**
- Produces: `internal static void AccountPagesLookTests.AssertSplit(AngleSharp.Dom.IDocument page, string headline, bool line)`. It asserts the markup above, that `.account-lead h1` text (trimmed) equals `headline`, that `.account-lead p.account-line` is present iff `line`, and that `page.QuerySelector(".sign-in-card")` is null. Tasks 2 and 3 call it.
- Produces CSS classes `account-split`, `account-split-wide`, `account-lead`, `account-line`, `account-card`.

- [ ] **Step 1: Write the failing tests** in `AccountPagesLookTests.cs`. Use a factory like `PublicLookTests.Factory()` (owner set up, `GridName = "Avalon"`), plus a resident added with `f.Robust.Add(...)` as in `SignInPagesKeysTests`.
  - `Sign_in_has_headline_left_and_form_right`: GET `/guest/sign-in` → `AssertSplit(page, "Sign in", line: true)`; `.account-card form input[name=Password]` not null.
  - `Sign_in_failure_keeps_the_error_in_the_card`: POST `/guest/sign-in` with the form's antiforgery token, a real resident's name and a wrong password → `AssertSplit(page, "Sign in", line: true)`; `.account-card .error` not null.
  - `Staff_login_keeps_the_staff_card`: GET `/login` → `.sign-in-card` not null, `.account-split` null.
  - In `ResidentTwoFactorTests`, `Code_page_has_headline_left_and_form_right`: enrol a resident and post the password as that file's other code tests do, then GET `/guest/sign-in/code` with the same client → `AccountPagesLookTests.AssertSplit(page, "Two-factor code", line: true)`; `.account-card input[name=Code]` not null.

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release -- --filter-class "*AccountPagesLookTests"` (and `--filter-method "*Code_page_has_headline*"`)
Expected: the sign-in and code tests FAIL (no `.account-split`); `Staff_login_keeps_the_staff_card` passes.

- [ ] **Step 3: Add the CSS** to `guest.css`, under a comment naming spec `2026-11-20-account-pages-design`:
  - `.account-split`: grid, `gap: 2rem`, `align-items: center`, `max-width: 64rem`, `margin: 1.5rem auto`.
  - At `min-width: 768px`: `grid-template-columns: minmax(0, 1fr) minmax(0, 26rem)`. For `.account-split-wide`: `minmax(0, 1fr) minmax(0, 36rem)`.
  - `.account-lead h1`: `font-size: clamp(2.2rem, 6vw, 4rem)`, `line-height: 1.05`, `margin-bottom: .75rem`, `overflow-wrap: anywhere`, `hyphens: auto`.
  - `.account-line`: the front hero line's size and weight (`clamp(1.05rem, 2.2vw, 1.3rem)`, 600), `margin: 0`.
  - `.account-card`: `width: 100%`.

- [ ] **Step 4: Change `SignIn.cshtml` and `SignInCode.cshtml`** to the markup above. Sign in's line is `SignIn.Intro` and the code page's is `SignInCode.Intro`. The code page's intro has no `tone-text` today; give it `class="tone-text account-line"` too. The error box, the "code unknown" alert, the resend form and the links all go in the card.

- [ ] **Step 5: Run the tests and see them pass**

Run the Step 2 commands, then `--filter-class "*SignInPagesKeysTests"` and `--filter-class "*ResidentTwoFactorTests"`.
Expected: all PASS.

- [ ] **Step 6: Commit** — `feat(guest): sign in and sign-in code put the headline left and the form right`

### Task 2: Forgot password and choose a new password

**Files:**
- Modify: `Launcher/GridGate.Launcher/Pages/Guest/ForgotPassword.cshtml`
- Modify: `Launcher/GridGate.Launcher/Pages/Guest/ResetPassword.cshtml`
- Modify: `Launcher/GridGate.Launcher.Tests/PasswordResetTests.Reset.cs` (three tests; its `Factory()` already turns resets on)

**Interfaces:**
- Consumes: `AccountPagesLookTests.AssertSplit` (Task 1).

- [ ] **Step 1: Write the failing tests**
  - `PasswordResetTests.Forgot_password_has_headline_left_and_form_right`: `Factory()`, GET `/guest/forgot-password` → `AssertSplit(page, "Forgot password", line: true)`; `.account-card form#forgot` not null; `.account-card a[href='/guest/sign-in']` text is "Back to sign in".
  - `PasswordResetTests.Reset_form_has_headline_left_and_back_link`: `LinkTokenAsync(rig, "Ann Smith", "ann@example.com")`, GET `/guest/reset-password?token=` + token → `AccountPagesLookTests.AssertSplit(page, "Choose a new password", line: true)`; `.account-card form#reset` not null; `.account-card a[href='/guest/sign-in']` text "Back to sign in".
  - `PasswordResetTests.Expired_link_shows_headline_alone`: GET `/guest/reset-password?token=` + 43 `A`s → `AssertSplit(page, "Choose a new password", line: false)`; `.account-card #reset-expired` not null.

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release -- --filter-method "*_headline_*"`
Expected: the three new tests FAIL.

- [ ] **Step 3: Change both pages** to the markup above. Forgot password's line is `ForgotPassword.Intro` and appears only in the form branch; the `Off` and `Sent` branches put their message in the card. Reset's line is `ResetPassword.Intro` (with its two arguments) and appears only in the form branch. The reset form branch gains `<p class="mt-3 mb-0"><a href="/guest/sign-in">@T["Common.BackToSignIn"]</a></p>` after the form.

- [ ] **Step 4: Run the tests and see them pass**

Run the Step 2 commands, then `--filter-class "*PasswordResetTests"`.
Expected: all PASS.

- [ ] **Step 5: Commit** — `feat(guest): the password reset pages put the headline left and the form right`

### Task 3: Sign up

**Files:**
- Modify: `Launcher/GridGate.Launcher/Pages/SignUp.cshtml`
- Modify: `Launcher/GridGate.Launcher.Tests/AccountPagesLookTests.cs`

**Interfaces:**
- Consumes: `AccountPagesLookTests.AssertSplit` (Task 1); `SignUpsTests.Factory(bool open = true)` (exists; returns a `Rig`, the `LauncherFactory` is `.F`).

- [ ] **Step 1: Write the failing tests**
  - `Sign_up_has_headline_left_and_wide_form_right`: `await using var rig = SignUpsTests.Factory();`, GET `/signup` on `rig.F` → `AssertSplit(page, "Create an account", line: true)`; `.account-split.account-split-wide` not null; `.account-card form#sign-up` not null.
  - `Sign_up_closed_shows_headline_alone`: `SignUpsTests.Factory(open: false)`, GET `/signup` → `AssertSplit(page, "Create an account", line: false)`; the card's text contains "This grid isn't open for sign-ups yet."

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release -- --filter-class "*AccountPagesLookTests"`
Expected: the two sign-up tests FAIL.

- [ ] **Step 3: Change `SignUp.cshtml`** to the markup above with `account-split account-split-wide`. The line is `SignUp.Intro` (with `Model.GridName`), shown only in the form branch. The `Closed`, `CheckEmail` and `WaitingForStaff` branches keep their words in the card. The step script tag stays inside the card where it is now.

- [ ] **Step 4: Run the tests and see them pass**

Run: `--filter-class "*AccountPagesLookTests"`, `--filter-class "*SignUpPageTests"`, `--filter-class "*SignUpStepsPageTests"`, `--filter-class "*SignUpsTests"`, `--filter-class "*PublicLookTests"`.
Expected: all PASS.

- [ ] **Step 5: Commit** — `feat(signup): sign up puts the headline left and a wide form right`

### Task 4: Whole check

- [ ] **Step 1:** Run the full launcher tests: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release`. Expected: 0 failed. A failure means a test that read the old card; fix the markup, not the test, unless the test pinned `sign-in-card` on one of the five pages.
- [ ] **Step 2:** In the browser at desktop width and at 375px, look at `/guest/sign-in`, `/guest/forgot-password`, `/guest/reset-password?token=AAAA…`, `/signup` (and step 2 if starter avatars exist), in English and German. Expected: headline left and card right on desktop; one column and no sideways scroll on the phone; `/login` unchanged.
- [ ] **Step 3:** `bash .github/scripts/check-old-name.sh` and gitleaks 8.30.1 (`git` over the branch range and `dir`). Expected: clean.
