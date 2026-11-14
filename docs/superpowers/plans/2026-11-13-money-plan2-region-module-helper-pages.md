# Money plan 2: region module, OpenSim fixes, economy helper and pages — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Regions use GridGate's money service for every money action a viewer or script can take. Firestorm's Buy currency
and Buy land windows talk to the launcher's economy helper. Staff get the Money page and residents get My coins. Money stays
Off in every build because `MoneyRelease` is unchanged.

**Architecture:**
- **The region module.** `GridGateMoneyModule` is a new `ISharedRegionModule` + `IMoneyModule` in its own assembly
  (`addon-modules/GridGateMoney`), so no OpenSim file changes to host it.
  - A thin adapter turns OpenSim events into plain records.
  - `MoneyActions` holds every decision the module is allowed to make. These are refusals only (forged source, pay price,
    group-owned, sale mismatch); the service decides the rest. It calls the service through `IMoneyService`.
  - `MoneyServiceClient` implements `IMoneyService` over pinned HTTPS. It has no OpenSim dependency, so the launcher's
    integration tests compile it against the real service.
- **The launcher.**
  - It writes each program's region key, service URL, certificate fingerprint and helper URL into that program's INI, but
    only when the effective mode isn't Off.
  - It serves `/economy/currency.php` and `/economy/landtool.php` as strict XML-RPC on the website, in every mode.
  - It adds `/money` (staff) and `/guest/coins` (residents).

**Tech Stack:** .NET 10; OpenSim region module APIs (Mono.Addins); `SocketsHttpHandler` with certificate pinning;
System.Text.Json; ASP.NET Core Razor Pages and minimal APIs; `XmlReader`; xunit.v3 on Microsoft.Testing.Platform; MariaDB
11.4.9 for the integration tests.

**Spec:** `docs/superpowers/specs/2026-11-13-money-design.md` (owner-approved). This plan covers §7.3, §7.6 on the module
side, §7.8, §8 and §9.1 (without Region Servers), and §9.5. It is steps 2 and 3 of spec §14. Plan 1
(`docs/superpowers/plans/2026-11-13-money-plan1-ledger-service.md`, merged in PR 80) built the ledger and the service this
plan calls. Read `Launcher/GridGate.Launcher/Money/MoneyApi.cs` for the exact wire format.

**Later plans:**
- Plan 3: Region Server pairing, LAN listening, wallet merge on the Money page.
- Plan 4: Gloebit.
- Plan 5: the AI review gate, and starting or stopping the money service when the mode changes. Today it only starts
  with the launcher.
- Plan 6: real coins (§9.2, §9.3, Buy coins page).

## Global Constraints

- **The gate.**
  - `MoneyRelease.PlayCoinsApproved` and `RealCoinsApproved` stay `false` in every commit.
  - Code that needs the effective mode reads it from the new `MoneyGate` service (Task 1), never from a constant directly, so
    tests can open the gate.
  - The live checks (Task 12) flip the constants in the working tree only. They are never committed.
- **Mode Off writes nothing new into region INIs:**
  - no `[GridGateMoney]` section;
  - `EconomyModule` stays blank, so OpenSim's sample module runs as today;
  - no region keys are created.
- **Two things change in every mode:** the helper address (grid info `economy` in Robust, `[GridInfo] economy` in
  `GridCommon.ini`) and the helper endpoints. That keeps free land buyable in Firestorm (spec §8.4).
- **The helper address** is `WebsiteAddress.Base(s) + "/economy/"`. It is always `http://`, because website HTTPS isn't
  built (the HTTPS design is shelved), and it always ends in `/`.
- **The module decides nothing the service decides.**
  - It never computes balances, limits or permissions.
  - Its own refusals happen before any service call:
    - a source that isn't `client.AgentId`;
    - an amount outside the object's fixed pay prices;
    - group-owned objects and parcels;
    - a sale whose type, price or root doesn't match;
    - an amount ≤ 0.
- **Payment source.** Every payment, purchase and fee is made for `client.AgentId`, with `client.SessionId` and
  `client.SecureSessionId`. The packet's ids are never used for this.
- **Messages to the viewer:**
  - at most 250 characters;
  - English in the region module, which can't know the viewer's language; the viewer's own `TransactionInfo` notices are
    translated;
  - translated on the website (six languages, `ResidentText*.resx`).
- **No secret in logs:** no region key, session id, secure session id or request body is ever logged, by the module or the
  launcher. Log only agent ids, amounts and answer codes.
- **Sync calls.** The module's synchronous `IMoneyModule` members block on the service for at most
  `MoneyServiceClient.Timeout` = **10 s**. Viewer packet handlers never block the UDP thread: payment work runs through
  `Util.FireAndForget`.
- **Transaction ids.** Every `SendMoneyBalance` uses a fresh `UUID.Random()` transaction id (Firestorm drops repeats, spec
  §8.8). Every service call carries a fresh `txnId`. Retries of the same step reuse it.
- **OpenSim changes.** Each change in spec §7.8 is one commit touching only what it needs, and is listed in `MERGING.md`
  under a new section "GridGate's own changes to OpenSim".
- **Build and style.**
  - Windows and Debian behave the same.
  - `TreatWarningsAsErrors` is on.
  - New `.csproj` files list their sources (`EnableDefaultItems=false`, as in the OpenSim projects) and are added to
    `GridGate.sln`.
- **Integration tests** use `GRIDGATE_MARIADB_BIN=C:\Users\ystud\AppData\Local\DreamGridDev\mariadb-11.4.9-winx64\bin`.
  Money classes must run with `GRIDGATE_REQUIRE_INTEGRATION=1` and no skips.
- **Branch and commits.**
  - The work is on branch `feature/money-plan2`, in this session's worktree. Check `git branch --show-current` before
    every commit.
  - Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Delivery succeeded but the complete call fails** (the service is restarting or times out). If nothing retries, the
   hold expires after 5 minutes and the buyer gets the coins back while keeping the object. The module retries complete
   with the same `txnId` 3 times, at 1 s, 3 s and 9 s, then logs an error naming the hold id. Test: Task 4,
   `Complete_is_retried_with_the_same_txn`.
2. **Teleport races `Presence`.** On region entry, the balance push can reach the service before Robust has moved the
   avatar's `Presence` row to the new region, and get `wrong_region`. The push tries once more after 2 s and otherwise
   stays silent. It never sends a 0 balance as if it were real. Test: Task 4,
   `Entry_push_retries_once_on_wrong_region`.
3. **Pay price defaults.** With the free-amount box hidden (`PayPrice[0] == -1`), a button left at `PAY_DEFAULT` (-2) shows
   the viewer's default amounts 1, 5, 10 and 20. Those amounts must be accepted, and every other amount refused. Test:
   Task 3, `Hidden_box_accepts_default_buttons`.
4. **Two buy clicks at once on the same parcel.** The second must not pay. The land module validates against the parcel as
   it is at that moment, so after the first sale the second buyer's hold is returned, because the new owner isn't them.
   Test: Task 4, `Land_hold_returns_when_owner_did_not_change`.
5. **Firestorm parsing traps in the helper.** A `<boolean>` written as anything but `1` or `0`, a non-digit `<int>`, a
   missing `errorURI`, or a fault in place of a struct. Every answer is checked against the §8.6 rules by one shared
   assertion. Test: Task 8, `Every_answer_follows_firestorm_rules`.

---

### Task 1: Service-side follow-ups from plan 1

Fixes the deferred findings and adds what the module and pages need from the service.

**Files:**
- Create: `Launcher/GridGate.Launcher.Core/Money/MoneyGate.cs`
- Create: `Launcher/GridGate.Launcher/Money/MoneyStatus.cs`
- Modify: `Launcher/GridGate.Launcher/Money/MoneyApi.cs`. Change the hold, complete and return answers, and the economy
  answer.
- Modify: `Launcher/GridGate.Launcher/Money/MoneyServiceHost.cs`. Record a failed start in `MoneyStatus`.
- Modify: `Launcher/GridGate.Launcher/Money/MoneyMaintenance.cs`. Record each self-check in `MoneyStatus`.
- Modify: `Launcher/GridGate.Launcher.Core/Money/PresenceCheck.cs`. Add `HasSecureSessionAsync`.
- Modify: `Launcher/GridGate.Launcher.Core/Money/MoneyCertificate.cs`. Put `LoadOrCreate` under a process-wide lock,
  because the config writer (Task 2) and the service host both call it.
- Modify: `Launcher/GridGate.Launcher/Program.cs:414-439`. Register `MoneyGate` and `MoneyStatus`, and pass
  `gate.Effective` wherever `() => MoneyRelease.Effective(current().Money)` is passed today.
