# Money plan 3: hypergrid visitors and Region Server pairing — Implementation Plan

> **For the builder:** the owner builds "native": this session does every task itself with superpowers:executing-plans,
> test-first (superpowers:test-driven-development), with no subagent-driven execution. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Hypergrid visitors get wallets that only their real home grid can open, and staff can find, fix and merge them.
A Region Server pairs with its Grid Asset Server's money service with a one-time code, and its regions then take payments
like the Grid Asset Server's own. Money stays Off in every build, because `MoneyRelease` is unchanged.

**Architecture:**
- **Visitors (Grid Asset Server, Local, Simple Public Server).** The ledger opens a visitor wallet only for a UUID and
  home that the Gatekeeper wrote into Robust's `GridUser` table when that visitor arrived. With visitor wallets off, the
  service answers a new code, `visitors_off`, for a visitor on either side. Staff pages read balances without creating
  wallets, can find visitors by name, and can merge a visitor's wallet into the one under their new home address.
- **The ledger stays where Robust runs.** A new rule in `MoneyGate` says where money runs. The ledger, its database and the
  service run only on a plan that runs Robust. A Region Server follows the mode its Grid Asset Server publishes. A
  Connected Server never has money (design §4).
- **LAN listening.** On a Grid Asset Server the money service listens on every address, and drops any connection from an
  address that isn't loopback, private or link-local.
- **Pairing.**
  - The Grid Asset Server publishes its money mode, money port and certificate fingerprint in its grid info.
  - Its Money page makes a one-time pairing code.
  - The Region Server's new Money page sends a proof of the code over HTTPS pinned to the published fingerprint. The proof
    is bound to the certificate the Region Server actually saw, so a machine in the middle can't use it. The Grid Asset
    Server answers with a key and its own proof.
  - The Grid Asset Server stores only the key's hash and the Region Server's address.
  - The key acts for avatars in the regions Robust lists at that address, never in the Grid Asset Server's own regions.

**Tech Stack:** .NET 10; ASP.NET Core (Kestrel, Razor Pages); `HMACSHA256`, `SHA256`, `RandomNumberGenerator`;
`SocketsHttpHandler` with certificate pinning; MySqlConnector; xunit.v3 on Microsoft.Testing.Platform; MariaDB 11.4.9 for
the integration tests.

**Spec:** `docs/superpowers/specs/2026-11-13-money-design.md` (owner-approved): §7.1 (wallet merge), §7.2 (who can reach the
service), §7.6, §7.7, §9.1 (People, Region Servers), §11. This is step 4 of spec §14. Plans 1 and 2
(`2026-11-13-money-plan1-ledger-service.md`, `2026-11-13-money-plan2-region-module-helper-pages.md`, merged in PRs 80 and
81) built the ledger, the service, the region module and the pages this plan extends.

