# Money plan 3: live checks

The checks from Task 9 of `2026-11-14-money-plan3-visitors-region-servers.md`, run on 2026-11-14 (US Pacific; 2026-11-15 UTC).

**Setup:**
- **This computer (Windows 11):** a Grid Asset Server on a fresh data folder, network-only address 192.168.86.206.
  - Region Welcome Tester at 1000,1000.
  - Residents Alice Tester and Bob Tester (signed up and approved). The owner avatar is Tom Sherry.
  - Money mode Play.
  - It runs a separate worktree at `55721427`, with `MoneyRelease.PlayCoinsApproved` set to `true` in that working tree only. That change was never committed.
- **The Debian 12 test VM (192.168.86.37):**
  - This branch's Linux package `0.0.0-dev+5572142`, from release.yml run 43659569236.
  - Its `GridGate.Launcher.dll` and `GridGate.Launcher.Core.dll` were replaced by the gate-open build above. They are portable IL from the same commit, and the originals were kept.
  - Set up as a Region Server joined to 192.168.86.206, first region port 9000, with region R1 at 1003,1003 (UDP 9001).
- **Who did what:**
  - The owner did both setups, the website sign-ins, and the Firestorm logins (Firestorm Release 7.2.5.81788, two copies).
  - Claude drove the web pages through the owner's signed-in browser tabs and the viewers through computer use, and read the ledger, the logs and the INIs. Claude typed no password.

| # | Check | What was done | Expected | Seen | Result |
|---|---|---|---|---|---|
| 1 | Grid info | `GET :8002/get_grid_info` on the Grid Asset Server; the VM's saved join | `gridgate_money play`, port, fingerprint; the VM reads them | `gridgate_plan grid-asset`, `gridgate_money play`, `gridgate_money_port 8000`, the fingerprint; the VM's join has Money Play, port 8000, the same fingerprint and the economy helper. Robust needed one restart after setup to publish them (it was started before setup finished). | Pass |
| 2 | Unpaired Region Server | R1's INI; Alice pays Bob 5 in R1 | `Paired = false`; told "This region isn't connected to the grid's money yet."; nothing moves | INI `EconomyModule = GridGateMoneyModule`, `Paired = false`, HelperURI the grid's; region log "Money is on, but this Region Server isn't paired with its grid's money yet."; Alice was told the region isn't connected to money yet; ledger: only the two starting balances, both 100 | Pass |
| 3 | Make a code and pair | Money page → Make a pairing code; the VM's `/advanced/money`: a typo first, then the code, then the same code again; this computer's settings file | Typo refused; "Paired"; listed here; a reused code refused; only the key's hash stored | Typo: "The Grid Asset Server didn't accept this code…". Real code: "Paired. Restart the regions to start taking payments." This page: "192.168.86.37, region ports from 9000, paired 2026-11-15". Reuse refused (audit on both sides: refused, ok, refused); the pairing kept. The settings file has `keyHash` only; the key appears in no settings or log file here. Check: "The grid's money service accepts this server's key." | Pass |
| 4 | Restart R1 and pay | R1 → Power → Restart; Bob pays Alice C$10 in R1 | Both balances change; the entry has R1's region id | R1's log: "Money is on; the money service is at 192.168.86.206:8000." Bob C$ 90, Alice C$ 110; ledger entry 3 `pay_avatar` 10, region 02319881… (R1). The restart logged both viewers out, as any region restart does. | Pass |
| 5 | Buy in R1 | Bob rezzes Sale Box, for sale Original C$10; Alice buys (pie More → Buy) | The hold completes | "Buy for C$10 from Bob Tester?" → bought; both C$ 100; hold 1 (R1, C$10) completed: entries 4 `hold` and 5 `hold_completed`. Firestorm greyed the pie's Buy for a Copy sale whose next owner can't copy; Original worked. | Pass |
| 6 | Teleport between the computers | Alice: Welcome Tester → R1 → Welcome Tester | The status bar balance stays right | C$ 100 in each region, Windows and Unix simulators | Pass |
| 7 | The key stays in its own regions | From the VM, `curl` with the paired key: Alice's balance with her real session while she is on Welcome Tester; then `health` | `wrong_region` | `{"code":"wrong_region"}`; health `{"ok":true,"regions":1}` (R1 only). Nothing secret printed. | Pass |
| 8 | Money port from outside the LAN | — | — | Not checked live: neither computer has a public address. Covered by `MoneyPlaceTests.Public_address_is_dropped` and `Middleware_drops_outsiders_before_anything_else`. | Not checked live |
| 9 | Remove the Region Server | This Money page → Remove; Bob pays Alice C$1 in R1; the VM's Check | "Money is unavailable"; Check says the key is no longer accepted | "Removed. Its regions can't take payments until it's paired again."; Bob: "Money is unavailable right now. Please try again later."; Check: "The grid no longer accepts this server's key. Pair again." | Pass |
| 10 | Wrong and reused codes | New code; five wrong codes on the VM, then the right one | All six refused; nothing paired | Six "refused" in the VM's audit (01:55:20–01:55:39); no paired server here | Pass |
| 11 | Hypergrid visitor | — | — | Not checked live: these plans need a public address for hypergrid, and the VM can't be a Region Server and a second grid at once. Covered by `MoneyLedgerVisitorTests`, `PresenceCheckTests.Gatekeeper_row_makes_a_known_visitor`/`The_first_home_seen_wins`, `VisitorDirectoryTests`, `MoneyActionsVisitorTests`. | Not checked live |
| 12 | Money Off build | Both computers back on the committed code (`MoneyRelease` false): this computer's launcher from `feature/money-plan3` at e25e1652, the VM's original package files; the world here restarted, R1 restarted | `gridgate_money off`; no `[GridGateMoney]` on the VM; free land still works in R1 | No money service started here. Grid info: `gridgate_money off`, no port or fingerprint keys. R1's and Welcome Tester's INIs: `EconomyModule=` empty, no `[GridGateMoney]` section; neither region logged "Money is on" after its restart. Free land in R1 not checked live: R1's land belongs to the estate owner, who wasn't logged in; plan 2's row 26 covers free land with money Off. | Pass (free land not checked) |