- Test: `Launcher/GridGate.Launcher.IntegrationTests/MoneyServiceTests.cs` (extend),
  `Launcher/GridGate.Launcher.IntegrationTests/PresenceCheckTests.cs` (extend),
  `Launcher/GridGate.Launcher.Tests/MoneyGateTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class MoneyGate(Func<LauncherSettings> settings, Func<MoneySettings, MoneyMode>? effective = null)`, in
    `GridGate.Launcher.Core.Money`.
    - `MoneyMode Effective()`, which defaults to `MoneyRelease.Effective`.
    - `bool On => Effective() != MoneyMode.Off`.
    - `static Func<MoneySettings, MoneyMode> Open = m => m.Mode`, for tests only.
  - `public sealed class MoneyStatus`, in `GridGate.Launcher.Money`, a thread-safe singleton:
    - `string? ServiceError { get; set; }` holds a fixed text: the exception type name only, never its message.
    - `(DateTimeOffset At, SelfCheckResult Result)? LastCheck { get; set; }`
  - `PresenceCheck.HasSecureSessionAsync(Guid agentId, Guid secureSessionId, CancellationToken ct) -> Task<PresenceOutcome>`.
    It answers `Ok` or `NoSession`, `NoSession` without a query for a zero id, and `Unavailable` on a database error. It runs
    `SELECT 1 FROM Presence WHERE UserID = @u AND SecureSessionID = @ss`.
  - **The wire changes** (spec deferred finding: Holding's balance must never look like someone's balance):
    - The `hold` answer drops `toBalance`. `balance` is the buyer's balance.
    - The `hold/complete` answer drops `balance`. `toBalance` is the seller's balance.
    - The `hold/return` answer drops `balance`. `toBalance` is the buyer's balance.
    - The `economy` answer becomes
      `{ code, currencySymbol, uploadFee, classifiedFee, visitorWallets, scriptPayouts, maxPayment }`.

- [ ] **Step 1: Write the failing tests**

```csharp
// MoneyServiceTests
[Fact] Task Hold_answer_has_no_holding_balance()        // JSON of hold has "balance":60 and no "toBalance" property
[Fact] Task Complete_answer_shows_only_the_seller()      // no "balance" property; "toBalance" == seller's 140
[Fact] Task Return_answer_shows_only_the_buyer()         // no "balance"; "toBalance" == 100
[Fact] Task Economy_answer_carries_the_switches()        // visitorWallets true, scriptPayouts true, maxPayment 1000000
// PresenceCheckTests
[Fact] Task Secure_session_alone_is_ok()                 // matching row, any region → Ok; wrong ss → NoSession; zero → NoSession
// MoneyGateTests
[Fact] void Gate_reads_off_until_approved()               // new MoneyGate(() => s with Mode Play).Effective() == Off
[Fact] void Open_gate_passes_the_mode()                   // new MoneyGate(..., MoneyGate.Open).Effective() == Play
// MoneyServiceTests
[Fact] Task Failed_start_is_recorded()                   // port already bound → status.ServiceError == "IOException" (type name), no message text
```

- [ ] **Step 2: Run them and see them fail.** Run `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release -- --filter-class "*MoneyGate*"`,
  then with `GRIDGATE_MARIADB_BIN` set, `dotnet test --project Launcher/GridGate.Launcher.IntegrationTests -c Release -- --filter-class "*MoneyService*" --filter-class "*PresenceCheck*"`.
  Expected: the build fails, because `MoneyGate` isn't defined.
- [ ] **Step 3: Implement.** In `MoneyApi`, shape the answers at the three hold endpoints (`Of(result) with { … = null }`).
  Leave the ledger as it is.
- [ ] **Step 4: Run both suites and see them pass.** Then run all the `*Money*` classes, to check that nothing else
  depended on the old answer shape.
- [ ] **Step 5: Commit** — `fix(money): hold answers never show Holding's balance; gate and status services`

---

### Task 2: The launcher writes the money settings into the INIs

**Files:**
- Modify: `Launcher/GridGate.Launcher.Core/Config/WebsiteAddress.cs`. Add `Economy(LauncherSettings s)`.
- Modify: `Launcher/GridGate.Launcher.Core/Config/GridConfigWriter.cs`:
  - The constructor gains `Func<MoneySettings, MoneyMode>? effective = null`.
  - `WriteProgram`, at line 115, writes the money keys after `Finish`'s blanking. Either set them on `os` after the
    `economymodule` loop, or have `Finish` skip the `[Economy] EconomyModule` key when a money value is present. The point
    is that `GridGateMoneyModule` must survive the blanking at line 332.
  - The Robust block, at lines 44-82, sets `[LoginService] Currency` and `[GridInfoService] economy`.
  - The `GridCommon.ini` block, at lines 84-91, sets `[GridInfo] economy`.
- Modify: `Launcher/GridGate.Launcher.Core/Grid/GridController.cs:1638-1642` (`WriteConfig`). When `gate.On`, run
  `s = MoneyKeys.EnsureProgramKeys(s)` first. When that returns a new instance, save it the way `UpdateSettings` saves
  (store and cached settings). Every caller already holds `_configGate`; check that before relying on it. `GridController`
  takes the `MoneyGate`; find its construction in `Program.cs` and in `GridTestSupport.cs`.
- Modify: `Launcher/GridGate.Launcher.Tests/GridConfigWriterTests.cs`. Update `No_active_third_party_urls_in_any_generated_file`
  (line 288), which asserts every `economymodule` is blank: it now allows `GridGateMoneyModule` in `[Economy]` when money is
  on, and still requires blank in Off.
- Modify: `Launcher/GridGate.Launcher.Tests/Snapshots/*.GridCommon.ini`. Regenerate with `GG_CAPTURE_SNAPSHOTS=1`. The only
  diff must be the new `[GridInfo] economy` line.
- Test: `Launcher/GridGate.Launcher.Tests/MoneyConfigTests.cs`

**Interfaces:**
- Consumes: `MoneyGate` (Task 1), `MoneyKeys.EnsureProgramKeys`, `MoneyCertificate.LoadOrCreate` / `Fingerprint`.
- Produces: the region INI contract that Task 5's module reads. Section and key names are exact:

```ini
[Economy]
EconomyModule = GridGateMoneyModule

[GridGateMoney]
ServiceURI = https://127.0.0.1:<MoneyPort>/money/v1/
RegionKey = <s.Money.Keys[instance]>
ServiceFingerprint = <MoneyCertificate.Fingerprint(cert)>
HelperURI = <WebsiteAddress.Economy(s)>
```

  - `WebsiteAddress.Economy(s) => Base(s) + "/economy/"`.
  - Robust, always: `[GridInfoService] economy = WebsiteAddress.Economy(s)`.
  - Robust, only when the gate is on: `[LoginService] Currency = s.Money.CurrencySymbol`. When Off, the prototype's value
    stays.
  - `GridCommon.ini`, always: `[GridInfo] economy = WebsiteAddress.Economy(s)`. `Scene.AllModulesLoaded`
    (`Scene.cs:1336-1340`) then advertises it as `currency-base-uri` when no module set one, which covers money Off.
  - The certificate path is `Path.Combine(paths.Security, MoneyCertificate.FileName)`, and the clock is
    `TimeProvider.System`.

- [ ] **Step 1: Write the failing tests** (writer built with `effective: MoneyGate.Open` unless the test says Off)

```csharp
[Fact] void Off_writes_no_money_keys()                // Mode Play but default (closed) gate: no [GridGateMoney]; [Economy] EconomyModule == ""
[Fact] void On_writes_each_programs_own_key()          // two programs: each OpenSim.ini RegionKey == s.Money.Keys[instance]; keys differ
[Fact] void On_names_the_module()                      // [Economy] EconomyModule == "GridGateMoneyModule"; [Startup] economymodule == ""
[Fact] void Service_uri_uses_the_money_port()          // MoneyPort 8123 → "https://127.0.0.1:8123/money/v1/"
[Fact] void Fingerprint_matches_the_certificate()      // == MoneyCertificate.Fingerprint(LoadOrCreate(same path))
[Fact] void Helper_uri_ends_with_a_slash()             // Robust economy, GridCommon [GridInfo] economy, HelperURI all == "http://<host>:<WebPort>/economy/"
[Fact] void Helper_uri_is_written_when_off_too()       // closed gate: Robust economy and GridCommon economy still set
[Fact] void Login_currency_is_the_symbol_when_on()     // CurrencySymbol "GG" → [LoginService] Currency == "GG"; closed gate keeps the prototype's value
// GridController (integration, GridTestSupport)
[Fact] Task Write_config_gives_new_programs_keys_and_saves_them() // open gate; add a program; settings file now has its key
```