**Decisions (the builder's, within spec §15 "the pairing exchange's details"):**
- **Which regions a Region Server's key covers.**
  - It covers the regions in Robust's `regions` table whose `serverIP` equals the address the Region Server gave when it
    paired. That is the `ExternalHostName` its regions register with.
  - The Grid Asset Server's own regions are never covered.
  - The Grid Asset Server can't know the Region Server's regions any other way. Robust's private port is open to the LAN
    (plan 6b, owner: "keep it basic"), so a machine that re-registers someone else's region in Robust takes that region
    over anyway. The key then acts only for avatars that machine really hosts. That is spec §11's "acts only for avatars
    currently in its regions".
  - One address is paired to one server. Pairing the same address again replaces the old key.
- **A visitor's home is checked against Robust's `GridUser` table, not taken from the region.**
  - The Gatekeeper writes `GridUser.UserID = "<uuid>;<HomeURI>;<First Last>"` for every visitor it lets in
    (`GatekeeperService.cs:622-639`).
  - A visitor wallet is created only when such a row exists for that UUID with the same home address, compared as the
    ledger already compares homes: trimmed, lower case, no trailing slash.
  - An existing wallet needs no new check.
  - This closes plan 2's deferred minor ("a made-up `home=` makes a junk visitor wallet") for regions and staff pages alike.
- **With visitor wallets off,** an existing visitor wallet is kept but can't pay or be paid (spec §7.6). Staff can still
  give, take and merge, so the owner can tidy up.
- **An unpaired Region Server's regions** run `GridGateMoneyModule` in a refusing state. Every payment answers "This region
  isn't connected to the grid's money yet." (spec §7.7). Regions don't fall back to OpenSim's sample module, which would
  show different prices.
- **Pairing codes** live only in the Grid Asset Server's memory:
  - one at a time;
  - valid for 15 minutes;
  - cancelled after 5 wrong proofs;
  - gone when the launcher restarts.
- **The Region Server's key** is kept in its owner-only settings file, as local program keys are (spec §7.2 "where local
  keys live"). Every program on the Region Server uses it. The Grid Asset Server keeps only its SHA-256 hash.
- **A changed certificate.** The money certificate renews 30 days before it expires (after about 5 years). The Region
  Server's Money page then says the certificate changed and asks for a new pairing. A pairing is never re-pinned without
  a code.
- **Connected Server:** nothing changes. It never runs money, and it writes no money keys, as today (design §4).

**Deferred minors from plan 2's final reviews, decided here:**

| Minor | Here? |
|---|---|
| Staff person page GET creates a wallet; a made-up `home=` makes a junk visitor wallet | Yes (Tasks 1, 3) |
| `VisitorsOffText` unused; a visitor payer with visitor wallets off gets "That person can't receive coins" | Yes (Tasks 1, 2) |
| Find a person asks for `int.MaxValue` rows; visitors can't be found there | Yes (Task 3). Building found the first half wasn't a bug: `int.MaxValue` is the unused staff level, and the directory pages at 25. It's now passed by name as 0. |
| Give, take and move-fees audit lines don't record the amount | Yes (Task 3, same page) |
| Currency symbol not re-validated at INI write time | Yes (Task 6, the INI writer is changed here) |
| `account_frozen` shows the grid-wide "paused" text | Yes (Task 2: one text, same file) |
| Buyer's "you paid" notice sent at hold time; `Run` blocks a pool thread; `_balances` keeps every payee; `RegionLoaded` handlers never unsubscribed | No: module internals unrelated to visitors or pairing. Left for plan 5's review. |
| `EconomyLimits` growth; My coins dates unlabelled; Off refuses priced pre-flight; classifieds free until the first economy answer; saved classified keeps the typed price; client failure-callback and logging minors | No: other areas. Left for plan 5's review. |

The two open findings in `2026-11-13-money-plan2-live-checks.md` (first-login `G$ 0`, Firestorm's unpublished-classifieds
warning) aren't in this plan. If either shows up in Task 9's live checks, it's recorded there.

**Later plans:**
- Plan 4: Gloebit, including Gloebit on a Region Server.
- Plan 5: the AI review gate, and starting or stopping the money service when the mode changes.
- Plan 6: real coins.

## Global Constraints

- **The gate.**
  - `MoneyRelease.PlayCoinsApproved` and `RealCoinsApproved` stay `false` in every commit.
  - Code reads the effective mode only through `MoneyGate` or `MoneyPlace` (Task 4), never from the constants.
  - Task 9's live checks flip the constants in the working tree only, and never commit them.
- **Mode Off changes nothing a release build writes today.**
  - The grid info keys added here carry `gridgate_money = off` and nothing else.
  - No pairing code can be made.
  - A Region Server writes no `[GridGateMoney]` section.
  - Snapshot tests prove it: the only new line in the Robust INI snapshots is `gridgate_money = off`.
- **No secret leaves its place.**
  - The pairing code, the key, the proofs and session ids are never logged, never put in the audit log, and never sent
    back in an error.
  - The Grid Asset Server stores the key's hash only.
  - The pairing code is shown once, on the page that made it.
  - Every comparison of a code, proof or key uses `CryptographicOperations.FixedTimeEquals`.
- **Fixed answers.** A pairing that fails for any reason (no code, an expired code, a wrong proof, a cancelled code)
  gets one answer, `{"code":"refused"}`, and counts as a failure for that address in `MoneyAuthFailures`.
- **Owner-facing words.** Pages say "Region Server", "Grid Asset Server", "pairing code" and "the grid's money", never
  INI or key names. Staff pages are English only. Viewer texts are English and at most 250 characters.
- **Windows and Debian behave the same.** `TreatWarningsAsErrors` is on.
- **Integration tests** use `GRIDGATE_MARIADB_BIN=C:\Users\ystud\AppData\Local\DreamGridDev\mariadb-11.4.9-winx64\bin` and
  run with `GRIDGATE_REQUIRE_INTEGRATION=1`, with no skips.
- **Branch and commits.**
  - The work is on `feature/money-plan3`, in the worktree `C:\Users\ystud\Desktop\CODE\GridGate-money-plan3`.
  - Check `git branch --show-current` before every commit, and add only this plan's files.
  - Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A machine in the middle of pairing.** A LAN attacker swaps the fingerprint in the grid info and answers the Region
   Server itself. It gets a proof it can't replay, because the proof is bound to its own certificate. It can't fake the
   server proof, so the Region Server saves nothing. Tests: Task 5 `Proof_for_another_certificate_is_refused`, Task 6
   `Server_without_the_code_is_not_trusted`.
2. **A Region Server's key reaches past its own regions.** It must never act for an avatar in a Grid Asset Server region,
   or in another paired server's region, even when Robust lists that region at the Region Server's address. Tests: Task 5
   `Server_key_never_covers_the_grids_own_regions`, `Server_key_covers_only_its_address`.
3. **Removing a Region Server ends its key on the next request,** with no restart. Test: Task 5
   `Removed_server_is_refused_at_once`.
4. **Junk and duplicate visitor wallets.** A made-up home, or a home for which Robust has no `GridUser` row, creates
   nothing. The same home spelled with another case or a trailing slash finds the same wallet. Tests: Task 1
   `Unknown_visitor_home_gets_no_wallet`, `Known_visitor_home_matches_without_case_or_slash`.
5. **The ledger on a computer without Robust.** A Region Server or Connected Server whose settings still say Play (left
   over from an earlier plan) must not start the money service, create the money database or make program keys. Tests:
   Task 4 `Region_server_never_runs_the_ledger`, `Connected_server_has_no_money`.
6. **Someone on the internet reaches the money port** of a Grid Asset Server that has a public address. The connection is
   dropped before TLS. Test: Task 4 `Public_address_is_dropped`.

---

### Task 1: Visitor wallets in the ledger

**Files:**
- Modify: `Launcher/GridGate.Launcher.Core/Money/MoneyLedger.cs`. Its constructor takes
  `Func<Guid, string, CancellationToken, Task<bool>> isKnownVisitor`. `ResolveAsync` gains `bool staff = false`.
- Modify: `Launcher/GridGate.Launcher.Core/Money/MoneyTypes.cs`. Add `MoneyOutcome.VisitorsOff`.
- Modify: `Launcher/GridGate.Launcher.Core/Money/MoneyLedger.Staff.cs`. Staff moves pass `staff: true`. Add `FindBalanceAsync`.
- Modify: `Launcher/GridGate.Launcher.Core/Money/IMoneyBooks.cs`. Add `FindBalanceAsync` and `MergeWalletsAsync`.
- Modify: `Launcher/GridGate.Launcher.Core/Money/PresenceCheck.cs`. Add `IsKnownVisitorAsync`.
- Modify: `Launcher/GridGate.Launcher/Program.cs`. Pass `PresenceCheck.IsKnownVisitorAsync` to the ledger.
- Modify: `Launcher/GridGate.Launcher/Money/MoneyPageRules.cs`. Add words for `VisitorsOff`.
- Modify: every `new MoneyLedger(` in the integration tests (`MoneyTestDatabase` and its callers).
- Test: `Launcher/GridGate.Launcher.IntegrationTests/MoneyLedgerVisitorTests.cs` (new),
  `Launcher/GridGate.Launcher.IntegrationTests/PresenceCheckTests.cs` (extend)

**Interfaces:**
- **`PresenceCheck.IsKnownVisitorAsync(Guid id, string home, CancellationToken ct) -> Task<bool>`.**
  - It runs `SELECT UserID FROM GridUser WHERE UserID LIKE @prefix`, with `@prefix = id.ToString() + ";%"`.
  - It is true when one row's second `;` part, normalized, equals `home` normalized.
  - "Normalized" is `MoneyLedger.NormalHome`, which becomes `internal static` so this check and the ledger share it.
  - An empty or whitespace `home` is false, with no query.
  - A database failure throws, as `IsLocalUserAsync` does, and the ledger turns it into `Unavailable`.
- **`MoneyOutcome.VisitorsOff`,** wire code `visitors_off`.
- **`ResolveAsync(conn, tx, who, ct, bool staff = false)`:**
  - For a visitor, when `!settings().VisitorWallets && !staff`, it answers `VisitorsOff` before looking anything up. That
    holds for existing wallets too.
  - When no wallet exists, a new visitor wallet needs `await isKnownVisitor(who.AgentId, home, ct)`; otherwise
    `NoWallet`. This holds for staff too: staff can't make a junk wallet either.
  - With wallets off and `staff` true, an existing wallet is found and no new one is made.
- **Staff moves.** `GiveAsync`, `TakeAsync`, `MoveFeesAsync` and `MergeWalletsAsync` call `ResolveAsync(…, staff: true)`.
- **`IMoneyBooks.FindBalanceAsync(AccountRef who, CancellationToken ct) -> Task<MoneyResult>`.**
  - `Ok` with the balance when the wallet exists, and `NoWallet` when it doesn't.
  - It never creates a wallet and never applies the allowance.
  - It's a plain `SELECT balance`, through `ReadAsync`.
- **`IMoneyBooks.MergeWalletsAsync(AccountRef from, AccountRef to, StaffAction who, CancellationToken ct) -> Task<MoneyResult>`.**
  The existing ledger method, now on the interface. The page tests' fake books implement it.

- [ ] **Step 1: Write the failing tests**

```csharp
// MoneyLedgerVisitorTests (MoneyTestDatabase; a fake isKnownVisitor over a set of (id, home) pairs)
[Fact] Task Unknown_visitor_home_gets_no_wallet()               // BalanceAsync(visitor, "http://made.up/") → NoWallet; no account row
[Fact] Task Known_visitor_home_matches_without_case_or_slash()  // known "http://Grid.Example:8002/"; Balance with "http://grid.example:8002" → Ok 0; one row
[Fact] Task Visitor_wallets_off_refuses_both_sides()            // existing visitor wallet with 10; VisitorWallets false: transfer visitor→resident and resident→visitor → VisitorsOff; balances unchanged
[Fact] Task Visitor_wallets_off_still_lets_staff_take_back()    // TakeAsync(visitor, 10, staff) → Ok
[Fact] Task Staff_cannot_make_a_junk_visitor_wallet()            // GiveAsync(unknown visitor) → NoWallet; no row
[Fact] Task Find_balance_never_creates()                          // FindBalanceAsync(resident without wallet) → NoWallet; no row; no starting_balance entry
[Fact] Task Merge_moves_everything_and_freezes_the_old()         // existing test of MergeWalletsAsync, now through IMoneyBooks
[Fact] Task Known_visitor_check_failing_is_unavailable()         // isKnownVisitor throws MySqlException → Unavailable, nothing written
// PresenceCheckTests (Robust test database with a GridUser table)
[Fact] Task Gatekeeper_row_makes_a_known_visitor()               // row "<id>;http://grid.example:8002/;Ann Smith" → true for "http://GRID.example:8002"
[Fact] Task Other_home_or_other_id_is_not_known()                 // same row → false for "http://other.example/" and for another id
[Fact] Task Empty_home_is_not_known_without_a_query()             // closed connection factory → false, no throw
```

- [ ] **Step 2: Run them and see them fail.** With `GRIDGATE_MARIADB_BIN` set, run
  `dotnet test --project Launcher/GridGate.Launcher.IntegrationTests -c Release -- --filter-class "*MoneyLedgerVisitor*" --filter-class "*PresenceCheck*"`.
  Expected: the build fails, because `VisitorsOff` and `FindBalanceAsync` aren't defined.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run every `*Money*` and `*PresenceCheck*` integration class and see them pass.**
- [ ] **Step 5: Commit** — `feat(money): visitor wallets only for homes the Gatekeeper recorded; visitors_off`

---

### Task 2: The region module: visitor and unpaired texts

**Files:**
- Modify: `addon-modules/GridGateMoney/MoneyModels.cs`. Add the `visitors_off` and `not_paired` texts, and a separate
  `account_frozen` text.
- Create: `addon-modules/GridGateMoney/UnpairedMoneyService.cs`, an `IMoneyService` that answers `not_paired` to every call
  and `null` to `EconomyAsync`.
- Modify: `addon-modules/GridGateMoney/GridGateMoneyModule.cs`, `Initialise`.
- Modify: `addon-modules/GridGateMoney/GridGateMoney.csproj` (the Compile list).
- Test: `tests/GridGate.Money.Tests/MoneyActionsPayTests.cs`, `tests/GridGate.Money.Tests/ModuleLoadTests.cs`,
  `tests/GridGate.Money.Tests/MoneyActionsTests.cs`

**Interfaces:**
- **`MoneyRules.Message(string code)`:**

| Code | Text |
|---|---|
| `visitors_off` | `MoneyRules.VisitorsOffText`: "Visitors can't pay or be paid on this grid." |
| `not_paired` | "This region isn't connected to the grid's money yet." |
| `account_frozen` | "Your coins are frozen. Please contact the grid's staff." |
| `frozen` | unchanged: "Payments are paused on this grid right now." |

- **`Payout`** maps `visitors_off` to `LINDENDOLLAR_ENTITYDOESNOTEXIST` (`ScriptReason`) and `not_paired` to
  `SERVICE_ERROR`. Its own visitor check before the call stays.
- **The INI contract gains one key**, and the four existing keys are unchanged:

```ini
[GridGateMoney]
Paired = false        ; only on an unpaired Region Server; RegionKey and ServiceFingerprint are then absent
HelperURI = <the grid's economy helper>
```

  - With `Paired = false` and a non-empty `HelperURI`, `Initialise` enables the module with `UnpairedMoneyService`. It logs
    "Money is on, but this Region Server isn't paired with its grid's money yet." `ServiceURI`, `RegionKey` and
    `ServiceFingerprint` aren't needed then.
  - Any other value of `Paired`, or a missing one, keeps today's rule: all four keys are required.
- **The balance push** on region entry gets `not_paired` and pushes nothing, as for `unavailable`. That is plan 2's
  existing rule for any refusal.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] void Visitors_off_has_its_own_text()                 // Message("visitors_off") == VisitorsOffText
