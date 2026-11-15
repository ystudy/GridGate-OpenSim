# Money plan 4: Gloebit — Implementation Plan

> **For the builder:** the owner builds "native": this session does every task itself with superpowers:executing-plans,
> test-first (superpowers:test-driven-development), with no subagent-driven execution. Steps use checkbox (`- [ ]`) syntax.

**Goal:** The owner can pick **Gloebit** on the Money page. Then each region program runs Gloebit's own OpenSim module
(LGPL, its own DLL), configured from settings pages rather than hand-edited INIs. Real dollars and artists' cash-outs go
through Gloebit; GridGate's coin ledger is kept but doesn't run. Money stays Off in every build, because `MoneyRelease`
is unchanged and Gloebit sits behind `PlayCoinsApproved`, as the design says.

**Architecture:**
- **Settings.** `MoneySettings.Gloebit` holds this computer's Gloebit app: its environment (sandbox or production), key,
  secret, and the owner's contact name and email. It lives in the owner-only settings file, like the database passwords.
  The secret is never shown back.
- **The DLL.**
  - Gloebit's module switches itself on whenever `Gloebit.dll` is present.
  - So the runtime gets `Gloebit.dll` only while the grid's mode is Gloebit and this computer has a complete Gloebit
    app. Otherwise it is removed.
  - The package ships the DLL built from this tree under the name `Gloebit.dll.Proto`.