- [ ] **Step 2: Run them and see them fail.** Run
  `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release -- --filter-class "*MoneyConfig*"`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the class, regenerate the snapshots, and run the whole unit suite.**
  - Check that `git diff Launcher/GridGate.Launcher.Tests/Snapshots` shows only the `economy` lines.
  - Run: `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release`. Expected: all pass.
- [ ] **Step 5: Commit** — `feat(money): region INIs carry the money service, key and fingerprint; helper address everywhere`

---

### Task 3: Module project, service client and the decision core

**Files:**
- Create: `addon-modules/GridGateMoney/GridGateMoney.csproj`.
  - Model it on `addon-modules/OpenSimTide/Module/OpenSimTide.Module.csproj`, output to `$(GgRoot)bin/`.
  - References: OpenSim.Framework, OpenSim.Region.Framework, OpenSim.Services.Interfaces, `OpenMetaverse*.dll`,
    `OpenMetaverseTypes.dll`, Nini, log4net and Mono.Addins, the same set as Tide.
  - Add `[assembly: InternalsVisibleTo("GridGate.Money.Tests")]`.
- Create: `addon-modules/GridGateMoney/AssemblyInfo.cs`, containing
  `[assembly: Addin("GridGateMoney", OpenSim.VersionInfo.VersionNumber)]` and
  `[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]`.
- Create: `addon-modules/GridGateMoney/MoneyServiceClient.cs`. Only BCL types: `Guid`, `long`, `string`. No OpenSim
  `using`s.
- Create: `addon-modules/GridGateMoney/MoneyActions.cs`, plus the records it uses, in `MoneyModels.cs`.
- Create: `tests/GridGate.Money.Tests/GridGate.Money.Tests.csproj`. Model it on `tests/GridGate.Platform.Tests` and
  reference the module project.
- Modify: `GridGate.sln`. Run `dotnet sln add` for both projects.
- Modify: `Launcher/GridGate.Launcher.IntegrationTests/GridGate.Launcher.IntegrationTests.csproj`. Add
  `<Compile Include="$(GgRoot)addon-modules/GridGateMoney/MoneyServiceClient.cs" Link="Money/MoneyServiceClient.cs" />`.
- Check: `RuntimeStager.ExcludedFiles` and `setup/package.ps1` `$excludedFiles` don't exclude `GridGateMoney.dll`, so it
  ships the way `bin/*.dll` ships.
- Test: `tests/GridGate.Money.Tests/MoneyActionsPayTests.cs`,
  `Launcher/GridGate.Launcher.IntegrationTests/MoneyServiceClientTests.cs`

**Interfaces:**
- Produces, in `MoneyServiceClient.cs` (namespace `GridGate.Region.Money`):
  - `public sealed record MoneyWho(Guid AgentId, string Home, Guid? SessionId = null, Guid? SecureSessionId = null)`.
    Its JSON is `{agentId, home, sessionId?, secureSessionId?}`.
  - `public sealed record MoneyAnswer(string Code, long? Balance, long? ToBalance, long? EntryId, long? HoldId)`, with
    `bool Ok => Code == "ok"`.
  - `public sealed record EconomyInfo(string CurrencySymbol, long UploadFee, long ClassifiedFee, bool VisitorWallets, bool ScriptPayouts, long MaxPayment)`.
  - `public interface IMoneyService`:
    - `Task<MoneyAnswer> BalanceAsync(MoneyWho who, CancellationToken ct)`
    - `Task<MoneyAnswer> TransferAsync(Guid txn, string type, MoneyWho from, MoneyWho to, long amount, Guid regionId, Guid? objectId, string? objectName, CancellationToken ct)`
    - `Task<MoneyAnswer> PayoutAsync(Guid txn, Guid ownerId, MoneyWho to, long amount, Guid regionId, Guid objectId, string objectName, CancellationToken ct)`
    - `Task<MoneyAnswer> ChargeAsync(Guid txn, string type, MoneyWho payer, long amount, Guid? payTo, CancellationToken ct)`
    - `Task<MoneyAnswer> HoldAsync(Guid txn, string kind, MoneyWho buyer, MoneyWho seller, long amount, Guid regionId, Guid? objectId, string? objectName, CancellationToken ct)`
    - `Task<MoneyAnswer> CompleteAsync(Guid txn, long holdId, CancellationToken ct)`
    - `Task<MoneyAnswer> ReturnAsync(Guid txn, long holdId, CancellationToken ct)`
    - `Task<EconomyInfo?> EconomyAsync(CancellationToken ct)`
  - `public sealed class MoneyServiceClient(Uri serviceUri, string regionKey, string fingerprint) : IMoneyService, IDisposable`:
    - Its own `SocketsHttpHandler`, whose `RemoteCertificateValidationCallback` accepts only a certificate whose upper-case
      hex SHA-256 of the DER bytes equals `fingerprint`. It ignores chain and name errors, because the pin is the trust.
    - `Timeout` = 10 s. A request header `Authorization: GridGateRegion <key>`.
    - Every transport failure, timeout, non-200 status or unreadable body answers `new MoneyAnswer("unavailable", …)`. It
      never throws, except `OperationCanceledException` on the caller's own token.
    - Unknown JSON properties in answers are ignored.
- Produces, in `MoneyModels.cs` / `MoneyActions.cs` (internal):
  - `record PartInfo(Guid PartId, Guid RootId, bool IsRoot, Guid OwnerId, Guid GroupId, string Name, int[] PayPrice, byte SaleType, int SalePrice, bool HasTransferableContents)`,
    with `bool GroupOwned => GroupId != Guid.Empty && OwnerId == GroupId`.
  - `enum Refusal { None, ForgedSource, BadAmount, NotPayPrice, GroupOwned, NotForSale, PriceChanged, NotRoot, NothingToSell, UnknownType }`
  - `static class MoneyRules`:
    - `Refusal CheckPayAvatar(Guid clientAgent, Guid source, int amount)`
    - `Refusal CheckPayObject(Guid clientAgent, Guid source, int amount, PartInfo root)`
    - `Refusal CheckBuy(PartInfo part, byte saleType, int salePrice)`
    - `bool PayPriceAllows(int[] payPrice, int amount)`
    - `string ScriptReason(string code)`, the `llTransferLindenDollars` reason for a service code
    - `string Message(Refusal r)` and `string Message(string code)`, the viewer texts below
  - **Pay price rule:**
    - When `payPrice[0] != -1` (the free-amount box is shown), any amount > 0 is allowed.
    - Otherwise, only button amounts are allowed. For `i` in 1..4, a button is `payPrice[i]` when it is > 0, or
      `{1,5,10,20}[i-1]` when it is `-2`. `-1` and `0` are hidden buttons.
  - **Script reasons:**

| Service code | Script reason |
|---|---|
| `not_enough` | `LINDENDOLLAR_INSUFFICIENTFUNDS` |
| `no_wallet` | `LINDENDOLLAR_ENTITYDOESNOTEXIST` |
| `not_allowed` | `TRANSFERS_DISABLED` |
| `over_daily_limit` | `THROTTLED` |
| `bad_amount` | `INVALID_AMOUNT` |
| anything else | `SERVICE_ERROR` |

  - **Viewer texts (exact, English):**