[Fact] void Not_paired_has_its_own_text()
[Fact] void Account_frozen_is_not_the_grid_freeze()          // Message("account_frozen") != Message("frozen")
[Fact] void Messages_fit_250_chars()                          // extended to the new codes
[Fact] Task Visitor_payer_with_wallets_off_is_told_so()      // fake service answers visitors_off to a visitor's pay → Tell(VisitorsOffText)
[Fact] Task Unpaired_service_refuses_every_action()          // MoneyActions over UnpairedMoneyService: pay → Tell(not_paired text), buy → deliver never called, payout false + SERVICE_ERROR
[Fact] void Module_enabled_unpaired_with_helper_only()       // ModuleLoadTests: EconomyModule + Paired=false + HelperURI → Enabled, IsPaired false
[Fact] void Unpaired_without_helper_stays_off()              // Paired=false, no HelperURI → not Enabled
```

- [ ] **Step 2: Run them and see them fail.** Run `dotnet test --project tests/GridGate.Money.Tests -c Release`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them and see them pass.** Then run `dotnet build GridGate.sln -c Release` and expect 0 errors.
- [ ] **Step 5: Commit** — `feat(money): module texts for visitors off, frozen accounts, and an unpaired Region Server`

---

### Task 3: Staff pages for visitors

**Files:**
- Modify: `Launcher/GridGate.Launcher/Pages/Money/Person.cshtml(.cs)`
- Modify: `Launcher/GridGate.Launcher/Pages/Money/Index.cshtml(.cs)`. Find a person.
- Create: `Launcher/GridGate.Launcher.Core/Money/VisitorDirectory.cs`
- Modify: `Launcher/GridGate.Launcher/Program.cs`. Register `IVisitorDirectory`.
- Test: `Launcher/GridGate.Launcher.Tests/MoneyPageTests.cs` (extend, with `FakeBooks` and a fake directory),
  `Launcher/GridGate.Launcher.IntegrationTests/VisitorDirectoryTests.cs` (new)

**Interfaces:**
- **`public interface IVisitorDirectory`:**
  - `Task<IReadOnlyList<VisitorInfo>> SearchAsync(string text, int max, CancellationToken ct)`
  - `Task<VisitorInfo?> FindAsync(Guid id, string home, CancellationToken ct)`
  - `public sealed record VisitorInfo(Guid Id, string Home, string Name)`
- **`VisitorDirectory(Func<CancellationToken, Task<MySqlConnection>> openRobust) : IVisitorDirectory`:**
  - It reads Robust's `GridUser` rows whose `UserID` has three `;` parts, and parses them as `id;home;First Last`.
  - `SearchAsync` matches `text` against the name part with `LIKE CONCAT('%', @t, '%')`, with `%`, `_` and `\` escaped,
    up to `max` (clamped to 1..100) rows. The newest `Login` comes first.
  - `FindAsync` matches the id and the normalized home.
  - A database failure throws `MoneyUnavailableException`.
- **Find a person** on `/money`:
  - It passes the staff level by name (0, unused for Everyone). The directory already returns one page of 25.
  - It also shows up to 50 visitors from `SearchAsync`, each as "Name (visitor from host)", linking to
    `/money/person?id=…&home=…`.
- **The person page:**
  - **Reading.** The balance comes from `FindBalanceAsync`. With no wallet it shows "No wallet yet. Giving coins makes one."
    The page never creates a wallet when it is opened.
  - **The visitor's name** is `"{VisitorInfo.Name} (visitor from {host})"`. A visitor the directory doesn't know, and who
    has no wallet, is a 404. A known visitor without a wallet is shown, with the "No wallet yet" line.
  - **Merge**, admin only and for visitors only:
    - a "Move to a new home address" form, with the new home address and a required reason;
    - it runs `MergeWalletsAsync(Who, new AccountRef(Id, newHome), …)`;
    - it is audited as `money-merge-wallet`, with target the visitor's name and result the outcome's code.
  - **Audit amounts.** `money-give`, `money-take` and `money-move-fees` lines put the amount in the result:
    `ok 25` or `not_enough 25`.

- [ ] **Step 1: Write the failing tests**

```csharp
// MoneyPageTests
[Fact] Task Opening_a_person_creates_no_wallet()            // FakeBooks records calls: GET → FindBalanceAsync only, never BalanceAsync
[Fact] Task Made_up_visitor_home_is_404()                   // directory knows nothing, FindBalance NoWallet → 404
[Fact] Task Known_visitor_without_wallet_is_shown()         // "No wallet yet"
[Fact] Task Find_shows_visitors()                           // directory returns Ann Smith @grid.example → "Ann Smith (visitor from grid.example)" link with home
[Fact] Task Merge_is_audited_and_admin_only()               // supervisor → 403; admin → MergeWalletsAsync called; audit "money-merge-wallet"
[Fact] Task Audit_lines_carry_the_amount()                   // give 25 → result "ok 25"
// VisitorDirectoryTests (Robust test database)
[Fact] Task Search_finds_gatekeeper_rows_by_name()           // rows for a visitor and a resident (UserID plain uuid): only the visitor
[Fact] Task Search_escapes_like_wildcards()                   // "%" matches nothing special
[Fact] Task Find_matches_home_without_case_or_slash()
```

- [ ] **Step 2: Run them and see them fail.** Run
  `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release -- --filter-class "*MoneyPageTests"`, then the
  integration class.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them, plus `*StaffMenu*` and `*AccessTests*`, and see them pass.**
- [ ] **Step 5: Commit** — `feat(money): staff find, read and merge visitor wallets without making junk ones`

---

### Task 4: Where money runs, and LAN listening

**Files:**
- Create: `Launcher/GridGate.Launcher.Core/Money/MoneyPlace.cs`
- Modify: `Launcher/GridGate.Launcher.Core/Money/MoneyGate.cs`
- Modify: `Launcher/GridGate.Launcher.Core/Grid/GridController.cs` (`WriteConfig` keys, `StartDatabaseAsync` money
  database) and `Launcher/GridGate.Launcher/Program.cs` (the service host, maintenance and ledger read `MoneyGate.ServiceHere`)
- Modify: `Launcher/GridGate.Launcher/Money/MoneyServiceHost.cs`. The listen addresses, and a connection filter.
- Modify: `Launcher/GridGate.Launcher.Core/Plans/PlanCatalog.cs`. Add one advisory line on Simple Public Server and on Grid
  Asset Server.
- Test: `Launcher/GridGate.Launcher.Tests/MoneyPlaceTests.cs` (new), `Launcher/GridGate.Launcher.Tests/MoneyGateTests.cs`,
  `Launcher/GridGate.Launcher.IntegrationTests/MoneyServiceTests.cs` (extend), `Launcher/GridGate.Launcher.Tests/PlanCatalogTests.cs`

**Interfaces:**
- **`public static class MoneyPlace`:**
  - `MoneyMode GridMode(LauncherSettings s, Func<MoneySettings, MoneyMode> gate)` is the mode of the grid this computer
    belongs to:
    - Robust here: `gate(s.Money)`;
    - Region Server: `gate(s.Money with { Mode = s.Join?.Money ?? MoneyMode.Off })`;
    - Connected Server: `Off`.
  - `bool ServiceHere(LauncherSettings s, Func<MoneySettings, MoneyMode> gate) =>
    PlanCatalog.RunsRobust(s) && GridMode(s, gate) is MoneyMode.Play or MoneyMode.Real`.
- **`MoneyGate`:**
  - `Effective()` becomes `MoneyPlace.GridMode(settings(), _effective)`.
  - A new `bool ServiceHere` property.
  - `Allows(mode)` is unchanged: what the Money page may offer.
- **What uses `ServiceHere` instead of `!= Off`:**
  - `MoneyServiceHost.StartAsync`;
  - `MoneyMaintenance`;
  - `MoneyStartup.TryPrepareAsync`, in `GridController.StartDatabaseAsync`;
  - `GridController.WriteConfig`'s `EnsureProgramKeys`;
  - the Money page's ledger sections, totals and dashboard notices (`MoneyNotices`);
  - My coins, through `MoneyPagesOnlyAttribute`.
- **`GridController` and `GridConfigWriter`** keep their `Func<MoneySettings, MoneyMode>` and call `MoneyPlace` with it.
- **`MoneyServiceHost.Build`:**
  - On `OwnerPlan.GridAsset` it calls `o.ListenAnyIP(port, …)`; otherwise `o.Listen(IPAddress.Loopback, port, …)`, as
    today.
  - On a Grid Asset Server, a Kestrel connection middleware (`ListenOptions.Use`) closes any connection whose remote address
    `GridProbe.IsPublic` says is public, before TLS. It's counted in `MoneyAuthFailures` and logged with the address only.
  - The log line names the addresses it listens on.
- **The advisory line** (Simple Public Server and Grid Asset Server):
  "Never forward the money port (8000 unless you changed it): only this computer and your network's Region Servers may
  reach it."

- [ ] **Step 1: Write the failing tests**

```csharp
// MoneyPlaceTests
[Fact] void Region_server_never_runs_the_ledger()        // Plan RegionServer, Money.Mode Play, Join.Money Play, open gate → GridMode Play, ServiceHere false
[Fact] void Region_server_follows_its_grid()             // Join.Money Off → Off, whatever s.Money.Mode says
[Fact] void Connected_server_has_no_money()              // Plan Connected, Money.Mode Play → Off
[Fact] void Closed_gate_is_off_everywhere()              // default gate, every plan → Off
[Fact] void Grid_asset_runs_the_ledger_when_on()         // GridAsset, Play, open gate → ServiceHere true; Gloebit → false
// GridController (GridControllerWiringTests, unit)
[Fact] Task Region_server_makes_no_program_keys()        // RS, Money.Mode Play, open gate → settings keep ProgramKeys null after RegenerateConfig
// MoneyServiceTests (integration)
[Fact] Task Grid_asset_listens_beyond_loopback()         // Plan GridAsset: TCP connect to the machine's first non-loopback IPv4 succeeds and TLS handshake gives the pinned cert
[Fact] Task Public_address_is_dropped()                   // the filter as a unit: a fake ConnectionContext with RemoteEndPoint 203.0.113.5 → closed, failure recorded; 192.168.1.9 → passes
[Fact] Task Local_plan_still_listens_on_loopback_only()   // Plan Local: connecting to the non-loopback address is refused
// PlanCatalogTests
[Fact] void Public_plans_say_not_to_forward_the_money_port()
```

- [ ] **Step 2: Run them and see them fail** (the unit and integration classes above).
- [ ] **Step 3: Implement.** Keep the filter as a small `internal static class MoneyNetworkFilter { bool Allowed(IPAddress) }`
  plus the middleware, so the unit test needs no network.
- [ ] **Step 4: Run them, then all `*Money*` unit and integration classes, and see them pass.**
- [ ] **Step 5: Commit** — `feat(money): the ledger runs only where Robust runs; the Grid Asset Server's money port is open to the LAN only`