- **The INIs.** In Gloebit mode every region program's `OpenSim.ini` gets `[Economy] economymodule = Gloebit` and a
  `[Gloebit]` section written by `GridConfigWriter`. Without a complete Gloebit app, the programs run GridGate's module in
  its refusing state (plan 3's `Paired = false`), so nothing is free by accident.
- **Beside the ledger (design §10).** `MoneyPlace.ServiceHere` is false in Gloebit mode, so the ledger, the money
  service, the Money page's books, My coins and the dashboard's money notices are all off. Coin balances stay in the
  database and come back when the owner switches back. Switching between coins and Gloebit asks the owner to confirm
  that, first.
- **Region Servers (design §7.7).** They follow the published mode `gloebit`. Each one's owner enters a Gloebit app on
  that computer's `/advanced/money` page. There's nothing to pair.

**Tech Stack:** .NET 10; ASP.NET Core Razor Pages; the existing `GridConfigWriter`, `RuntimeStager`, `MoneyGate`,
`MoneyPlace` and economy helper; xunit.v3; PowerShell (`setup/package.ps1`); Gloebit's module source in
`addon-modules/Gloebit` (built by `GridGate.sln` into `bin/Gloebit.dll`).

**Spec:** `docs/superpowers/specs/2026-11-13-money-design.md` (owner-approved): §1, §3 goal 5, §4, §6 (LGPL, a separate
DLL), §7.7 (Gloebit on a Region Server), §10 (changing mode), §14 step 5. Plan 3
(`2026-11-14-money-plan3-visitors-region-servers.md`, PR 84) must be merged before this plan's PR.

**Found while preparing this plan (the owner has been told):**
1. **The first-login `G$ 0` from plan 2's live checks** has a cause:
   - `bin/Robust.HG.ini.proto:546` sets `[LoginService] Currency="G$"`, left over from DreamGrid's Gloebit set-up. Every
     login is told the currency is G$ unless money is on *and* Robust was started with money's INI.
   - In plan 2's first run, and again in plan 3's, Robust and the regions had started before money was turned on.
     Restarting GridGate adopted them, so they kept their old INIs. That also explains "no money lines" in the regions.
   - Two things are fixed here: the proto's `G$`, and the "takes effect when GridGate restarts" wording, which should
     say the world restarts too.
2. **Fred's Gloebit credentials are committed in this repository and ship in every package.**
   - `bin/Gloebit.ini` holds a *production* `GLBKey` and `GLBSecret`, Fred's name and email, and `Currency=G$`.
   - `bin/config-include/Gloebit.ini` holds a sandbox key and secret.
   - `bin/config-addon-opensim/Gloebit.ini` has `Enabled=True` and Fred's contact details. Every region loads it, because
     `ConfigDirectory="./config-addon-opensim"`.
   - `.gitleaksignore` calls the `bin/Gloebit.ini` values a sandbox key, which is wrong.
   - This plan removes all three files from the tree and the packages.
   - The values stay in git history. Rewriting history needs a force-push, which the owner's rules forbid, so it's the
     owner's call whether to tell Fred that his key and secret are public.
3. **`bin/Gloebit.dll.Proto` is a committed binary** from DreamGrid days (`89f1296a "PayPal final"`). It isn't the DLL
   built from `addon-modules/Gloebit`. This plan removes it and packages the freshly built DLL instead.

**Decisions (the builder's, within design §14 step 5):**
- **Mode choice.**
  - The Money page offers Off, Play coins and Gloebit, each only when the gate allows it. Gloebit is gated by
    `PlayCoinsApproved`, as `MoneyRelease` already does. Real coins stay plan 6.
  - Choosing Gloebit needs a complete Gloebit app on this computer: environment, key, secret and owner name.
  - Switching between Play and Gloebit needs a ticked "I understand coin balances don't move to Gloebit" (design §10).
- **Restart wording.**
  - Saving a mode change says "Takes effect after you restart GridGate and then the world (Power → Restart on the
    dashboard)." The dashboard line from plan 3 says the same.
  - Starting and stopping on a mode change is plan 5.
- **The currency.**
  - Robust's `[LoginService] Currency` is the owner's symbol in Play or Real, and `G$` in Gloebit (Gloebit's own symbol,
    which its module also sends to the viewer).
  - In Off the key is commented out, so the viewer shows its own default.
- **The economy helper** answers in Gloebit mode too:
  - `getCurrencyQuote` and a land pre-flight with a shortfall answer `success` 0, with the text "This grid uses Gloebit.
    Buy gloebits on Gloebit's website." in the six languages.
  - `errorURI` is Gloebit's purchase page for the chosen environment.
  - In practice Firestorm asks the region instead, because Gloebit's module sets `currency-base-uri` to the region. The
    helper only answers older viewers.
- **Reachability.** Gloebit calls back into the region's HTTP port. The Money page warns, in Gloebit mode, on a Local
  plan or a Grid Asset Server with a network-only address: "Gloebit needs your regions reachable from the internet:
  purchases and payments call back to them."
- **The Gloebit build warnings.**
  - The two `CS0612` warnings (`RegionInfo.originRegionID` is obsolete, `GloebitMoneyModule.cs:2263` and `:3138`) become
    `RegionID`. OpenSim's own getter returns exactly that.
  - Those are the only edits to Gloebit's source, and they stay inside its own folder (design §6: a separate DLL). They're
    listed in `MERGING.md` under the addon modules.
- **Database.** Gloebit falls back to the region's `[DatabaseService]` connection, the program's own `opensim`
  database. No `GLBSpecific*` keys are written.

**Not in this plan:**
- Moving balances between GridGate coins and Gloebit: a non-goal (design §4).
- Gloebit live payments end to end: they need a public address for Gloebit's callbacks. The live checks run what a LAN
  can (Task 8).
- Starting and stopping services on a mode change: plan 5.

## Global Constraints

- **The gate.**
  - Both `MoneyRelease` constants stay `false` in every commit.
  - Code reads the mode only through `MoneyGate` / `MoneyPlace`.
  - Live checks open the gate in a separate working tree only.
- **Secrets.**
  - The Gloebit secret is never logged, audited, echoed into a page, or sent anywhere but the region's own INI. The
    region's INI is in the owner-only data folder already, like the database passwords.
  - Its form field is a password field that's empty on every page load. Saving with it empty keeps the stored secret.
  - The key is shown masked (`••••` plus its last 4 characters).
- **Mode Off and Play write no Gloebit anything:**
  - no `Gloebit.dll` in the runtime;
  - no `[Gloebit]` section;
  - `economymodule` blank (Off) or GridGateMoneyModule (Play).
  - Tests prove each.
- **No GridGate code is copied from Gloebit's module** (LGPL); GridGate only writes its INI keys and stages its DLL.
- Windows and Debian behave the same. `TreatWarningsAsErrors`. Staff pages are English; resident texts in six languages
  (`docs/languages.md`).
- Integration tests use `GRIDGATE_MARIADB_BIN=C:\Users\ystud\AppData\Local\DreamGridDev\mariadb-11.4.9-winx64\bin` with
  `GRIDGATE_REQUIRE_INTEGRATION=1`, no skips.
- **Branch and commits.**
  - The work is on `feature/money-plan4`, in the worktree `C:\Users\ystud\Desktop\CODE\GridGate-money-plan4`.
  - Check `git branch --show-current` before every commit.
  - Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Gloebit switching on by accident.** If `Gloebit.dll` is in the runtime in any mode but Gloebit, or when Gloebit's
   settings are incomplete, Gloebit takes over the economy. Tests: Task 2 `Gloebit_dll_only_in_gloebit_mode`,
   `Removed_when_leaving_gloebit`.
2. **The secret leaking.**
   - It must not appear in the launcher log, the audit log, the page HTML, the Money page's settings echo, or any error.
   - Tests: Task 1 `Secret_is_never_shown_back`, Task 3 `Secret_never_logged_or_audited`.
3. **A half-configured Gloebit** (mode Gloebit, but no key on this computer, for example on a Region Server whose owner
   hasn't entered one). The regions must refuse payments, not run free or crash. Tests: Task 2
   `Incomplete_gloebit_refuses_payments`, Task 5 `Region_server_without_its_own_app_refuses`.
4. **Fred's credentials.** No file in the tree, the runtime or a package may still contain them. Test: Task 6
   `No_upstream_gloebit_credentials_ship` (scans `bin/` and the package staging list).
5. **Coin balances across a switch.** Play → Gloebit → Play keeps every balance and entry untouched. Test: Task 4
   `Switching_to_gloebit_and_back_keeps_the_books`, integration.

---

### Task 1: Gloebit settings and the mode choice

**Files:**
- Modify: `Launcher/GridGate.Launcher.Core/Money/MoneySettings.cs`. Add
  `GloebitSettings? Gloebit = null`, with
  `public sealed record GloebitSettings(string Environment = "sandbox", string Key = "", string Secret = "", string OwnerName = "", string OwnerEmail = "")`
  and `bool Complete`. The record's `ToString` is overridden so the secret never prints.
- Create: `Launcher/GridGate.Launcher.Core/Money/GloebitRules.cs`:
  - `string? Problem(GloebitSettings g)`:
    - `Environment` is `sandbox` or `production`;
    - `Key` and `Secret` are GUIDs, which is the shape Gloebit issues;
    - `OwnerName` is 1–64 characters;
    - `OwnerEmail` is empty or looks like an address;
    - none of the values has a control character, `"`, `;`, `[` or `]`, which keeps them safe on an INI line.
  - `string Masked(string key)`.
- Modify: `Launcher/GridGate.Launcher/Money/MoneyPageRules.cs`. `Offered` adds Gloebit when `gate.Allows(Gloebit)`.
- Modify: `Launcher/GridGate.Launcher/Pages/Money/Index.cshtml(.cs)`:
  - **A Gloebit section** with environment (select), key, secret (password, never prefilled), owner name and owner email,
    saved through `grid.UpdateSettings(…, writeConfig: true)`. Audited as `money-gloebit` with result `ok` or the problem
    code, never the values.
  - **The mode select** includes "Gloebit" when offered.
  - **Saving a mode change:**
    - to Gloebit needs `Gloebit.Complete`;
    - Play/Real ↔ Gloebit needs `ConfirmSwitch = true`. The page then shows the design §10 warning, for example "GridGate
      coin balances don't move to Gloebit. They are kept, not shown, and come back if you switch back."
  - **The notice** after a mode change says "Takes effect after you restart GridGate and then the world (Power → Restart
    on the dashboard)."
  - **In Gloebit mode** the page shows "Gloebit handles payments on this grid", the reachability warning when it applies
    (see Decisions), and where coin balances are.
- Modify: `Launcher/GridGate.Launcher/Money/MoneyNotices.cs`. The restart line from plan 3 says "Restart GridGate and then
  the world (Power → Restart) for money to start."
- Test: `Launcher/GridGate.Launcher.Tests/GloebitRulesTests.cs` (new), `Launcher/GridGate.Launcher.Tests/MoneyPageTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// GloebitRulesTests
[Theory] void Problem_spots_bad_values()          // env "staging"; key "x"; secret not a GUID; name ""; name with ';'; email "a@"; → each a problem; a valid set → null
[Fact] void Complete_needs_key_secret_and_name()
[Fact] void ToString_never_shows_the_secret()      // $"{settings}" doesn't contain the secret
[Fact] void Masked_keeps_the_last_four()
// MoneyPageTests (open gate)
[Fact] Task Gloebit_is_offered_when_the_gate_allows()      // options Off, Play, Gloebit
[Fact] Task Closed_gate_offers_no_gloebit()
[Fact] Task Gloebit_settings_save_and_audit_without_values() // audit "money-gloebit" "ok"; no key/secret text in the audit line
[Fact] Task Secret_is_never_shown_back()                     // GET after save: no secret in HTML; key masked
[Fact] Task Empty_secret_keeps_the_stored_one()
[Fact] Task Gloebit_mode_needs_a_complete_app()              // POST mode Gloebit, no app → error, mode unchanged
[Fact] Task Switching_coins_and_gloebit_needs_the_tick()     // Play → Gloebit without ConfirmSwitch → error; with it → saved
[Fact] Task Mode_change_says_restart_the_world()
[Fact] Task Network_only_gloebit_warns_about_reachability()  // Plan Local + mode Gloebit → warning text
```

- [ ] **Step 2: Run them and see them fail.** · [ ] **Step 3: Implement.** · [ ] **Step 4: Run them, plus `*MoneyPage*`, `*MoneyGate*` and `*StaffMenu*`.**
- [ ] **Step 5: Commit** — `feat(money): Gloebit settings and the Gloebit mode on the Money page`

---

### Task 2: The runtime DLL and the region INIs

**Files:**
- Modify: `Launcher/GridGate.Launcher.Core/Runtime/RuntimeStager.cs`. Add `void SetGloebit(bool on)`:
  - **On:** copy the program folder's `Gloebit.dll.Proto` (the packaged name) or, in a dev build, its `Gloebit.dll`, to
    `runtime/opensim/Gloebit.dll`.
  - **Off:** delete `runtime/opensim/Gloebit.dll`.
  - Staging also leaves out `Gloebit.dll.Proto`.
- Modify: `Launcher/GridGate.Launcher.Core/Grid/GridController.cs`. Wherever it stages and writes config, call
  `stager.SetGloebit(MoneyPlace.GloebitHere(s, gate))`.
- Modify: `Launcher/GridGate.Launcher.Core/Money/MoneyPlace.cs`. Add
  `bool GloebitHere(LauncherSettings s, Func<MoneySettings, MoneyMode> gate) => GridMode(s, gate) == Gloebit && s.Money.Gloebit?.Complete == true && s.Plan != Connected`.
- Modify: `Launcher/GridGate.Launcher.Core/Config/GridConfigWriter.cs`:
  - **`GloebitHere`:** each program gets `[Economy] economymodule = Gloebit`, surviving `Finish`'s blanking like the money
    module does, and a `[Gloebit]` section with `Enabled = true`, `GLBEnvironment`, `GLBKey`, `GLBSecret`,
    `GLBOwnerName`, `GLBOwnerEmail` (when set), `GLBShowNewSessionAuthIM = true`, `GLBShowNewSessionPurchaseIM = false`
    and `GLBShowWelcomeMessage = true`.
  - **Gloebit mode without a complete app here:** every program gets plan 3's refusing state (`EconomyModule =
    GridGateMoneyModule`, `[GridGateMoney] Paired = false`, `HelperURI`).
  - **Robust `[LoginService] Currency`:** the owner's symbol in Play or Real, `G$` in Gloebit, and commented out in Off.
    That replaces the proto's leftover `G$`.
- Modify: `bin/Robust.HG.ini.proto`. Comment out `Currency="G$"` (one line, listed in `MERGING.md`).
- Test: `Launcher/GridGate.Launcher.Tests/GloebitConfigTests.cs` (new), `Launcher/GridGate.Launcher.Tests/RuntimeStagerTests.cs`,
  `Launcher/GridGate.Launcher.Tests/MoneyConfigTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// RuntimeStagerTests
[Fact] void Gloebit_dll_only_in_gloebit_mode()           // SetGloebit(true) → runtime/Gloebit.dll from .Proto (or Gloebit.dll in a dev bin); SetGloebit(false) → gone
[Fact] void Removed_when_leaving_gloebit()
[Fact] void Staging_never_copies_the_proto()
// GloebitConfigTests (open gate)
[Fact] void Gloebit_mode_writes_the_module_and_its_section()   // economymodule Gloebit; [Gloebit] Enabled true, env, key, secret, owner
[Fact] void Incomplete_gloebit_refuses_payments()              // Gloebit mode, no app → GridGateMoneyModule + Paired false; no [Gloebit]
[Fact] void Play_and_off_write_no_gloebit()                    // no [Gloebit] section, no economymodule Gloebit
[Fact] void Closed_gate_writes_no_gloebit()
[Fact] void Login_currency_follows_the_mode()                  // Play "C$"; Gloebit "G$"; Off: no active Currency key
[Fact] void Gloebit_values_with_ini_breakers_are_refused()     // a hand-edited settings file with ';' in the name → not written, launcher warning
// GridControllerWiringTests
[Fact] void Write_config_stages_gloebit_only_in_gloebit_mode()
```

- [ ] **Step 2: Run them and see them fail.** · [ ] **Step 3: Implement.** · [ ] **Step 4: Run them; regenerate the snapshots
  (`GG_CAPTURE_SNAPSHOTS=1`). The only expected diff is the Robust Currency line, which isn't in the snapshots, so there
  should be no diff; check it.**
- [ ] **Step 5: Commit** — `feat(money): Gloebit's DLL and INI only in Gloebit mode; no leftover G$ currency`

---

### Task 3: The economy helper, the coins page and the region side in Gloebit mode

**Files:**
- Modify: `Launcher/GridGate.Launcher/Economy/EconomyHelper.cs`. Gloebit mode answers with `Economy.Gloebit`, and
  `errorURI` is `https://www.gloebit.com/purchase/` (production) or `https://sandbox.gloebit.com/purchase/` (sandbox).
  It is the same for a land shortfall.
- Modify: `Launcher/GridGate.Launcher/Pages/Coins.cshtml(.cs)`. Gloebit mode says: "This grid uses Gloebit, an outside
  service, for payments. Buy gloebits and cash out on Gloebit's website." It links to Gloebit's site for the environment.
- Modify: `Launcher/GridGate.Launcher/Resources/ResidentText*.resx` (all six). Add `Economy.Gloebit` and
  `Coins.Gloebit`, following `docs/languages.md`.
- Test: `Launcher/GridGate.Launcher.Tests/EconomyHelperTests.cs`, `Launcher/GridGate.Launcher.Tests/CoinsPageTests.cs` (or
  wherever plan 2 put `/coins` tests)

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] Task Quote_in_gloebit_mode_points_at_gloebit()       // success 0, Economy.Gloebit text, errorURI sandbox purchase page; AssertFirestormSafe
[Fact] Task Land_shortfall_in_gloebit_mode_points_at_gloebit()
[Fact] Task Coins_page_explains_gloebit()                    // translated (fr)
[Fact] Task Secret_never_logged_or_audited()                 // save Gloebit settings, write config, start a fake grid start: launcher log + audit contain no secret
```

- [ ] **Step 2: Run them and see them fail.** · [ ] **Step 3: Implement.** · [ ] **Step 4: Run them, plus `TranslationsTests` and `ResidentTextScanTests`.**
- [ ] **Step 5: Commit** — `feat(money): the economy helper and coins page in Gloebit mode`

---

### Task 4: Coins and Gloebit side by side

**Files:**
- Test: `Launcher/GridGate.Launcher.IntegrationTests/MoneyGloebitSwitchTests.cs` (new)
- Modify (if the test finds one): anything that touches the money database while `ServiceHere` is false.

**What it proves:**
- Play coins with entries; switch to Gloebit and back to Play.
- The money database's accounts, balances and entries are identical before and after.
- No money service or maintenance ran while in Gloebit mode. The self-check passes after.

- [ ] **Step 1: Write the test** (`Switching_to_gloebit_and_back_keeps_the_books`), driving `MoneyStartup`,
  `MoneyServiceHost` and `MoneyMaintenance` with the mode function, on `MoneyTestDatabase`.
- [ ] **Step 2: Run it. If it passes at once, break it on purpose (let maintenance run in Gloebit mode) to see that it
  can fail, then restore.**
- [ ] **Step 3: Commit** — `test(money): coin books survive a switch to Gloebit and back`

---

### Task 5: Gloebit on a Region Server

**Files:**
- Modify: `Launcher/GridGate.Launcher/Pages/Advanced/Money.cshtml(.cs)`. When the grid's mode is Gloebit, the page
  shows the same Gloebit app form as Task 1 (this computer's own key and secret) instead of pairing. Saving rewrites the
  INIs, and the page says to restart the regions.
- `MoneyPlace.GloebitHere` and `GridConfigWriter` from Task 2 already cover a Region Server. Its own `Money.Gloebit`
  decides.
- Test: `Launcher/GridGate.Launcher.Tests/RegionServerMoneyPageTests.cs`, `Launcher/GridGate.Launcher.Tests/MoneyServerConfigTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] Task Gloebit_grid_shows_the_app_form_not_pairing()
[Fact] Task Region_server_saves_its_own_gloebit_app()         // secret masked after; audit without values
[Fact] void Region_server_without_its_own_app_refuses()        // Join.Money Gloebit, no app → Paired false, no Gloebit.dll staged
[Fact] void Region_server_with_its_app_runs_gloebit()          // economymodule Gloebit, [Gloebit] with its own key
[Fact] void Connected_server_never_runs_gloebit()
```

- [ ] **Step 2: Run them and see them fail.** · [ ] **Step 3: Implement.** · [ ] **Step 4: Run them.**
- [ ] **Step 5: Commit** — `feat(money): a Region Server runs Gloebit with its own app`

---

### Task 6: Packaging, upstream credentials and the build warnings

**Files:**
- Delete: `bin/Gloebit.ini`, `bin/config-include/Gloebit.ini`, `bin/config-addon-opensim/Gloebit.ini`,
  `bin/Gloebit.dll.Proto`.
- Modify: `.gitleaksignore`. Drop the four Gloebit lines.
- Modify: `.gitignore`. Add `bin/Gloebit.dll.Proto`, which only the package makes.
- Modify: `setup/package.ps1`. The freshly built `bin/Gloebit.dll` is copied into the package as `bin/Gloebit.dll.Proto`,
  and stays out under its own name, as today.
- Modify: `addon-modules/Gloebit/GloebitMoneyModule/GloebitMoneyModule.cs:2263` and `:3138`, `originRegionID` →
  `RegionID`.
- Modify: `MERGING.md`. List both GridGate edits to addon and upstream files: the Gloebit warning fix and the Robust proto
  Currency line.
- Modify: `tests/GridGate.Smoke/SmokeRun.cs:76`. Its comment names the old mechanism; update it.
- Test: `Launcher/GridGate.Launcher.Tests/ShippedFilesTests.cs` (new), or the existing packaging test class if there is
  one (`git grep -n "package.ps1" Launcher/*Tests`).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] void No_upstream_gloebit_credentials_ship()    // no file under bin/ (tracked) contains "GLBSecret" with a GUID value, "outworldz.com", or "Ferd Frederix"
[Fact] void Package_ships_the_built_gloebit_as_proto() // package.ps1 text: copies bin/Gloebit.dll to Gloebit.dll.Proto; excludes Gloebit.dll by name
```

- [ ] **Step 2: Run them and see them fail.** · [ ] **Step 3: Make the changes.** Run `dotnet build GridGate.sln -c Release`
  and expect 0 errors with **4 warnings** (the 2 Gloebit ones gone).
- [ ] **Step 4: Run the smoke run** (`dotnet run --project tests/GridGate.Smoke -c Release -- --bin bin`), because an
  upstream file changed. Expect it to pass.
- [ ] **Step 5: Commit** — `fix(money): no upstream Gloebit credentials in the tree; ship the built Gloebit DLL; Gloebit build warnings`

---

### Task 7: Full automated check

- [ ] **Step 1:** Run `dotnet build GridGate.sln -c Release`. Expected: 0 errors, 4 warnings.
- [ ] **Step 2:** Run the launcher unit tests and `tests/GridGate.Money.Tests`. Expected: all pass.
- [ ] **Step 3:** Run the money integration classes (`*Money*`, `*PresenceCheck*`, `*VisitorDirectory*`, `*RegionHosts*`)
  with no skips.
- [ ] **Step 4:** Run `tests/GridGate.Lsl.Tests`, `tests/GridGate.Platform.Tests` and the smoke run.
- [ ] **Step 5:** Run `git grep -n "PlayCoinsApproved = true\|RealCoinsApproved = true"`. Expected: only plan text.
  Then run `git grep -n -i "GLBSecret\s*=\s*[0-9a-f]"`. Expected: nothing.

---

### Task 8: Live checks

The gate is opened in a separate working tree only, as in plan 3. The owner does the logins. A Gloebit **sandbox** app
is needed for rows 3–6: the owner creates one at sandbox.gloebit.com and types its key and secret into the Money page
themself. Claude never types them. Without one, rows 3–6 are recorded as not checked live.

1. **Off build:** no `Gloebit.dll` in the runtime, and Robust's Currency isn't G$ (Firestorm shows its default).
2. **Play:** coins work as in plan 3, and the status bar shows the owner's symbol, not G$, on the first login after a
   world restart.
3. **Choose Gloebit (sandbox app):** the switch warning, the tick, then a world restart. `Gloebit.dll` is in the runtime.
   The region log shows Gloebit's module initialising with environment sandbox and "secret configured", and no secret.
4. **In-world:** the status bar shows G$, and Gloebit's authorize IM arrives on region entry. Paying an avatar starts
   Gloebit's flow. Completing a payment needs Gloebit's callbacks, which a LAN-only grid can't receive: record how far
   it got.
5. **Back to Play:** the balances from row 2 are unchanged, and the self-check passes.
6. **Region Server:** with Gloebit on the Grid Asset Server and no app on the VM, R1 refuses payments. With the owner's
   sandbox app entered on the VM, R1 runs Gloebit.

Results go in `docs/superpowers/plans/2026-11-14-money-plan4-live-checks.md`.

### Task 9: Review, PR and merge

- [ ] **Step 1:** Run a fresh, independent review of the branch against `main` (after PR 84 merges). Fix every Critical
  and Important finding with a test, and record the minors.
- [ ] **Step 2:** Run Task 7 again.
- [ ] **Step 3:** Push and open the PR. Its body lists the live results and says `MoneyRelease` is unchanged.
- [ ] **Step 4:** Wait for every CI check yourself, and merge only when the owner says so.