| When | Text |
|---|---|
| `Refusal.GroupOwned` | "Group-owned objects can't take payments on this grid. Ask the owner to set it to a person." |
| group land | "Group-owned land can't be sold for coins on this grid." |
| `not_enough` | "You don't have enough coins for that." |
| `frozen` or `account_frozen` | "Payments are paused on this grid right now." |
| `unavailable` | "Money is unavailable right now. Please try again later." |
| `no_wallet` | "That person can't receive coins on this grid." |
| `no_session` or `wrong_region` | "Please log in again to use money here." |
| `NotPayPrice` | "That amount isn't one this object accepts." |
| `NotForSale` or `PriceChanged` | "This object's sale details changed. Please try again." |
| `NothingToSell` | "This object has nothing to sell." |
| visitor wallets off | "Visitors can't pay or be paid on this grid." |

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/GridGate.Money.Tests/MoneyActionsPayTests.cs
[Fact] void Forged_source_is_refused()                     // CheckPayAvatar(a, b, 5) == ForgedSource
[Theory, InlineData(0), InlineData(-1)] void Non_positive_is_refused(int amt)   // BadAmount
[Fact] void Open_box_accepts_any_amount()                    // PayPrice {-2,-2,-2,-2,-2}: 37 allowed
[Fact] void Hidden_box_accepts_only_buttons()                // {-1, 10, 25, -1, -1}: 10, 25 ok; 11, 1 refused
[Fact] void Hidden_box_accepts_default_buttons()             // {-1,-2,-2,-2,-2}: 1,5,10,20 ok; 2 refused
[Fact] void Group_owned_object_is_refused()                  // OwnerId == GroupId != 0 → GroupOwned
[Fact] void Buy_needs_root_matching_type_and_price()         // child part → NotRoot; saleType 0 → NotForSale; price differs → PriceChanged
[Fact] void Contents_sale_needs_something_transferable()     // saleType 3, HasTransferableContents false → NothingToSell
[Fact] void Script_reasons_map()                             // each row of the table
[Fact] void Messages_fit_250_chars()                         // every Refusal and every service code
// Launcher.IntegrationTests/MoneyServiceClientTests.cs (real MoneyServiceHost on MoneyTestDatabase, open gate)
[Fact] Task Client_talks_to_the_real_service()               // BalanceAsync for present avatar → Ok, Balance 100
[Fact] Task Wrong_fingerprint_is_unavailable()               // client pinned to "00…" → Code "unavailable", no request reached the ledger
[Fact] Task Wrong_key_is_unavailable()                       // 401 → "unavailable"
[Fact] Task Service_down_is_unavailable_within_timeout()     // nothing listening → "unavailable" in < 11 s
[Fact] Task Hold_complete_round_trip()                       // hold → Ok + HoldId; complete → ToBalance seller
```

- [ ] **Step 2: Run them and see them fail.** Run
  `dotnet test --project tests/GridGate.Money.Tests -c Release`, then the integration class
  `--filter-class "*MoneyServiceClient*"`. Expected: the build fails.
- [ ] **Step 3: Implement the project, client, models and `MoneyRules`.**
- [ ] **Step 4: Run them and see them pass.** Run `dotnet build GridGate.sln -c Release` too, and check that
  `bin/GridGateMoney.dll` exists.
- [ ] **Step 5: Commit** — `feat(money): region money module project, pinned service client and payment rules`

---

### Task 4: `MoneyActions`: every §7.3 action, against a fake service

**Files:**
- Modify: `addon-modules/GridGateMoney/MoneyActions.cs`
- Create: `tests/GridGate.Money.Tests/FakeMoneyService.cs`. It records each call (method, txn, arguments) and answers from
  a queue or a lambda.
- Test: `tests/GridGate.Money.Tests/MoneyActionsTests.cs`

**Interfaces:**
- Consumes: `IMoneyService`, `MoneyRules` and `PartInfo` (Task 3).
- Produces `internal sealed class MoneyActions(IMoneyService service, Guid regionId, Func<EconomyInfo> economy, IMoneyView view)`:
  - `interface IMoneyView` is how actions reach the viewer and scene. The module implements it in Task 5.
    - `void Tell(Guid agent, string text)`
    - `void PushBalance(Guid agent, long balance, BalanceNote? note)`. A null note is a silent update.
    - `void ObjectPaid(Guid partId, Guid payer, int amount)`
    - `Task Delay(TimeSpan t)`, so tests don't sleep.
  - `record BalanceNote(int TransactionType, Guid Source, Guid Dest, int Amount, string Item)`
  - `record Payer(Guid AgentId, Guid SessionId, Guid SecureSessionId)`. The adapter always builds it from the client.
  - `Task PayAvatarAsync(Payer p, Guid source, MoneyWho to, int amount)`. Checks, then `pay_avatar`. On Ok it pushes the
    payer's balance with a `Gift` note and the payee's `toBalance` with the same note.
  - `Task PayObjectAsync(Payer p, Guid source, PartInfo part, PartInfo root, MoneyWho owner, int amount)`.
    - It runs the checks, then `pay_object` with the object id and name.
    - On Ok: first `view.ObjectPaid(part.PartId, p.AgentId, amount)`, then the balance pushes.
    - On anything else: `Tell`, and no `ObjectPaid`.
  - `Task<bool> BuyAsync(Payer p, PartInfo part, MoneyWho seller, byte saleType, int salePrice, Func<bool> deliver)`.
    - Checks first.
    - Price 0, or a buyer who is the seller: call `deliver()` only, with no money.
    - Otherwise: hold (`object`) → `deliver()` → true: `CompleteAsync`, retried with the same txn at 1 s, 3 s and 9 s while
      the answer is `unavailable`; false: `ReturnAsync`, retried the same way.
    - It pushes the buyer's balance after the hold and after a return, and the seller's after complete.
  - `Task<long?> HoldLandAsync(Payer p, MoneyWho seller, int price, int parcelLocalId)`. It returns the hold id, or null
    after telling the buyer why. Price ≤ 0 returns 0 and holds nothing.
  - `Task SettleLandAsync(long holdId, bool newOwnerConfirmed)`. Complete or return, retried like `BuyAsync`.
  - `bool MovePass(Payer p, MoneyWho parcelOwner, int amount)`: a synchronous `parcel_pass` transfer, true only on Ok.
  - `bool Payout(Guid ownerId, MoneyWho to, int amount, Guid objectId, string objectName, Guid txn, out string reason)`.
    - Amount ≤ 0 gives `INVALID_AMOUNT` without a call.
    - A visitor recipient with `!economy().VisitorWallets` gives `LINDENDOLLAR_ENTITYDOESNOTEXIST` without a call.
    - A zero `txn` becomes a new Guid.
  - `bool Covered(Payer p, long amount)`: true for amount ≤ 0; otherwise a fresh balance ≥ amount. Anything but Ok is false.
  - `void Charge(Payer p, string type, int amount, Guid? payTo)`.
    - It does nothing for amount ≤ 0.
    - On anything but Ok it logs, at warning level, `Uncollected {type} of {amount} from {agent}: {code}` and doesn't retry
      (spec §7.3).
    - **Recorded deviation:** the spec's "recorded as uncollected" is this log line. The ledger can't hold an entry that
      moves nothing.
  - `Task PushEntryBalanceAsync(Payer p)`. It pushes a silent balance. On `wrong_region` it waits 2 s and tries once more;
    otherwise it pushes nothing.
  - `MoneyTransactionType` values come from libomv by name: `Gift`, `PayObject`, `ObjectSale`, `LandSale`,
    `LandPassSale`, `ObjectPays`, `UploadCharge`, `GroupJoin`, `ClassifiedCharge`. A test in Task 3's project asserts these
    names exist in `OpenMetaverse.MoneyTransactionType` (check it with reflection).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] Task Pay_avatar_moves_then_pushes_both()             // service Ok bal 70 to 130 → two PushBalance with notes (Gift, src, dest, 30)
[Fact] Task Forged_pay_makes_no_call()                      // source != payer → no service call, Tell(...)
[Fact] Task Pay_object_raises_paid_after_ok()               // order: Transfer call, then ObjectPaid(partId, payer, amt)
[Fact] Task Pay_object_refused_raises_nothing()              // not_enough → no ObjectPaid, Tell "You don't have enough coins for that."
[Fact] Task Pay_object_wrong_amount_makes_no_call()          // hidden box, amount 11 → no call
[Fact] Task Group_owned_object_payment_makes_no_call()
[Fact] Task Buy_delivers_after_hold_and_completes()          // calls: Hold, deliver, Complete
[Fact] Task Failed_delivery_returns_at_once()                // deliver false → Return, no Complete
[Fact] Task Refused_hold_does_not_deliver()                  // hold not_enough → deliver never called
[Fact] Task Complete_is_retried_with_the_same_txn()          // Complete answers unavailable twice then ok: 3 Complete calls, one txn, Delay(1s),Delay(3s)
[Fact] Task Free_sale_skips_money()                          // price 0 → deliver only
[Fact] Task Group_owned_sale_is_refused()
[Fact] Task Land_hold_returns_when_owner_did_not_change()    // HoldLand → id; SettleLand(id, false) → Return
[Fact] Task Land_hold_completes_when_owner_changed()
[Fact] Task Payout_zero_is_invalid_without_call()
[Fact] Task Payout_maps_refusals_to_script_reasons()          // not_allowed → TRANSFERS_DISABLED, false
[Fact] Task Payout_to_visitor_needs_visitor_wallets()
[Fact] Task Uncollected_charge_is_logged_not_retried()       // charge not_enough → one call, one warning line without session ids
[Fact] Task Entry_push_retries_once_on_wrong_region()        // wrong_region then ok → Delay(2s), one silent push; wrong_region twice → no push
[Fact] Task Holding_balance_never_pushed()                    // hold answer Balance=60 ToBalance=null: push(buyer, 60) only
```

- [ ] **Step 2: Run them and see them fail** · [ ] **Step 3: Implement** · [ ] **Step 4: Run them and see them pass**
  (`dotnet test --project tests/GridGate.Money.Tests -c Release`)