---

### Task 5: Pairing on the Grid Asset Server

**Files:**
- Create: `Launcher/GridGate.Launcher.Core/Money/MoneyPairing.cs`. The code format, the proofs, and the pending code.
- Create: `Launcher/GridGate.Launcher.Core/Money/RegionHosts.cs`. Which regions Robust lists at an address.
- Modify: `Launcher/GridGate.Launcher.Core/Money/MoneySettings.cs`. Add `PairedServers` and `Pairing` (Task 6 uses
  `Pairing`).
- Modify: `Launcher/GridGate.Launcher.Core/Money/RegionKeyRing.cs`. Add the server grants.
- Modify: `Launcher/GridGate.Launcher/Money/MoneyApi.cs`. Add `POST /money/v1/pair`, and resolve server grants per request.
- Modify: `Launcher/GridGate.Launcher.Core/Config/GridConfigWriter.cs`. Publish the money keys in the grid info.
- Modify: `Launcher/GridGate.Launcher.Core/Plans/GridJoin.cs`. Add the `GridInfoKeys` money keys.
- Modify: `Launcher/GridGate.Launcher/Pages/Money/Index.cshtml(.cs)`. Add the Region Servers section.
- Modify: `Launcher/GridGate.Launcher/Program.cs`. Register `MoneyPairing` and `RegionHosts`.
- Modify: `Launcher/GridGate.Launcher.Tests/Snapshots/*`. Regenerate; the only new line is `gridgate_money = off`.
- Test: `Launcher/GridGate.Launcher.Tests/MoneyPairingTests.cs` (new), `Launcher/GridGate.Launcher.IntegrationTests/MoneyPairServiceTests.cs` (new),
  `Launcher/GridGate.Launcher.IntegrationTests/RegionHostsTests.cs` (new), `Launcher/GridGate.Launcher.Tests/MoneyPageTests.cs`,
  `Launcher/GridGate.Launcher.Tests/MoneyConfigTests.cs`