## Found while checking

- **The dashboard reported a fault where there was none.**
  - After Play coins were chosen, and before a restart, the dashboard showed a red "money database can't be reached". The money service and its database only start with GridGate.
  - Fixed in `e25e1652`, with `Dashboard_says_money_starts_on_restart` written first and seen to fail. The dashboard now shows a calm info line: payments start when GridGate restarts.
- **Robust wasn't restarted after setup.**
  - Robust was started before setup finished, and GridGate restarted later without restarting it. So its grid info kept the pre-setup values until the world was restarted from the dashboard.
  - That's existing behaviour outside money, recorded here and not changed. A Region Server can't join until the Grid Asset Server's grid info names its plan.
- **Setup places a Region Server's first region on the first free square, without asking.** That's plan 6b's design. The owner asked for a choice; that work is a separate PR, ystudy/GridGate-OpenSim#83.
- **Wording:** the Money page's Region Servers section said "A Region Server's key works only for its own regions" twice, once in its help tip. The help tip now says how pairing is done.

## Not checked live

- **The money port from outside the LAN (check 8)** and **a hypergrid visitor (check 11).** The reasons and the covering tests are in the table.
- **Free land in R1 with money Off (check 12).** R1's land is the estate owner's, and the owner wasn't logged in. Plan 2's live check 26 covered free land with money Off.

## Restored afterwards

- **Both `MoneyRelease` constants are `false`** in the committed tree. `git diff --stat Launcher/GridGate.Launcher.Core/Money/MoneyRelease.cs` on `feature/money-plan3` prints nothing.
- **The VM runs the package's own launcher files again.**

## Final review

- **The reviewer:** a fresh one (opus), read-only, given the branch, the plan and the design. Its verdict was "With fixes", with no Critical findings.
- **Important 1** (one `GridUser` row per UUID, so a visitor's new home is never known): fixed in `488ec701`.
- **Important 2** (Region Servers sharing one address): fixed in `55721427`, along with Minors 2, 3, 4, 5, 6, 7, 8, 10 and 11.
- **Minor 1** (a LAN machine can plant a `GridUser` row and get an empty junk wallet): recorded in the plan as a known limit, for plan 5's review.
- **Minor 9** (an unpaired Region Server charges no fees): recorded in the plan's Decisions.
- **Fresh re-review of the fixes** (`4d1a75d4..a4dd7633`), verdict "With fixes", no Critical findings:
  - **Important** (a key's ports anchored on the first-port setting, not on the regions' real ports): fixed. The pairing
    sends the lowest live region port, and Check reports partial coverage.
  - **Minors fixed:** drop logging resets with the window; address tallies are hard-capped; the same machine re-pairing
    replaces its own key, with the ports and the replacement logged; a re-point during pairing saves nothing.
  - **Minor recorded:** a visitor with no wallet at a moved home, as a known limit in the plan.
  - **Minor left:** a `FirstPort = 0` entry from a dev settings file is never replaced. Released settings never have one,
    because money is release-gated.
  - Not seen live: these fixes came after the live checks and are covered by tests.