- [ ] **Step 5: Commit** — `feat(money): module actions for pay, buy, land, pass, payouts and fees`

---

### Task 5: `GridGateMoneyModule`: wiring to OpenSim

**Files:**
- Create: `addon-modules/GridGateMoney/GridGateMoneyModule.cs`
- Modify: `addon-modules/GridGateMoney/GridGateMoney.csproj` (the Compile list)

**Interfaces:**
- Consumes: `MoneyActions`, `MoneyServiceClient`, the INI contract (Task 2) and `IVisitorMoneyModule` (Task 6).
- Produces: `[Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "GridGateMoneyModule")] public sealed class GridGateMoneyModule : ISharedRegionModule, IMoneyModule, IVisitorMoneyModule`.
  - `Name` = `"GridGateMoneyModule"` and `ReplaceableInterface` = null.
  - **`Initialise`:** enabled only when `[Economy] EconomyModule == "GridGateMoneyModule"` and all four `[GridGateMoney]` keys
    are non-empty. Otherwise it logs one line saying which key is missing, and stays disabled.
  - **`AddRegion`:**
    - `RegisterModuleInterface<IMoneyModule>(this)` here, because YEngine hooks `OnObjectPaid` in its `RegionLoaded`.
    - Subscribe `OnNewClient`, `OnClientClosed`, and the parcel pass path (nothing to subscribe: the land module calls
      `AmountCovered` and `MoveMoney`).
    - Build one `MoneyActions` per scene.
  - **`RegionLoaded`:**
    - Subscribe `OnValidateLandBuy` and `OnLandBuy` here, so they run after the land module's handlers
      (`RegionModulesControllerPlugin.cs:310-337`, `:484-491`). The `OnValidateLandBuy` handler must see `landValidated`
      and `parcelOwnerID` already set.
    - Subscribe `ISimulatorFeaturesModule.OnSimulatorFeaturesRequest`. It sets `OpenSimExtras["currency-base-uri"] = HelperURI`
      and `OpenSimExtras["currency"] = economy.CurrencySymbol`, creating `OpenSimExtras` if it's missing (as Gloebit does).
    - Fetch the economy info. Cache it for 60 s; when the service is unreachable, use the last value, or fees 0 and symbol
      "C$".
  - **`OnNewClient(client)`:** subscribe these, each a closure over `client`:
    - `OnMoneyBalanceRequest`
    - `OnMoneyTransferRequest`, whose handler passes `client.AgentId` as the payer and runs on `Util.FireAndForget`
    - `OnEconomyDataRequest`
    - `OnRequestPayPrice` → `client.SendPayPrice(id, root.PayPrice)`
    - `OnObjectBuy`
    - `OnCompleteMovementToRegion` → `PushEntryBalanceAsync`
    - `OnLogout`, to unsubscribe
  - **The transfer handler:**
    - Type 5001 (`Gift`) → `PayAvatarAsync`.
    - Type 5008 (`PayObject`) → look up the part by `destID`, then `PayObjectAsync` with the root part's `PayPrice`, and the
      owner as `MoneyWho`.
    - Any other type → `Tell("That kind of payment isn't available on this grid.")`.
  - **`MoneyWho` for an avatar id:**
    - `IUserManagement.IsLocalGridUser(id)` → home `""`.
    - Otherwise, home `um.GetUserHomeURL(id)`, and an empty URL also reads as `""`. The ledger then answers `no_wallet` for
      anyone not in `UserAccounts`; `IsLocalGridUser` failing open is safe because of this.
  - **`OnEconomyDataRequest`:** `SendEconomyData`, as the sample does (`SampleMoneyModule.cs:715-721`), with:
    - `PriceUpload = economy.UploadFee`;
    - `PriceGroupCreate = 0`;
    - every other price 0, and `EnergyEfficiency`, `ObjectCapacity` and `ObjectCount` as the sample sends them.

    `UploadCharge` and `GroupCreationCharge` return the same numbers.
  - **`OnObjectBuy(remoteClient, …, localID, saleType, salePrice)`:**
    - It ignores the packet's agent id and uses `remoteClient`.
    - Find the part with `scene.GetSceneObjectPart(localID)`, null-checked before anything else.
    - `deliver = () => scene.RequestModuleInterface<IBuySellModule>().BuyObject(remoteClient, categoryID, localID, saleType, salePrice)`.
    - `HasTransferableContents`: the root part's inventory has at least one item, and every item has Transfer in
      `CurrentPermissions`.
  - **Land:**
    - **`OnValidateLandBuy`:**
      - When `!e.landValidated`, or `economyValidated` is already set, do nothing.
      - The parcel is group-owned (`land.LandData.IsGroupOwned`, found by `e.parcelLocalID`) → `Tell`, and leave
        `economyValidated` false.
      - Price 0 → `economyValidated = true`.
      - Otherwise `HoldLandAsync(.., e.parcelOwnerID …)`, waited synchronously. With a hold id, set
        `economyValidated = true` and `amountDebited = price`, and remember `(agentId, regionId, parcelLocalID) → holdId`.
    - **`OnLandBuy`:** for a remembered hold, read the parcel again. The new owner is confirmed when
      `LandData.OwnerID == e.agentId`, or `GroupID == e.groupId` when `e.groupOwned`. Then `SettleLandAsync(holdId, confirmed)`.
      A hold left behind by an exception is returned by the service after 5 minutes.
  - **`AmountCovered` / `UploadCovered`:** `Covered`, for the client found with `scene.TryGetScenePresence` in any of the
    module's scenes. No client → false.
  - **`ApplyUploadCharge`:** `Charge(upload_fee)`.
  - **`ApplyCharge(agent, amount, type, extra)`:**
    - `GroupJoin` → `Charge(group_join_fee, payTo: IGroupsModule.GetGroupRecord(extra)?.FounderID)`.
    - `ClassifiedCharge` → `Charge(classified_fee)`.
    - Anything else → log at debug level and ignore.
  - **`MoveMoney`:** the bool overload with `LandPassSale` → `MovePass`; other types → false. The land module already
    refuses passes on group land (`LandManagementModule.cs:586-590`); a group id as payee would get `no_wallet` anyway. The void overload logs and
    does nothing; it has no callers.
  - **`ObjectGiveMoney(objectID, fromID, toID, amount, txn, out reason)`:**
    - The root part is group-owned → `GROUP_OWNED`, false.
    - `fromID` isn't the part's owner → `SERVICE_ERROR`, false.
    - Otherwise `Payout(...)`. On Ok, push both balances if online, with an `ObjectPays` note.
  - **`GetBalance`:** the last pushed balance, or 0.
  - **`IVisitorMoneyModule.CanPayVisitor(id)`:** `economy.VisitorWallets && !IsLocalGridUser(id) && GetUserHomeURL(id) != ""`.
  - **`IMoneyView` on the module:**
    - `PushBalance` → `client.SendMoneyBalance(UUID.Random(), true, Array.Empty<byte>(), (int)balance, note?.TransactionType ?? 0, note?.Source ?? UUID.Zero, false, note?.Dest ?? UUID.Zero, false, note?.Amount ?? 0, note?.Item ?? "")`,
      for each online client of that agent in the module's scenes.
    - `Tell` → `client.SendAlertMessage(text)`.
    - `ObjectPaid` → `OnObjectPaid?.Invoke(partId, payer, amount)`.

- [ ] **Step 1: Build check.** There is no scene harness. Run `dotnet build GridGate.sln -c Release` and expect 0 errors.
  Task 12's live checks cover this adapter.