**Interfaces:**
- **`MoneySettings` gains:**
  - `List<PairedServer>? PairedServers = null`, with `public sealed record PairedServer(Guid Id, string Host, string KeyHash, DateTimeOffset PairedAt)`.
    `KeyHash` is upper-case hex SHA-256 of the key's UTF-8 bytes.
  - `ServerPairing? Pairing = null`, the Region Server's own side (Task 6), with
    `public sealed record ServerPairing(string GridHost, string Fingerprint, string Key, DateTimeOffset PairedAt)`.
- **`public static class PairingCode`:**
  - `const int Length = 16`. The alphabet is Crockford base32, `0123456789ABCDEFGHJKMNPQRSTVWXYZ`, so 80 bits.
  - `string New()` uses `RandomNumberGenerator.GetItems`.
  - `string Show(string code)` is `XXXX-XXXX-XXXX-XXXX`.
  - `string? Normalize(string? typed)`:
    - upper case;
    - drops spaces and `-`;
    - maps `O` to `0`, and `I` and `L` to `1`;
    - null unless exactly 16 alphabet characters are left.
- **`public static class PairingProof`:**
  - `const string Version = "gridgate-pair-v1"`.
  - `string Client(string code, string fingerprint, string nonce, string host)` is
    `HMAC-SHA256(key: UTF8(code), "gridgate-pair-v1|client|{fingerprint}|{nonce}|{host}")`, as upper-case hex.
  - `string Server(string code, string fingerprint, string nonce, string key)` is
    `HMAC-SHA256(key: UTF8(code), "gridgate-pair-v1|server|{fingerprint}|{nonce}|{key}")`, as upper-case hex.
  - `bool Same(string a, string b)` compares in constant time over the hex bytes.
- **`public sealed class MoneyPairing(TimeProvider time)`,** a singleton:
  - `const int MaxWrongProofs = 5`, and `static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15)`.
  - `(string Code, DateTimeOffset Expires) Issue()` replaces any pending code.
  - `void Cancel()`.
  - `DateTimeOffset? PendingUntil`.
  - `string? Redeem(Func<string, bool> proofMatches)`:
    - it returns the code and clears it when `proofMatches(code)` is true;
    - otherwise it counts a wrong proof, cancels the code at 5, and returns null;
    - an expired code is cleared and null.
  - It is thread-safe, under one lock.
- **`RegionKeyRing`:**
  - `RegionKeyGrant` becomes `(string KeyId, IReadOnlySet<Guid> Regions, string? ServerHost = null)`.
  - `FromSettings` adds one grant per `PairedServer`: `KeyId = "server:" + Id`, `Regions` empty, and
    `ServerHost = Host`. Its hash comes from `KeyHash`, not from a key.
- **`public sealed class RegionHosts(Func<CancellationToken, Task<MySqlConnection>> openRobust)`:**
  - `Task<IReadOnlySet<Guid>> RegionsAtAsync(string host, CancellationToken ct)` runs
    `SELECT uuid FROM regions WHERE serverIP = @host`.
  - A database failure throws `MoneyUnavailableException`.
