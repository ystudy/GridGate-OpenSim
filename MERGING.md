# Taking updates from OpenSimulator

The `OpenSim/` tree here comes from [OpenSimulator](http://opensimulator.org/). We take its changes by
cherry-picking commits from its `master` branch, a batch at a time, each batch in one PR.

This project began as a fork of Fred Beckhusen's Outworldz DreamGrid (`Outworldz/DreamGrid-Opensim`, branch
`V7`), and until November 2026 OpenSimulator's changes reached us through his tree. That repository is no
longer public, so we now take them from OpenSimulator directly.

## Where our tree stands

- **Base:** OpenSimulator `master` at `9d00a5555225b17d3d1063cb0f3496fb1b6217f9` (2026-11-07, "soem extra
  spaces"), version `0.9.3.1` Dev. Every upstream commit up to it has been picked or recorded. On top of it sit
  Fred's patches and our own changes.
- **How we got there:** Fred brought in upstream `78cb44c0` (2026-08-11) as "Update to August core"
  (`a19786cf`). "OpenSim merge 1" (PR #30) picked 34 later commits, and "OpenSim merge 2" picked the other 36.
- **No shared history.** Our history starts with Fred's import of the source in January 2025 ("V5 from Tandy"),
  so `git merge-base` finds no common commit with OpenSimulator. A `git merge` would treat every file that
  differs as a conflict, so we cherry-pick instead.
- **What we have taken since the base** is recorded in commit trailers: every commit picked from
  OpenSimulator carries `Upstream-OpenSim: <full upstream SHA>`.

## One-time setup

```bash
git remote add opensim https://github.com/opensim/opensim.git
```

That is OpenSimulator's official GitHub mirror of its own repository, `git://opensimulator.org/git/opensim`;
both carry the same commits. Use the mirror. It serves HTTPS, while opensimulator.org's HTTPS endpoint
(`https://opensimulator.org/git/opensim`) failed its TLS handshake when this was written.

On Windows, clone with `core.longpaths=true` (see `BUILDING.md`).

## Each update

Work in a branch named after the batch, e.g. `opensim-merge-2`.

### 1. List the commits we have not taken

```bash
git fetch opensim
BASE=9d00a5555225b17d3d1063cb0f3496fb1b6217f9
git log main --format='%(trailers:key=Upstream-OpenSim,valueonly)' | grep . | sort -u > picked.txt
git rev-list --reverse --no-merges $BASE..opensim/master | grep -vFf picked.txt > todo.txt
git log --no-walk=unsorted --date=short --format='%h %ad %an | %s' $(cat todo.txt)
```

Skip merge commits: a pull request's own commits are picked one by one.

Read the list before picking. Upstream messages sometimes say a commit is untested or broken ("wait before
pull"). Take those together with the commit that fixes them, or leave them for a later batch.

### 2. Pick them, oldest first

```bash
for c in $(cat todo.txt); do
  git cherry-pick "$c" &&
  git commit --amend --no-edit --trailer "Upstream-OpenSim: $c" || break
done
```

The loop stops at the first conflict. Resolve it (see below), then `git cherry-pick --continue`, add the trailer
with the `--amend` line above, remove the commits already done from `todo.txt` and start the loop again.

A commit we already have makes an empty pick. A commit we decide not to take gets no pick at all. Record both
so they drop off the list next time:

```bash
git cherry-pick --skip   # only if a pick is in progress
git commit --allow-empty -m "OpenSim: already have <subject>" --trailer "Upstream-OpenSim: <sha>"
git commit --allow-empty -m "OpenSim: not taking <subject>: <reason>" --trailer "Upstream-OpenSim: <sha>"
```

### 3. Fix up what a clean pick leaves broken

- **Project files.** The `.csproj` files and `GridGate.sln` are checked in here, and Prebuild is not run.
  Our projects list every source file (`EnableDefaultItems` is `false`), while OpenSimulator's Prebuild picks
  files up by pattern. So an upstream commit that adds a `.cs` file picks cleanly but fails to build. Add a
  `<Compile Include=...>` to the matching `.csproj` (remove it for a deleted file). When upstream changes its
  `prebuild.xml`, make the same change by hand. To see the batch's project changes, diff upstream's own
  files, using the upstream SHAs of the first and last commits you picked:

  ```bash
  git diff <first-upstream-sha>^ <last-upstream-sha> -- prebuild.xml 'addon-modules/*/prebuild*.xml'
  ```

  Our own `prebuild.xml` files are not used by anything. If a pick conflicts on one, take theirs.

  - **Changed reference:** edit the project's `.csproj`. Reference paths use `$(GgRoot)` (the repo root),
    never an absolute path.
  - **New project:** create a `.csproj` modeled on a sibling project, then
    `dotnet sln GridGate.sln add <path-to-csproj>`.
  - **Removed project:** `dotnet sln GridGate.sln remove <path-to-csproj>` and delete the file.

  The target framework is set once in `Directory.Build.props`; do not add `<TargetFramework>` to a project.
  Put these fixes in a commit of their own after the picks ("OpenSim merge N: build the upstream picks").

- **Config templates.** OpenSimulator documents new settings in `bin/*.ini.example`. GridGate writes its
  configuration from `bin/Robust.HG.ini.proto` and `bin/config-include/Proto/*/OpenSim.ini`, and the launcher
  tests compare the result with `Launcher/GridGate.Launcher.Tests/Snapshots/`. Carry new or changed settings
  over to those files by hand, in a commit of their own.

### Conflicts to expect

- **DreamGrid patches.** Fred patched some OpenSimulator files, for example Smart Start in
  `GridService.cs`, `HypergridLinker.cs`, `LLLoginService.cs` and `UserAgentService.cs`, and
  `OutboundUrlFilter.cs`. These are most of the source conflicts. Keep the GridGate behavior and fit the
  upstream change around it. The patches are usually marked with comments naming GridGate or Smart Start; `grep -rn 'SmartStart\|GridGate'
  OpenSim` finds most of them.
- **Our own changes.** We have changed OpenSim code too (ubODE fixes, the Rapier physics backend, login and
  logging fixes). Look at the blame on our side before you take upstream's side of a hunk.
- **Third-party binaries in `bin/`.** OpenSimulator tracks prebuilt third-party DLLs (libomv, CSJ2K, and
  others), and so do we. When it updates one, the pick conflicts as binary. Take theirs:
  `git checkout --theirs <file> && git add <file>`. Then check that every DLL from the same library was
  updated together.
- **Build outputs in `bin/`.** These are built, not tracked: only third-party binaries and hand-maintained
  files like `*.dll.config` are tracked in `bin/`. OpenSimulator does not track its build outputs either, so a
  pick should not touch them. If one does, `git rm --cached` the file.
- **Upstream CI.** `.github/workflows/msbuildnet.yml` is OpenSimulator's own workflow. It only runs in
  `opensim/opensim`. Skip upstream changes to `.github/`.

### Verify

```bash
dotnet build -c Release GridGate.sln
dotnet test --project tests/GridGate.Platform.Tests -c Release
dotnet run --project tests/GridGate.Smoke -c Release -- --bin bin
```

All three must pass before you open the PR. The PR CI then runs the full set on Debian 12, Debian 13 and
Windows.

### Move the base forward

Once every commit up to some upstream commit has been picked or recorded, update `BASE` above (and the
"Where our tree stands" section) to that commit, so the next list starts there.

## GridGate's own changes to OpenSim

Changes we made to OpenSimulator's own files for GridGate's money system (money design §7.8). Each is one commit,
marked in the code with a `GridGate:` comment. When an upstream pick touches the same lines, keep these.

| Commit | Files |
|---|---|
| `fix(opensim): PERMISSION_DEBIT must come from the object's owner` | `OpenSim/Region/ScriptEngine/Shared/Api/Implementation/LSL_Api.cs` (`llGiveMoney`, `llTransferLindenDollars`) |
| `fix(opensim): auto_grant_all_perms never grants PERMISSION_DEBIT` | `LSL_Api.cs` (`llRequestPermissions`) |
| `feat(opensim): fire OnCrossAgentToNewRegion when an avatar crosses` | `OpenSim/Region/CoreModules/Framework/EntityTransfer/EntityTransferModule.cs` (`CrossAgentToNewRegionAsync`) |
| `fix(opensim): llGiveMoney refuses amounts of 0 or less` | `LSL_Api.cs` (`llGiveMoney`) |
| `fix(opensim): llGiveMoney refuses group-owned objects` | `LSL_Api.cs` (`llGiveMoney`) |
| `feat(opensim): llTransferLindenDollars can pay hypergrid visitors` | `OpenSim/Framework/IVisitorMoneyModule.cs` (new), `OpenSim/Framework/OpenSim.Framework.csproj`, `LSL_Api.cs` (`llTransferLindenDollars`) |
| `fix(opensim): only the owner can grant PERMISSION_DEBIT, and a script answer grants only what its script asked` | `LSL_Api.cs` (`llRequestPermissions`, `handleScriptAnswer`, `llAttachToAvatarTemp`), `OpenSim/Region/ScriptEngine/Shared/Api/Implementation/DebitPermission.cs` (new), `OpenSim/Region/ScriptEngine/YEngine/XMRInstCtor.cs` (both state restores) |
| `fix(opensim): UDP uploads are charged the upload fee` | `OpenSim/Region/CoreModules/Agent/AssetTransaction/AssetXferUploader.cs` |
| `fix(opensim): the UDP upload fee is charged where the asset is stored, by the type checked at upload` | `AssetXferUploader.cs` (`StartUpload`, `StoreUploaded`, `CompleteCreateItem`), `AssetTransactionModule.cs` (`UploadFeeApplies`) |
| `fix(opensim): a UDP texture upload may be saved as any texture item` | `AssetTransactionModule.cs` (`UploadTypesMatch`) |
| `fix(opensim): every texture variant pays the UDP upload fee; type mismatches are refused for updates too, only when a fee is in force` | `AssetXferUploader.cs` (`RefuseTypeMismatch`, used by `CompleteCreateItem`, `CompleteItemUpdate`, `CompleteTaskItemUpdate`), `AssetTransactionModule.cs` (the texture family in `UploadFeeApplies`) |
| `fix(opensim): classifieds are checked and charged at the money module's fee` | `OpenSim/Framework/IClassifiedFeeModule.cs` (new), `OpenSim/Framework/OpenSim.Framework.csproj`, `UserProfileModule.cs` (`ClassifiedInfoUpdate`) |
| `feat(money): Gloebit's DLL and INI only in Gloebit mode; no leftover G$ currency` | `bin/Robust.HG.ini.proto` (`[LoginService] Currency="G$"` commented out: GridGate writes the currency), `bin/config-include/Proto/Robust/OpenSim.ini` (the upstream launcher's `[Gloebit]` section with placeholder values and a Robust connection string removed: GridGate writes `[Gloebit]` in Gloebit mode) |
| `fix(money): no upstream Gloebit credentials in the tree; ship the built Gloebit DLL; Gloebit build warnings` | `bin/Gloebit.ini`, `bin/config-include/Gloebit.ini`, `bin/config-addon-opensim/Gloebit.ini` (Outworldz's Gloebit apps and contact details) and the committed `bin/Gloebit.dll.Proto` (an old upstream build) removed: GridGate writes `[Gloebit]` and the package builds the Proto from `addon-modules/Gloebit`. `addon-modules/Gloebit/GloebitMoneyModule/GloebitMoneyModule.cs`: obsolete `RegionInfo.originRegionID` replaced with `RegionID` (the getter returns it) |
| `feat(opensim): Robust records where each session starts` | `OpenSim/Data/ISessionStartsData.cs` (new), `OpenSim/Data/MySQL/MySQLSessionStartsData.cs` (new), `OpenSim/Data/MySQL/Resources/GridGateSessionStarts.migrations` (new), `OpenSim.Data.csproj`, `OpenSim.Data.MySQL.csproj`, `OpenSim.Services.LLLoginService.csproj` (references OpenSim.Data), `LLLoginService.cs` (`RecordSessionStart`), `GatekeeperService.cs` (`CreateAgent` path in `LoginAgent`) |
| `fix(opensim): session-start warnings name only the error type; the crossing event says what it is for` | `LLLoginService.cs`, `GatekeeperService.cs` (the session-start warnings), `EntityTransferModule.cs` (the comment at the crossing trigger) |
| `fix(opensim): group join, invitation, classified and caps upload fees are held before the action` | `OpenSim/Framework/IFeeHoldModule.cs` (new), `OpenSim.Framework.csproj`, `Addons/Groups/GroupsModule.cs` (`JoinGroupRequest`, invitation accept), `XmlRpcGroups/GroupsModule.cs` (same), `UserProfileModule.cs` (`ClassifiedInfoUpdate`), `BunchOfCaps.cs` (`UploadCompleteHandler`; the rest of it moved, body unchanged, into the new private `StoreUpload`) |
| `fix(opensim): an invitation to a paid group says the join fee and gives it to the viewer` | `Addons/Groups/GroupsModule.cs` (`InviteGroup`, `JoinFeeOnAccept`, `InvitationBucket`), `XmlRpcGroups/GroupsModule.cs` (same); `IFeeHoldModule.cs` (the `IFeeHold.Settle` comment) |
| `fix(scripts): a debit permission saved in script state from another program or an archive is not restored` | `OpenSim/Framework/ISceneObject.cs` and `SceneObjectGroup.cs` (`StateFromOutside`, cleared in `CloneForNewScene`), `OpenSim/Server/Handlers/Simulation/ObjectHandlers.cs` (`DoObjectPost`), `OpenSim/Framework/ChildAgentDataUpdate.cs` (`AgentData.Unpack`, the attachment objects), `ArchiveReadRequest.cs` (`LoadObjects`), `SceneXmlLoader.cs` (`LoadPrimsFromXml`, `LoadPrimsFromXml2`), `XMRInstCtor.cs` (`RestoredPermsMask`), `DebitPermission.cs` (`FromRunningScript`) |
| `fix(scripts): a debit permission from another program or an archive is not kept in the saved script state either` | `SceneObjectGroup.cs` (`StateFromOutside` setter, both `LoadScriptState`s), `SceneObjectGroup.Inventory.cs` (`SetState`, `DropDebit`, `DropDebitFromSavedScriptStates`, `DropDebitFromStoredStates`) |
| `fix(opensim): script state from before this version loses its debit permission once, so only state this program wrote keeps it` | `DebitPermission.cs` (`Mark`, `IsMarked`, `FromRunningScript` asks for the mark), `XMRInstCapture.cs` (`GetExecutionState` marks the state it writes), `XMRInstCtor.cs` (`LoadScriptState` reads the mark; `processXstate`, a state from XEngine, never has it; `RestoredPermsMask`). XEngine is not in this tree |
| `fix(money): a paired server counts only the logins and handoffs that went to its own address` | `GridGateSessionStarts.migrations` (version 2: `ServerIP`, `ServerPort`), `ISessionStartsData.cs` (`Record` takes the address; `SessionStartRow`), `MySQLSessionStartsData.cs` (`Record`), `LLLoginService.cs` (`RecordSessionStart` takes the destination), `GatekeeperService.cs` (the session-start record in `LoginAgent`) |
| `feat(opensim): a group invitation keeps the join fee it showed` | `OpenSim/Data/MySQL/Resources/GridGateGroupInvites.migrations` (new; assumes the default groups realm `os_groups`, as `os_groups_Store` does), `OpenSim.Data.MySQL.csproj`, `MySQLGroupsData.cs` (`MySqlGroupsInvitesHandler` runs the `GridGateGroupInvites` store), `IGroupsServicesConnector.cs` (`GroupInviteInfo.Fee`, `AddAgentToGroupInvite` takes a fee), `GroupsService.cs` (`AddAgentToGroupInvite` stores a fee only when it is above 0: a free invitation stores none, so a failed migration cannot break it; `GetAgentToGroupInvite`), `GroupsExtendedData.cs` (`GroupInviteInfo` both ways, `ReadFee`), `GroupsServiceRemoteConnector.cs`, `GroupsServiceRobustConnector.cs` (`HandleInvite`), `GroupsServiceRemoteConnectorModule.cs`, `GroupsServiceLocalConnectorModule.cs`, `GroupsServiceHGConnectorModule.cs` (`AddAgentToGroupInvite`), `GroupsModule.cs` (passes no fee yet) |
| `fix(opensim): accepting a group invitation charges at most the fee it showed, and a member never pays the join fee twice` | `OpenSim/Framework/JoinFeeOffers.cs` (new), `OpenSim.Framework.csproj`, `Addons/Groups/GroupsModule.cs` (`m_joinFeeOffers`, `InviteGroup`, the invitation accept and decline branches, `SendFeeOffer`, `JoinGroupRequest`) |
| `fix(opensim): the older groups module charges at most the invitation's fee, and both groups modules take one paid join at a time per person and group` | `OpenSim/Framework/JoinsInProgress.cs` (new), `OpenSim.Framework.csproj`, `Addons/Groups/GroupsModule.cs` (`m_joinsInProgress`, the invitation accept branch: only the invitee accepts a paid invitation, and no money module means no fee; `JoinGroupRequest`), `Region/OptionalModules/Avatar/XmlRpcGroups/GroupsModule.cs` (`m_joinFeeOffers`, `m_joinsInProgress`, `InviteGroup`, the invitation accept and decline branches, `SendFeeOffer`, `JoinGroupRequest`) |
| `fix(opensim): UDP uploads hold the fee before the asset is stored, and texture or sound data pays the fee whatever type it is uploaded as` | `AssetXferUploader.cs` (`StoreUploaded` and its callers `SendCompleteMessage`, `CompleteItemUpdate`, `CompleteTaskItemUpdate`, `CompleteCreateItem`), `OpenSim/Framework/UploadData.cs` (new), `OpenSim.Framework.csproj` |
| `fix(opensim): a refused UDP item update puts the item's old asset back` | `AssetXferUploader.cs` (`RequestUpdateInventoryItem`, `RestoreItemAsset`, and the call to it in `CompleteItemUpdate`). The restore also applies with no money module: it fixes upstream leaving the item pointing at an asset that was never stored |
| `fix(opensim): caps uploads are priced by the type they are stored as and by their data, a mesh upload pays once anything is stored, and a model can't hold more than it was priced for` | `OpenSim/Region/ClientStack/Linden/Caps/BunchOfCaps/BunchOfCaps.cs` (`StoredAssetType` new, `NewAgentInventoryRequest` incl. the model's counts, `UploadCompleteHandler`, `StoreUpload`) |
| `fix(opensim): a refused UDP task item update puts the task item's old asset back, an early item update keeps the first old asset, and a model's counts are set outside the client check` | `AssetXferUploader.cs` (`RequestUpdateInventoryItem`, `RequestUpdateTaskInventoryItem`, `RestoreTaskItemAsset`, `CompleteTaskItemUpdate`), `AgentAssetsTransactions.cs` (`RequestUpdateTaskInventoryItem` passes the part), `BunchOfCaps.cs` (`NewAgentInventoryRequest`) |
| `fix(opensim): the task script update cap refuses texture and sound data while an upload fee is in force, and a second task item update keeps the first old asset` | `Scene.Inventory.cs` (`CapsUpdateTaskInventoryScriptAsset`), `AssetXferUploader.cs` (`RequestUpdateTaskInventoryItem`) |
| `fix(opensim): item update caps refuse texture and sound data while an upload fee is in force` | `Scene.Inventory.cs` (`CapsUpdateItemAsset`, which the agent-item and task-item update caps both pass through), `OpenSim/Framework/UploadData.cs` (`RefusesItemUpdate`) |
| `fix(opensim): a parcel pass is bought once at a time, a renewal replaces only the buyer's own entry, and a pass being checked isn't told to try again` | `OpenSim/Framework/IMoneyModuleTellsPayer.cs` (new), `OpenSim.Framework.csproj`, `LandManagementModule.cs` (`ClientParcelBuyPass`, `TakeOwnPass`, `m_passesInFlight`) |
| `fix(opensim): script payouts run on their own queue, pause for thirty seconds after one gets no answer, and are limited to sixty a minute per owner` | `Plugins/Payouts.cs` (new), `AsyncCommandManager.cs` (`PayoutsPlugin`), `LSL_Api.cs` (`llGiveMoney`, `llTransferLindenDollars`: payouts go to the payout queue, not the dataserver pool) |
| `fix(opensim): a pass paid during a parcel edit lands on the live parcel, a land re-check accepts every for-sale flag, payouts the pause refuses don't use the owner's allowance` | `LandManagementModule.cs` (`ClientParcelBuyPass`: the paid job reads the parcel's `LandData` again) |
| `fix(money): uploads wait for the fee before pricing, joins find the founder by group id and refuse when the group can't be read, a late land hold is returned, absent visitors known only by name aren't paid, and fees need a money service with holds` | `OpenSim/Framework/IFeeHoldModule.cs` (`IUploadFeeKnown`), `OpenSim/Framework/UploadData.cs` (`RefusesItemUpdate`), `BunchOfCaps.cs` (`NewAgentInventoryRequest`: an error while the money module does not know the fee), `Addons/Groups/GroupsModule.cs` and `Region/OptionalModules/Avatar/XmlRpcGroups/GroupsModule.cs` (the join fee is taken by group id; `JoinGroupRequest` and the invitation accept branch refuse a group whose record can't be read while a money module is present) |
| `fix(opensim): a mesh upload is priced again from its data at completion, animation data is recognised whatever type it claims, and temporary uploads are limited per avatar` | `OpenSim/Framework/UploadData.cs` (`IsAnimation`, `IsFeeData`, `TemporaryUploadLimit`), `BunchOfCaps.cs` (`StoreUpload`: a model whose data costs more than was quoted and held is refused), `AssetXferUploader.cs` and `BunchOfCaps.cs` (texture, sound and animation data pays the fee whatever type it claims), `AssetTransactionModule.cs` (`HandleUDPUploadRequest`: 30 temporary uploads a minute per avatar) |
| `fix(opensim): a script payout is paid from the owner who made the call, only while they still own the object and hold the debit permission, and an owner has at most sixty waiting` | `Plugins/Payouts.cs` (`TryEnqueue`, `MaxWaitingPerOwner`), `LSL_Api.cs` (`llGiveMoney`, `llTransferLindenDollars`, `PayoutStillAllowed`) |
| `fix(opensim): a script state the engine writes from an object's asset or from outside has no debit permission and no mark` | `IScriptModule.cs` (`SetXMLState` with `fromRunningScript`, a default member), `SceneObjectPartInventory.cs` (`RestoreSavedScriptState`), `SceneObjectGroup.Inventory.cs` (`SetState`), `XMREngine.cs` (`SetXMLState`), `DebitPermission.cs` (`ForStoredState`) |
| `fix(opensim): a direct group join and a parcel pass charge at most the price the viewer was last sent, and refuse a higher one with the current price` | `OpenSim/Framework/ShownPrices.cs` (new), `OpenSim.Framework.csproj`, `Addons/Groups/GroupsModule.cs` and `Region/OptionalModules/Avatar/XmlRpcGroups/GroupsModule.cs` (`GroupProfileRequest` remembers the fee, `JoinGroupRequest` charges no more), `LandObject.cs` (`SendLandProperties` remembers the pass price), `LandManagementModule.cs` (`ClientParcelBuyPass`) |
| `fix(opensim): accepting a group invitation charges only a fee this program offered the invitee, and offers the fee first otherwise` | `Addons/Groups/GroupsModule.cs` (the invitation accept branch no longer falls back to the fee stored with the invitation) |

The script changes (the six `LSL_Api.cs` rows) are tried against a real `LSL_Api` in a test region by
`tests/GridGate.Lsl.Tests`, which CI runs on Linux and Windows, and so are the places the saved-debit row marks an
object whose script state comes from outside. After an upstream pick that touches `LSL_Api.cs` or one of those files, run
it:

```bash
dotnet test --project tests/GridGate.Lsl.Tests -c Release
```

Those tests build OpenSim's own test helpers (`OpenSim/Tests/Common`, as the `tests/OpenSim.Tests.Common` project). The
helpers had fallen behind OpenSim's interfaces, so we changed them too:

| Commit | Files |
|---|---|
| `test(opensim): OpenSim's test helpers build again` | `OpenSim/Tests/Common/Mock/MockScriptEngine.cs`, `TestClient.cs`, `TestEventQueueGetModule.cs`, `TestXInventoryDataPlugin.cs` (missing interface members added), `MockRegionDataPlugin.cs` (`GetExtra` returns an empty set, so a test region keeps extra settings) |
| `test(opensim): a test avatar works on Linux` | `OpenSim/Tests/Common/Mock/TestClient.cs` (its wait handle is unnamed: .NET has no named ones on Linux) |

`Mock/TestLLUDPServer.cs` and `Helpers/ClientStackHelpers.cs` no longer match `LLUDPServer` and aren't built. If upstream
fixes the helpers, take its version and drop ours.

The money module itself is not an OpenSim change: it lives in `addon-modules/GridGateMoney`.