- [ ] **Step 2: Add a load test** to `tests/GridGate.Money.Tests`: `Module_disabled_without_config` (`Initialise` with an
  empty `IniConfigSource` → no exception; `AddRegion(null)` is never reached, because it's disabled) and
  `Module_enabled_with_all_keys` (an IniConfigSource with the four keys plus EconomyModule → `Enabled` true, an internal
  property).
- [ ] **Step 3: Run them and see them pass.**
- [ ] **Step 4: Commit** — `feat(money): GridGateMoneyModule wires viewer and script money actions to the service`

---

### Task 6: OpenSim changes from spec §7.8

Each item is its own commit, in this order. Each one touches only the lines it names.

**Files:**
- Create: `OpenSim/Framework/IVisitorMoneyModule.cs`: `public interface IVisitorMoneyModule { bool CanPayVisitor(UUID agentID); }`.
  Add it to `OpenSim/Framework/OpenSim.Framework.csproj`.
- Modify: `OpenSim/Region/ScriptEngine/Shared/Api/Implementation/LSL_Api.cs`, at the lines in each commit below.
- Modify: `OpenSim/Region/CoreModules/Agent/AssetTransaction/AssetXferUploader.cs` (around line 506) and its module, for
  commit 6.
- Modify: `MERGING.md`. Add a section "GridGate's own changes to OpenSim" listing each commit's subject and the files it
  touches.

**The commits:**
1. **`fix(opensim): PERMISSION_DEBIT must come from the object's owner`.** `llGiveMoney` (around line 3229) and
   `llTransferLindenDollars` (around lines 17588-17594) refuse when `m_item.PermsGranter != m_host.OwnerID`.
   `llTransferLindenDollars` answers `MISSING_PERMISSION_DEBIT`; `llGiveMoney` returns 0 without calling the module.
2. **`fix(opensim): auto_grant_all_perms never grants PERMISSION_DEBIT`.** Line 4523:
   `implicitPerms = perm & ~ScriptBaseClass.PERMISSION_DEBIT;`.
3. **`fix(opensim): llGiveMoney refuses amounts of 0 or less`.** At the top of `llGiveMoney`, `if (amount <= 0) return 0;`.
4. **`fix(opensim): llGiveMoney refuses group-owned objects`.** The same rule `llTransferLindenDollars` has at line 17574.
5. **`feat(opensim): llTransferLindenDollars can pay hypergrid visitors`.** Around line 17623: when `GetUserAccount` is
   null, continue only if `money is IVisitorMoneyModule v && v.CanPayVisitor(toID)`; otherwise
   `LINDENDOLLAR_ENTITYDOESNOTEXIST`, as today. This commit also adds `IVisitorMoneyModule`.
6. **`fix(opensim): UDP uploads are charged the upload fee`.**
   - In `AssetXferUploader.DoCreateItem` (or the method holding line 506), after `m_Scene.AddInventoryItem(item)`
     succeeds, call `m_Scene.RequestModuleInterface<IMoneyModule>()?.ApplyUploadCharge(ourClient.AgentId, mm.UploadCharge, "Asset upload")`.
   - Charge only when `mm.UploadCharge > 0`, and only for the asset types the check in `AssetTransactionModule.cs:251-262`
     covers.
   - The caps path (`Scene.Inventory.cs:102`) already charges and isn't touched.

- [ ] **Step 1: Make commit 1.** Run `dotnet build GridGate.sln -c Release` (0 errors), then commit.
- [ ] **Step 2: Make each of commits 2 to 6 the same way.** Build after each one, then commit it on its own.
- [ ] **Step 3: Write the `MERGING.md` section,** then commit — `docs(merging): list GridGate's own OpenSim changes`.

Task 12's live checks cover each change: debit from a non-owner, auto-grant, `llGiveMoney(0)`, and the group-owned payout.
OpenSim's LSL tests aren't built in this repository.

---

### Task 7: XML-RPC reader and writer for the helper

**Files:**
- Create: `Launcher/GridGate.Launcher/Economy/XmlRpc.cs`
- Test: `Launcher/GridGate.Launcher.Tests/XmlRpcTests.cs`

**Interfaces:**
- Produces, as `internal static class XmlRpc`:
  - `sealed record Call(string Method, IReadOnlyDictionary<string, object> Params)`. The values are `string`, `int`,
    `bool` or `double`, from the first param's struct members.
  - `static Call? Read(Stream body, int maxBytes = 16384)`. It returns null for anything that doesn't parse: over the size
    limit, a DTD, a struct that isn't one, nesting deeper than 4. It uses an `XmlReader` with `DtdProcessing.Prohibit`,
    `XmlResolver = null`, `MaxCharactersInDocument = maxBytes`. Untyped `<value>text</value>` reads as a string.
  - `static string Response(IEnumerable<(string Name, object Value)> members)` writes
    `<?xml version="1.0"?><methodResponse><params><param><value><struct>…</struct></value></param></params></methodResponse>`.
    - A `bool` is written as `<boolean>1</boolean>` or `<boolean>0</boolean>`.
    - An `int` or `long` is written as `<int>` in invariant digits. A `long` outside the int range throws.
    - A `string` is written as `<string>` with XML escaping.
    - A nested `IEnumerable<(string, object)>` is written as `<struct>`.
  - `static string Fault(int code, string message)`, for unknown methods only.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] void Reads_firestorm_quote_call()        // the getCurrencyQuote body from spec §8.3 (agentId, secureSessionId, language, currencyBuy int, viewer fields)
[Fact] void Untyped_value_is_a_string()
[Fact] void Dtd_is_refused()                    // <!DOCTYPE …> → null
[Fact] void Oversized_body_is_refused()         // 20 KB → null
[Fact] void Garbage_is_null()                   // "<a>", "", "not xml"
[Fact] void Booleans_are_1_or_0()               // Response([("success", false)]) contains "<boolean>0</boolean>" and no "false"
[Fact] void Ints_are_plain_digits()             // 1234 → "<int>1234</int>" under de-DE and ar-SA current culture
[Fact] void Strings_are_escaped()               // "<&>" round-trips
[Fact] void Response_shape_is_exact()           // root methodResponse/params/param/value/struct, nothing else
```

- [ ] **Step 2: Run them and see them fail** · [ ] **Step 3: Implement** · [ ] **Step 4: Run them and see them pass**
- [ ] **Step 5: Commit** — `feat(money): strict XML-RPC reader and writer for the viewer economy helper`

---

### Task 8: The economy helper endpoints and the public coins page

**Files:**
- Create: `Launcher/GridGate.Launcher/Economy/EconomyHelper.cs` (the answers) and
  `Launcher/GridGate.Launcher/Endpoints/EconomyApi.cs`
  (`static void Map(WebApplication app)`). The endpoints are
  `POST /economy/currency.php` and `POST /economy/landtool.php`, `.AllowAnonymous()` with no antiforgery, and their answers
  are `text/xml`.
- Create: `Launcher/GridGate.Launcher/Economy/EconomyLimits.cs`. It allows **60 calls per rolling minute per address**,
  in memory, folding IPv4-mapped addresses like `ResetLimits`.
- Create: `Launcher/GridGate.Launcher/Pages/Coins.cshtml(.cs)`, at `@page "/coins"`, anonymous, with resident text only.
  - It shows the currency name and symbol.
  - In Play mode: "These are play coins. They can't be bought or cashed out." It explains how to get them: the starting
    balance (shown), the weekly allowance when > 0, gifts from staff, and being paid by other residents.
  - In Off mode: "This grid doesn't use money."
- Modify: `Program.cs:490-503` (call `EconomyApi.Map`). Also modify:
  - `Security/SetupGate.cs:121-129` `AlwaysOpen`, adding `/economy` and `/coins`;
  - `Security/AdminNetworkFilter.cs` `PublicPaths`, adding `"/economy"` and `"/coins"`;
  - the anonymous page list at `Program.cs:233`, adding `/Coins`.
- Modify: `Launcher/GridGate.Launcher/Resources/ResidentText*.resx` (all six files), adding the `Economy.*` and `Coins.*`
  keys. Translations follow `docs/languages.md`, including the AI-draft comment `TranslationsTests` requires.
- Test: `Launcher/GridGate.Launcher.Tests/EconomyHelperTests.cs` (`LauncherFactory` with a fake `PresenceCheck`. Register
  an `IPresenceLookup` seam if `PresenceCheck` can't be faked directly; it's sealed, so add
  `interface ISecureSessionCheck { Task<PresenceOutcome> HasSecureSessionAsync(...) }`, implemented by `PresenceCheck`).

**Interfaces:**
- Consumes: `XmlRpc` (Task 7), `PresenceCheck.HasSecureSessionAsync` (Task 1), `MoneyGate`, settings, and
  `IStringLocalizer<ResidentText>` with the culture taken from the call's `language` (a supported language, else English;
  see `Core/Languages/Languages.cs`).
- **Answers.** Every answer has `success`, `errorMessage` and `errorURI`, always present. The URI is
  `WebsiteAddress.Base(s) + "/coins"`.

| Call | Condition | Answer |
|---|---|---|
| any | not parsable | `Fault(1, "bad request")` |
| any | unknown method | `Fault(2, "unknown method")` |
| any | over the rate limit | success 0, `Economy.TooMany` |
| any | `agentId` or `secureSessionId` missing or not a Guid, or `NoSession` | success 0, `Economy.LogInAgain` |
| any | `Unavailable` | success 0, `Economy.Unavailable` |
| `getCurrencyQuote` | Off | success 0, `Economy.NoMoney` |
| `getCurrencyQuote` | Play | success 0, `Economy.PlayCoins` (symbol as `{0}`) |
| `getCurrencyQuote` | Real | success 0, `Economy.RealCoins` |
| `buyCurrency` | always | success 0, `Economy.BuyNotOffered` |
| `preflightBuyLandPrep` | `currencyBuy` ≤ 0 or missing | success 1, `confirm` "", `membership{upgrade 0, action "", levels {}}`, `landUse{upgrade 0, action ""}`, `currency{estimatedCost 0}` |
| `preflightBuyLandPrep` | `currencyBuy` > 0, Off | success 0, `Economy.NoMoneyLand` |
| `preflightBuyLandPrep` | `currencyBuy` > 0, Play or Real | success 0, `Economy.Shortfall` (`{0}` amount, `{1}` symbol) |
| `buyLandPrep` | always | success 0, the same text as the pre-flight would give, or `Economy.BuyNotOffered` |

  - The helper moves no money and calls no money service endpoint.
  - It sets no cookie: the response has no `Set-Cookie`, which the test checks.

- [ ] **Step 1: Write the failing tests**

```csharp
static void AssertFirestormSafe(string xml) // shared: root shape exact; every <boolean> is 0|1; every <int> matches ^-?\d+$;
                                             // success, errorMessage, errorURI members present; no <fault> unless the test expects one