- **`MoneyApi.Handle`.** After authenticating, a grant with a `ServerHost` gets its `Regions` from `RegionsAtAsync`, minus
  every UUID in `settings().Regions`. A failure answers `unavailable`.
- **`POST /money/v1/pair`** takes `{ host, nonce, proof }`. It needs no region key, and the only error answer is `refused`.
  1. Refuse when `!MoneyPlace.ServiceHere(s)` or the plan isn't `GridAsset`.
  2. `host` must pass `SettingsValidator.HostnameError` and be at most 255 characters. `nonce` must be base64 of exactly
     32 bytes. `proof` must be 64 hex characters.
  3. `code = pairing.Redeem(c => PairingProof.Same(PairingProof.Client(c, ownFingerprint, nonce, host), proof))`, where
     `ownFingerprint` is the fingerprint of the certificate this listener serves.
  4. On a match:
     - `key = Secrets.NewPassword(MoneyKeys.KeyLength)`;
     - save through `grid.UpdateSettings(…, writeConfig: false)`: drop any `PairedServer` with the same host
       (case-insensitive), then add the new one;
     - write the launcher log line "Paired a Region Server at {host}.", which names the host only;
     - answer `{ code: "ok", key, proof: PairingProof.Server(code, ownFingerprint, nonce, key) }`.
  5. Otherwise answer `refused`, and call `failures.RecordFailure(ip)` with its slowdown.
- **The grid info**, in `GridConfigWriter`, written only where Robust runs:
  - Always: `gridgate_money = <off|play|real|gloebit>`, the effective mode in lower case.
  - When the mode is Play or Real: also `gridgate_money_port = <MoneyPort>` and
    `gridgate_money_fingerprint = <MoneyCertificate.Fingerprint>`.
- **The Money page** gets a Region Servers section, shown on Grid Asset Server only, and only when `ServiceHere`:
  - "Make a pairing code" (admin). It shows the code once, as `PairingCode.Show`, with "Type this on the Region Server's
    Money page within 15 minutes. It works once." It's audited as `money-pairing-code`, without the code.
  - While a code is pending: "A pairing code is waiting until {time}", and a Cancel button (`money-pairing-cancel`).
  - The paired servers: their address and when they were paired, each with Remove (admin, `money-pairing-remove`, target the
    host). Remove saves at once: "Removed. Its regions can't take payments until it's paired again."
  - A paragraph: "A Region Server's key works only for its own regions, never for this computer's."

- [ ] **Step 1: Write the failing tests**

```csharp
// MoneyPairingTests (unit)
[Fact] void Code_has_80_bits_in_crockford()                 // 16 chars, alphabet only; 1000 codes all different
[Theory] void Typed_codes_normalize()                        // "abcd-efgh-jkmn-pqrs" ok; "O" → "0", "I"/"L" → "1"; 15 chars → null; "U" → null
[Fact] void Proof_is_bound_to_the_fingerprint()             // Client(code, F1, n, h) != Client(code, F2, n, h)
[Fact] void Redeem_works_once()                              // second Redeem with the right proof → null
[Fact] void Five_wrong_proofs_cancel_the_code()              // 5 × false, then the right proof → null
[Fact] void Expired_code_is_refused()                        // FakeTimeProvider +16 min → null
[Fact] void New_code_replaces_the_old()
// MoneyPairServiceTests (integration: real MoneyServiceHost, Plan GridAsset, open gate, MoneyTestDatabase, a Robust test db with a regions table)
[Fact] Task Right_proof_pairs_and_stores_only_a_hash()       // ok + key + server proof that verifies; settings PairedServers[0].KeyHash == SHA256(key); key not in settings file text
[Fact] Task Proof_for_another_certificate_is_refused()       // proof computed with fingerprint "00…" → refused; code still pending (one wrong proof)
[Fact] Task Refusals_all_look_the_same()                     // no code / wrong proof / expired → identical body {"code":"refused"}
[Fact] Task Paired_key_acts_for_its_own_regions()            // regions row (uuid R, serverIP "192.168.1.20"); avatar present in R → balance ok
[Fact] Task Server_key_never_covers_the_grids_own_regions()  // regions row for a s.Regions uuid at 192.168.1.20 → wrong_region
[Fact] Task Server_key_covers_only_its_address()            // region at 192.168.1.30 → wrong_region
[Fact] Task Removed_server_is_refused_at_once()              // remove from settings → next call 401
[Fact] Task Pairing_the_same_host_again_replaces_the_key()   // old key 401, new key ok
[Fact] Task Pair_refused_off_grid_asset()                    // Plan Local → refused
[Fact] Task Nothing_secret_is_logged()                       // captured log has no code, key, nonce or proof text
// RegionHostsTests
[Fact] Task Regions_at_an_address()
// MoneyConfigTests
[Fact] void Grid_info_publishes_money_when_on()              // open gate GridAsset Play: gridgate_money play, port, fingerprint == cert
[Fact] void Grid_info_says_off_and_nothing_else_when_off()   // closed gate: gridgate_money off; no port or fingerprint keys
// MoneyPageTests
[Fact] Task Pairing_code_shown_once_and_audited_without_it() // POST make → page shows code; audit line has no code; GET again → no code, "waiting until"
[Fact] Task Region_servers_section_only_on_grid_asset()
[Fact] Task Remove_paired_server_is_admin_only_and_audited()
```

- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.** Then regenerate the snapshots with `GG_CAPTURE_SNAPSHOTS=1`, and check that
  `git diff Launcher/GridGate.Launcher.Tests/Snapshots` shows only `gridgate_money = off`.
- [ ] **Step 4: Run every `*Money*` class, unit and integration, and see them pass.**
- [ ] **Step 5: Commit** — `feat(money): Grid Asset Server pairs Region Servers with a one-time code; their keys cover only their regions`

---

### Task 6: The Region Server's side

**Files:**
- Modify: `Launcher/GridGate.Launcher.Core/Settings/LauncherSettings.cs`. `JoinedGrid` gains `MoneyMode? Money = null`,
  `int? MoneyPort = null`, `string? MoneyFingerprint = null` and `string? Economy = null`.
- Modify: `Launcher/GridGate.Launcher.Core/Plans/GridJoin.cs`. `Parse` reads `gridgate_money`, `gridgate_money_port`,
  `gridgate_money_fingerprint` and `economy`. Missing keys (an older Grid Asset Server) read as null.
- Create: `Launcher/GridGate.Launcher.Core/Money/MoneyPairClient.cs`
- Create: `Launcher/GridGate.Launcher.Core/Money/MoneyPin.cs`. The pinned `SocketsHttpHandler`, the same rule as the module's
  client.
- Modify: `Launcher/GridGate.Launcher.Core/Config/GridConfigWriter.cs`. Money on a Region Server, `[GridInfo] economy`
  there, and the symbol check.