[Fact] Task Every_answer_follows_firestorm_rules()        // runs every table row, AssertFirestormSafe on each
[Fact] Task Preflight_succeeds_when_money_is_off()         // closed gate, currencyBuy 0 → success 1
[Fact] Task Preflight_shortfall_names_the_amount()        // open gate Play, currencyBuy 25 → errorMessage contains "25" and "C$"
[Fact] Task Quote_in_play_mode_refuses_with_help_link()   // errorURI == "http://localhost:<port>/coins"
[Fact] Task Buy_currency_always_refuses()
[Fact] Task Unknown_session_is_told_to_log_in()           // fake presence NoSession
[Fact] Task Answer_uses_the_viewers_language()             // language "de" → German Economy.PlayCoins text
[Fact] Task Sixty_one_calls_hit_the_limit()
[Fact] Task Helper_sets_no_cookies()
[Fact] Task Helper_is_open_outside_admin_networks()        // AdminNetworks set to 10.0.0.0/8, caller 203.0.113.5 → 200 XML, not 404
[Fact] Task Coins_page_is_anonymous_and_translated()       // GET /coins as anonymous with lang cookie "fr" → 200, French text
```

- [ ] **Step 2: Run them and see them fail** · [ ] **Step 3: Implement** · [ ] **Step 4: Run them, then the whole unit
  suite** (`TranslationsTests` and `ResidentTextScanTests` must pass)
- [ ] **Step 5: Commit** — `feat(money): viewer economy helper (currency.php, landtool.php) and the coins page`

---

### Task 9: The Money staff page and dashboard notices

**Files:**
- Create: `Launcher/GridGate.Launcher/Pages/Money/Index.cshtml(.cs)` (`/money`) and
  `Launcher/GridGate.Launcher/Pages/Money/Person.cshtml(.cs)` (`/money/person?id=<guid>&home=<string?>`)
- Modify: `Program.cs` folder rules. `/Money` is `StaffPolicy`, and every POST handler starts with
  `if (StaffSignIn.RoleOf(User) < StaffRole.Admin) return Forbid();`.
- Modify: `Navigation/StaffMenu.cs`, adding a "Money" item in the Settings group. It is shown to every staff role on plans
  with staff pages.
- Modify: `Pages/People/Person.cshtml`. Add a "Coins" link to `/money/person?id=…`, shown only when `gate.On`.
- Modify: `Pages/Index.cshtml(.cs)` (the dashboard). Add the money notices, red (`error-box error alert alert-danger`),
  shown only when `gate.On`:
  - `MoneyStatus.ServiceError != null` → "The money service couldn't start, so payments are refused until GridGate restarts.
    The launcher log has the details."
  - `ledger.FrozenAsync` says frozen → "All payments are frozen: {reason}. Open Money to unfreeze."
  - `FrozenAsync` throws `MoneyUnavailableException` → "The money database can't be reached; payments are refused."
- Test: `Launcher/GridGate.Launcher.Tests/MoneyPageTests.cs` (`LauncherFactory` with `ExtraServices` replacing `MoneyGate`
  with an open one, and a `MoneyLedger` over a fake open function where the test needs no database), and
  `Launcher/GridGate.Launcher.IntegrationTests/MoneyPageLedgerTests.cs` (real ledger on `MoneyTestDatabase`, for give,
  take, refund and freeze through the page)

**What the page has** (staff pages are English only):
- **Mode.** A select with only the modes the gate allows, among Off and Play. Real comes in plan 6 and Gloebit in plan 4,
  so neither is offered here even when the gate opens. With the gate closed it shows Off and the line "Play coins become
  available once the money safety review is approved." Saving a mode change shows "The new mode takes effect when GridGate
  restarts."
- **Settings.** The spec §7.5 fields, validated:
  - `CurrencySymbol`: 1–4 characters, no whitespace.
  - `CurrencyName`: 1–20 characters.
  - The starting balances, `WeeklyAllowance`, `UploadFee` and `ClassifiedFee`: 0 to `MaxPayment`.
  - `DailyPayoutLimit`: 0 to 100,000,000.
  - `MaxPayment`: 1 to 1,000,000,000.
  - `ScriptPayouts` and `VisitorWallets`: checkboxes.

  It saves through `grid.UpdateSettings(s => s with { Money = … }, writeConfig: true)`. `ProgramKeys` is preserved.
- **Shown only when `gate.On`:**
  - **Totals:** `TotalsAsync`.
  - **Self-check:** `MoneyStatus.LastCheck`, and a "Check now" button that runs `SelfCheckAsync` and updates the status.
  - **Freeze all / Unfreeze:** both need a reason.
  - **Refund an entry:** entry id and reason.
  - **Move fees:** to a resident, by name search; amount and reason.
  - **Find a person:** a name search through the same Robust lookup `/people` uses, linking to Person.
- **`Person` page:** the balance (`BalanceAsync`), the 100 newest history lines, and these actions:
  - Give, Take (amount and reason);
  - Freeze or unfreeze the account;
  - Turn script payouts on or off for them (staff can turn it on for someone, spec §7.2).
- **Supervisors** see everything but get no forms. The POST handlers return 403 for them.
- **Audit:** every POST writes `audit.Write(User, ip, action, target, result)`.
  - The actions are `money-settings`, `money-give`, `money-take`, `money-refund`, `money-move-fees`, `money-freeze-all`,
    `money-unfreeze-all`, `money-freeze-account`, `money-unfreeze-account`, `money-payouts-staff`, `money-self-check`.
  - `target` is the person's name or the entry id. `result` is the ledger outcome in snake case.
  - Reasons go through `LogText.Clean(reason, 200)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] Task Closed_gate_offers_only_off()                    // GET /money as owner: select has one option "Off"; approval line shown
[Fact] Task Settings_save_and_audit()                        // POST symbol "GG": settings.Money.CurrencySymbol == "GG"; audit line "money-settings"
[Fact] Task Bad_symbol_is_refused()                           // "TOOLONG" → error, settings unchanged
[Fact] Task Program_keys_survive_a_settings_save()
[Fact] Task Supervisor_sees_no_forms_and_gets_403()
[Fact] Task Ledger_sections_hidden_when_off()                 // closed gate: no "Totals"
[Fact] Task Dashboard_shows_frozen_notice()                   // open gate, ledger frozen "test" → red notice with "test"
[Fact] Task Dashboard_shows_service_failure()                 // status.ServiceError set → notice
[Fact] Task Dashboard_quiet_when_off()                        // closed gate, ServiceError set → no notice
// integration
[Fact] Task Give_then_take_through_the_page()                // balances change; entries carry staff name and reason; two audit lines
[Fact] Task Refund_through_the_page()
[Fact] Task Freeze_all_through_the_page_blocks_payments()     // then ledger.TransferAsync → Frozen
[Fact] Task Staff_payout_switch_is_audited()                  // "money-payouts-staff", account payouts_allowed = 1
```

- [ ] **Step 2: Run them and see them fail** · [ ] **Step 3: Implement** · [ ] **Step 4: Run them and see them pass**
- [ ] **Step 5: Commit** — `feat(money): Money staff page, person page and dashboard notices`

---

### Task 10: My coins for residents

**Files:**
- Create: `Launcher/GridGate.Launcher/Pages/Guest/Coins.cshtml(.cs)` (`@page "/guest/coins"`)
- Create: `Launcher/GridGate.Launcher/Guests/MoneyPagesOnlyAttribute.cs`. It returns 404 when `!gate.On`, like
  `IarPagesOnlyAttribute`.
- Modify: `Pages/Guest/Index.cshtml`. Link "My coins" when `gate.On`.
- Modify: `ResidentText*.resx` (six files). Add the `MyCoins.*` keys, including one `MyCoins.Type.<entry_type>` per spec
  §7.4 type.
- Test: `Launcher/GridGate.Launcher.Tests/MyCoinsPageTests.cs`, `Launcher/GridGate.Launcher.IntegrationTests/MyCoinsLedgerTests.cs`

**What the page has:**
- **Balance:** `BalanceAsync(AccountRef.Resident(guest.Principal))`. The first visit creates the wallet with the starting
  balance, which is correct.
- **History:** the 50 newest lines, with date (the resident's culture), what (`MyCoins.Type.*`), who and amount (signed).
  - "Who" is the other resident's name, from a Robust account lookup cached per request; or "Visitor from {host of home}";
    or empty for system accounts.
  - Hold lines show their type text only ("Purchase, waiting for delivery", "Purchase completed", "Purchase returned").
    They never show a "who" or a balance for Holding.
- **"My scripts may pay out" switch,** off by default. The explanation text includes `DailyPayoutLimit` and the currency
  symbol. When `ScriptPayouts` is false grid-wide, the switch is replaced by `MyCoins.PayoutsOffGrid`.
- **Turning it on or off:** a POST `?handler=Payouts` with `on=true|false` → `SetPayoutsAllowedAsync`, then
  `audit.Write(new AuditEntry(name, principal, AuthSchemes.GuestRole, ip, "money-payouts", name, on ? "on" : "off"))`.
  This closes plan 1's deferred finding.
- **Errors:** `MoneyUnavailableException`, or an `Unavailable` outcome, shows `MyCoins.Unavailable` in a red box and no
  numbers.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] Task Page_is_404_when_money_is_off()
[Fact] Task Page_needs_a_resident_sign_in()                  // anonymous → challenge
// integration (open gate, MoneyTestDatabase, guest = a local user)
[Fact] Task First_visit_shows_starting_balance()             // "100"
[Fact] Task History_shows_names_and_signed_amounts()          // pay_avatar 30 to "Test Friend": line with "Test Friend" and "-30"
[Fact] Task Hold_lines_show_no_holding_balance()              // after hold+complete, lines say purchase texts; no line shows Holding's balance
[Fact] Task Payout_switch_turns_on_and_is_audited()           // POST on=true → payouts_allowed 1; audit "money-payouts" "on"
[Fact] Task Payout_switch_explains_the_daily_limit()          // text contains "5,000"/"5000" per culture and "C$"
[Fact] Task Grid_wide_off_hides_the_switch()
```

- [ ] **Step 2: Run them and see them fail** · [ ] **Step 3: Implement** · [ ] **Step 4: Run them and the whole unit suite and see them pass**
- [ ] **Step 5: Commit** — `feat(money): My coins page with history and the script payout switch`

---

### Task 11: Full automated check

- [ ] **Step 1:** Run `dotnet build GridGate.sln -c Release`. Expected: `0 Error(s)`.
- [ ] **Step 2:** Run `dotnet test --project Launcher/GridGate.Launcher.Tests -c Release` and
  `dotnet test --project tests/GridGate.Money.Tests -c Release`. Expected: all pass.
- [ ] **Step 3:** Run `$env:GRIDGATE_REQUIRE_INTEGRATION='1'; dotnet test --project Launcher/GridGate.Launcher.IntegrationTests -c Release -- --filter-class "*Money*" --filter-class "*PresenceCheck*" --filter-class "*MyCoins*"`,
  with `GRIDGATE_MARIADB_BIN` set. Expected: all pass, with no skips.
- [ ] **Step 4:** Run `dotnet test --project tests/GridGate.Platform.Tests -c Release` and
  `dotnet run --project tests/GridGate.Smoke -c Release -- --bin bin` (from `MERGING.md`'s Verify step, because OpenSim
  files changed). Expected: both pass.
- [ ] **Step 5:** Run `git grep -n "PlayCoinsApproved = true\|RealCoinsApproved = true"`. Expected: no output.

---

### Task 12: Live Firestorm checks

The service only runs with the gate open, so this task builds a local, uncommitted copy with the gate open.

**Setup:**
1. In the working tree only, set both `MoneyRelease` constants to `true`, then build Release.
2. Run the grid on this computer with the launcher (Local plan): two regions in **two programs**, so there are two region
   keys and a cross-program teleport.
3. In the Money page set the mode to Play, then restart GridGate.
4. Create two residents, A and B, and a group G founded by B.
5. Use Firestorm Release (`C:\Program Files\FirestormOS-Releasex64`). Run two instances for A and B; Firestorm allows a
   second instance with the same install.
6. Use computer-use to drive the viewers.

**Record** each result in `docs/superpowers/plans/2026-11-13-money-plan2-live-checks.md`, a table of action, what was
done, expected, seen, and pass/fail, with the Firestorm version. Check that the ledger and history agree with what the
viewer showed.

The checks, one row each:
1. **Login:** the status bar shows `C$ 100`. The currency symbol shows in the Pay window.
2. **Click the balance:** it refreshes.
3. **Pay avatar:** A pays B 10. Both viewers show their translated notices and new balances.
4. **Tip jar:** pay an object with a `money()` script that says the amount.
   - `money()` fires after the coins moved.
   - With `llSetPayPrice(PAY_HIDE, [10, 25, PAY_HIDE, PAY_HIDE])`, the buttons work.
   - A forced amount from a modified packet can't be made from Firestorm. Unit tests cover it; the row says so.
5. **Buy original:** the object, with coins moved.
6. **Buy copy:** a copy in inventory, with coins moved.
7. **Buy contents:** the items, with coins moved. An empty-contents sale is refused with nothing charged.
8. **Buy when short of coins:** refused. Nothing is delivered and nothing is charged.
9. **Buy a group-owned object:** refused with the group message.
10. **Pay a group-owned object:** refused with the group message.
11. **Buy land (priced):**
    - The Buy land window's Buy button is enabled; the pre-flight passed.
    - The parcel changes owner, and the seller is paid.
12. **Buy land (free):** works.
13. **Buy group-owned land:** refused, nothing charged.
14. **Buy land with too few coins:** the pre-flight says how many are missing.
15. **Parcel pass:** bought, the owner is paid.
16. **`llGiveMoney`:**
    - With A's payouts off: refused, and nothing moves.
    - With them on in My coins: paid.
    - With an amount of 0: nothing happens.
    - From a group-owned object: refused.
17. **`llTransferLindenDollars`:**
    - `transaction_result` reports success.
    - With payouts off: `TRANSFERS_DISABLED`.
    - A hypergrid recipient can't be tested on a Local grid; write "not checked live" and why.
18. **`PERMISSION_DEBIT`:** granted by B for A's object → `llGiveMoney` refused. With `auto_grant_all_perms` set on the
    region, debit is still asked.
19. **Upload fee (set to 5):**
    - A texture upload costs 5, and Firestorm shows the price before uploading.
    - The fee shows on the Money page under Fees.
    - Firestorm uploads through caps, so the UDP path can't be driven from Firestorm; write "not checked live".
20. **Group join fee:** B sets G's join fee to 15. A joins, A is charged 15, B is paid 15.
21. **Classified fee (set to 3):** a new classified costs 3.
22. **Teleport to the other program's region:** the status bar balance stays right.
23. **Buy currency window:** type an amount. The refusal text and the link to `/coins` show; there is no Second Life
    wording.
24. **Freeze all on the Money page:**
    - A payment in-world is refused with the paused text.
    - The dashboard shows the red notice.
    - Unfreeze.
25. **Money service stopped mid-sale:** use a firewall rule or stop the launcher's money port by changing the INI port for
    one program and restarting it. Buying gives "Money is unavailable right now", and nothing is charged.
26. **Money Off build (gate closed, normal build):**
    - Free land is still buyable.
    - The Buy currency window shows the "doesn't use money" text.
    - No `[GridGateMoney]` section appears in the region INIs.

- [ ] **Step 1: Run the checks and fill in the table.** For any failure, use superpowers:systematic-debugging, then fix it
  in the task that owns the code, with a test, as a new commit. Rerun the row.
- [ ] **Step 2: Restore the constants.** Run `git diff --stat Launcher/GridGate.Launcher.Core/Money/MoneyRelease.cs`.
  Expected: no output.
- [ ] **Step 3: Commit the results** — `docs(money): live Firestorm checks for plan 2`
- [ ] **Step 4: Open the PR.** It goes to `main`, from `feature/money-plan2`, and its body lists what was checked live and
  what wasn't. Wait for CI yourself; `--auto` merges at once on this repository. Don't merge without the owner.