- Create: `Launcher/GridGate.Launcher/Pages/Advanced/Money.cshtml(.cs)` (`/advanced/money`), Region Server only.
- Modify: `Launcher/GridGate.Launcher/Navigation/StaffMenu.cs`. Add a "Money" item under Settings on a Region Server, for
  admins.
- Modify: `Launcher/GridGate.Launcher/Program.cs`. Register `MoneyPairClient`, with its own `HttpClient` per call because
  of the pin.
- Test: `Launcher/GridGate.Launcher.Tests/GridJoinTests.cs`, `Launcher/GridGate.Launcher.Tests/MoneyConfigTests.cs`,
  `Launcher/GridGate.Launcher.Tests/RegionServerMoneyPageTests.cs` (new),
  `Launcher/GridGate.Launcher.IntegrationTests/MoneyPairClientTests.cs` (new, against the real service from Task 5)

**Interfaces:**
- **`public sealed class MoneyPairClient(IGridJoinClient join, TimeProvider time)`:**
  - `Task<PairOutcome> PairAsync(LauncherSettings s, string typedCode, CancellationToken ct)`, with
    `public sealed record PairOutcome(ServerPairing? Pairing, string? Error)`. The steps:
    1. `PairingCode.Normalize`; null → "That isn't a pairing code. It has 16 letters and numbers, like 7K2M-…".
    2. Read the grid info fresh with `join.ReadAsync(s.Join.Host, s.Join.PublicPort)`.
       - `Money` Off or null → "The grid's money is off, so there's nothing to pair."
       - Gloebit → "The grid uses Gloebit. Gloebit is set up on each computer; there's nothing to pair."
       - No port or fingerprint → "The Grid Asset Server's GridGate is too old to pair. Update it first."
    3. `host = HostAddress.Effective(s.Hostname)` and a fresh 32-byte nonce. POST to
       `https://{Join.Host}:{MoneyPort}/money/v1/pair`, pinned to `MoneyFingerprint`, with a 10 s timeout.
    4. On `ok`, check `PairingProof.Server(code, fingerprint, nonce, key)` against the answer's proof. A mismatch →
       "The Grid Asset Server didn't prove it made this code. Nothing was saved." Then return
       `new ServerPairing(Join.Host, fingerprint, key, now)`.
    5. `refused` → "The Grid Asset Server didn't accept this code. Codes work once, for 15 minutes; make a new one there."
       A pin failure or anything unreachable → "Couldn't reach the grid's money service at {host}:{port}."
  - `Task<PairingHealth> CheckAsync(LauncherSettings s, CancellationToken ct)` calls `/money/v1/health` with the saved
    key, and answers `Ok`, `Refused` (401), `CertificateChanged` (pin failed, or the grid info fingerprint differs from the
    saved one) or `Unreachable`.
- **The Region Server's INIs**, in `GridConfigWriter.MoneyFor` / `MoneySection`:
  - **Mode.** `MoneyPlace.GridMode(s, gate)` must be Play or Real; otherwise nothing is written. Gloebit is plan 4.
  - **Paired.** The pairing is valid when `s.Money.Pairing` is set, its `GridHost == Join.Host`, and its
    `Fingerprint == Join.MoneyFingerprint`. Then every program gets:
    - `[Economy] EconomyModule = GridGateMoneyModule`;
    - `[GridGateMoney] ServiceURI = https://{Join.Host}:{Join.MoneyPort}/money/v1/`;
    - `RegionKey = Pairing.Key`;
    - `ServiceFingerprint = Pairing.Fingerprint`;
    - `HelperURI = Join.Economy`.
  - **Not paired.** Every program gets `[Economy] EconomyModule = GridGateMoneyModule` and `[GridGateMoney] Paired = false`,
    `HelperURI = Join.Economy`.
  - **`[GridInfo] economy`** in `GridCommon.ini` is `Join.Economy` when it's set, in every mode, as on a grid that runs
    Robust.
  - **The currency symbol** in `[LoginService] Currency`, where Robust runs, is written only when `MoneyPageRules`'
    symbol rule accepts it, which moves to Core as `MoneySymbolRule.Valid`. Otherwise it's `C$`, with a launcher warning.
    This closes plan 2's deferred minor.
- **`/advanced/money` (Region Server, admin):**
  - It shows the grid's money, from `Join.Money`: "The grid's money is Play coins", or Off, or Gloebit.
  - It shows the pairing: "Paired on {date}", or "Not paired: these regions refuse payments", or "The grid's money
    certificate changed: pair again".
  - A pairing code field and a Pair button. On success it saves `Money.Pairing` through
    `grid.UpdateSettings(…, writeConfig: true)` and says "Paired. Restart the regions to start taking payments." It's
    audited as `money-pair`, with result `ok` or `refused`, never the code.
  - A Check button, which shows the `CheckAsync` result in words.
  - An Unpair button, which clears `Money.Pairing` (`money-unpair`). The page says to remove it on the Grid Asset Server
    too.
  - On any other plan the page is a 404.
- **Changing what the Region Server joined** (Advanced → Join, to another Grid Asset Server) clears `Money.Pairing` in the
  same `UpdateSettings`. A test checks it.

- [ ] **Step 1: Write the failing tests**

```csharp
// GridJoinTests
[Fact] void Parse_reads_the_grids_money()                   // gridgate_money play, port 8000, fingerprint, economy → JoinedGrid fields
[Fact] void Parse_older_grid_asset_has_no_money()           // no money keys → Money null
// MoneyConfigTests (open gate)
[Fact] void Paired_region_server_writes_the_grids_service() // RS + Join(Money Play, port 8000, fp F, Economy E) + Pairing(host, F, K) → ServiceURI https://192.168.1.5:8000/money/v1/, RegionKey K, fp F, HelperURI E, in every program
[Fact] void Unpaired_region_server_refuses_money()          // no Pairing → EconomyModule GridGateMoneyModule, Paired false, HelperURI E, no RegionKey
[Fact] void Changed_certificate_counts_as_unpaired()         // Pairing.Fingerprint != Join.MoneyFingerprint → Paired false
[Fact] void Region_server_with_grid_money_off_writes_nothing() // Join.Money Off → no [GridGateMoney], EconomyModule blank
[Fact] void Region_server_advertises_the_grids_helper()      // GridCommon [GridInfo] economy == Join.Economy
[Fact] void Connected_server_writes_no_money()               // Plan Connected, Money.Mode Play → no [GridGateMoney]
[Fact] void Bad_symbol_never_reaches_the_ini()                // Money.CurrencySymbol "a=b" (hand-edited) → Currency "C$"
// RegionServerMoneyPageTests
[Fact] Task Page_is_404_off_region_server()
[Fact] Task Pairing_saves_and_never_audits_the_code()         // fake pair client ok → settings.Money.Pairing set; audit "money-pair" "ok"; no code text in audit
[Fact] Task Refused_code_saves_nothing()
[Fact] Task Unpair_clears_the_pairing()
[Fact] Task Rejoining_another_grid_clears_the_pairing()
// MoneyPairClientTests (integration, the real service from Task 5 on loopback; join client faked to return its grid info)
[Fact] Task Pairs_with_the_real_service()                     // code from MoneyPairing.Issue → Pairing with the service's fingerprint; then CheckAsync Ok
[Fact] Task Server_without_the_code_is_not_trusted()          // a test server with its own cert answering ok + wrong proof, grid info naming its fingerprint → Error, no Pairing
[Fact] Task Swapped_fingerprint_reaches_nobody()              // grid info fingerprint "00…" → Unreachable error; the real service saw no pair request
[Fact] Task Removed_pairing_checks_as_refused()               // remove on the service side → CheckAsync Refused
```

- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them, the snapshot tests and `*StaffMenu*`, and see them pass.**
- [ ] **Step 5: Commit** — `feat(money): a Region Server pairs with its grid's money and writes it into its regions`

---

### Task 7: Docs

**Files:**
- Modify: `docs/superpowers/specs/2026-11-13-money-design.md`. Add one line under §7.7: "Plan 3 settled the pairing details:
  see `docs/superpowers/plans/2026-11-14-money-plan3-visitors-region-servers.md`, Decisions." No other spec change.
- Modify: the owner help for the Grid Asset Server and Region Server plans, if `docs/` has one for money. If there is
  none, nothing is added (`git grep -n "money" docs/*.md`).

- [ ] **Step 1: Make the edits.** · [ ] **Step 2: Commit** — `docs(money): pairing details live in plan 3`

---

### Task 8: Full automated check

- [ ] **Step 1:** Run `dotnet build GridGate.sln -c Release`. Expected: 0 errors, and no new warnings.
- [ ] **Step 2:** Run `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release` and
  `dotnet test --project tests/GridGate.Money.Tests -c Release`. Expected: all pass. The unit suite takes about 17 minutes;
  run it in the background.
- [ ] **Step 3:** Run, with `GRIDGATE_MARIADB_BIN` set and `GRIDGATE_REQUIRE_INTEGRATION=1`:
  `dotnet test --project Launcher/GridGate.Launcher.IntegrationTests -c Release -- --filter-class "*Money*" --filter-class "*PresenceCheck*" --filter-class "*VisitorDirectory*" --filter-class "*RegionHosts*"`.
  Expected: all pass, with no skips.
- [ ] **Step 4:** Run `dotnet test --project tests/GridGate.Lsl.Tests -c Release` and
  `dotnet test --project tests/GridGate.Platform.Tests -c Release`. Expected: both pass. No OpenSim file changes in this
  plan, so the smoke run isn't needed. Check with `git diff --stat origin/main -- OpenSim/`, which should print nothing.
- [ ] **Step 5:** Run `git grep -n "PlayCoinsApproved = true\|RealCoinsApproved = true"`. Expected: only this plan's own
  text.

---

### Task 9: Live checks

These run on a working-tree-only build with both `MoneyRelease` constants `true`, never committed.

**Setup:**
- **Windows (this computer):** the Grid Asset Server plan, money Play, one region program with one region (A1). Residents
  Alice and Bob already exist.
- **The Debian test VM (`root@192.168.86.37`):** this branch's Linux package, from `gh workflow run release.yml --ref
  feature/money-plan3` (see the VM memory note). It is installed as a Region Server joined to the Windows Grid Asset
  Server, with one region (R1).
- **The owner** does the setup, website sign-in and both Firestorm logins. Claude never types a password.

**Record** each result in `docs/superpowers/plans/2026-11-14-money-plan3-live-checks.md`, with the same table columns as
plan 2's live checks.

The checks, one row each:
1. **Grid info:** `GET :8002/get_grid_info` on the Grid Asset Server shows `gridgate_money play`, the port and the
   fingerprint. The VM's join page reads them.
2. **Unpaired Region Server:** R1's INI has `Paired = false`. In R1, Alice pays Bob and is told "This region isn't
   connected to the grid's money yet." Nothing moves in the ledger.
3. **Make a code and pair:** the Windows Money page makes a code, and the owner types it on the VM's `/advanced/money`. The
   VM says "Paired". The Windows page lists the VM's address. The settings file on Windows holds the hash only (check with
   `grep`).
4. **Restart R1 and pay:** Alice pays Bob in R1. Both balances change, and the ledger entry has R1's region id.
5. **Buy and land in R1:** Bob buys Alice's object for sale in R1, and the hold completes.
6. **Teleport between the computers:** Alice teleports A1 → R1 → A1, and the status bar balance stays right.
7. **The key stays in its own regions:** from the VM, a `curl` with the paired key (read from the VM's settings file by
   the owner, or by root on the VM) asks for Alice's balance with her session while she is in A1. The answer is
   `wrong_region`.
8. **The money port from outside the LAN:** where the setup can't make a public address, record "not checked live", and
   name the unit test.
9. **Remove the Region Server:** on Windows, Remove it. The next payment in R1 gets "Money is unavailable right now". The
   VM's Check button says the grid no longer accepts its key.
10. **Wrong and reused codes:** a code used twice is refused the second time, and a typo is refused. After 5 wrong tries
    the code is cancelled: a sixth try with the right code is refused.
11. **Hypergrid visitor:** if the VM can be a separate grid with hypergrid on and a LAN address, visit the Windows grid
    from it. The visitor's status bar shows `C$ 0`. Alice tips the visitor 5. The visitor pays a tip jar 2. The Money page
    finds "… (visitor from 192.168.86.37)", with a balance of 3. Turn visitor wallets off and restart: the visitor is told
    "Visitors can't pay or be paid on this grid." If hypergrid needs a public address on these plans, record "not checked
    live", and name the integration tests that cover it.
12. **Money Off build:** with the constants back to `false` and both computers rebuilt, `gridgate_money off` is published.
    The VM writes no `[GridGateMoney]` section. Free land still works in R1.

- [ ] **Step 1: Run the checks and fill in the table.** For any failure, use superpowers:systematic-debugging, then fix it
  with a test in the task that owns the code, as a new commit. Rerun the row.
- [ ] **Step 2: Restore the constants.** Run `git diff --stat Launcher/GridGate.Launcher.Core/Money/MoneyRelease.cs`.
  Expected: no output.
- [ ] **Step 3: Commit the results** — `docs(money): live checks for plan 3`

---

### Task 10: Review, PR and merge

- [ ] **Step 1:** Run a fresh, independent review of the whole branch against `main`, with superpowers:requesting-code-review.
  The reviewer gets the branch diff, this plan and the design, not the builder's notes. Fix every Critical and Important
  finding with a test, and record the minors at the end of this plan's live-checks file.
- [ ] **Step 2:** Run Task 8 again.
- [ ] **Step 3:** Push `feature/money-plan3` and open a PR to `main`. The body lists what was checked live and what
  wasn't, and says `MoneyRelease` is unchanged.
- [ ] **Step 4:** Wait for every CI check yourself; `--auto` merges at once on this repository. Merge with `--merge` only
  when the owner says so. Plan 4's PR comes after this merge.
