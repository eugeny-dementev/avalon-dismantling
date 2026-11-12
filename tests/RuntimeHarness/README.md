# Isolated runtime harness

This optional BepInEx plugin tests the installed game APIs against a copied profile. Never include it in a public release ZIP.

The harness uses a separate plugin GUID and requires `com.eugenydementev.avalondismantling`.

## Build

```powershell
dotnet build tests/RuntimeHarness/AvalonDismantling.Harness.csproj -c Release -p:GameRoot="C:\path\to\game"
```

The output is `bin/Release/netstandard2.1/AvalonDismantling.Harness.dll` within this directory.
Game assemblies remain local references. Build the main plugin before testing.

## Prepare an isolated profile

1. Wait for the game to close.
2. Back up the production profile and installed mod configuration.
3. Copy the complete production profile into a new directory under `%LOCALAPPDATA%\Codex\artifacts`.
4. Include both `Saved` and `Unsynchronized`, plus any other profile subdirectories.
5. Create `AVALON_DISMANTLING_ISOLATED_PROFILE.txt` inside the copied profile.
6. Set its content to exactly `avalon-dismantling-runtime-test`.
7. Set `AVALON_DISMANTLING_TEST_PROFILE` to the copied profile's absolute path for the test process.
8. Temporarily install the harness DLL alongside the current main plugin and core DLL.
9. Launch the game with that process environment.
10. Check `startup.json` before issuing any load command.

Production profiles normally use this directory:

```text
%USERPROFILE%\AppData\LocalLow\Questline\Fall of Avalon\<account-id>
```

Use a separate test destination:

```text
%LOCALAPPDATA%\Codex\artifacts\<test>\profile
```

No command runs without the explicit marker. The harness rejects production paths, game-directory paths, and junctions or symbolic links.

The harness replaces the cloud-service factory with `DebugCloudService` and redirects `CloudService.DataPath` before initialization.
This redirects save slots, file preferences, and large-file storage. It also redirects save caches and suppresses legacy save migrations.
ZIP cache consumers call an explicit harness path getter. The harness does not modify static readonly cache fields.
Inventory test patches install only after the hero initializes, because native inventory initialization requires loaded game references.
An isolation failure requests game exit with code 91. Do not continue if the startup report is missing or reports failure.

## Commands

Write one command into `command.txt` inside the isolated profile. Write a temporary file, then rename it to `command.txt` atomically.
Wait for `last-command.json` to report completion before sending another command.

| Command | Action |
|---|---|
| `status` | Write readiness, wallet, save-path, and UI information into `status.json`. |
| `load-latest` | Complete the native Press Start transition, then invoke Continue for the latest copied save. |
| `open-bench` | Open the native bench menu with a loaded handcrafting station configuration. |
| `close-bench` | Close the dismantling and bench test models. |
| `run-tests` | Run native inventory tests and write `native-tests.json`. |
| `run-bulk-tests` | Check eligible filters, review, cancellation, queue safety, and complete batches; write `bulk-tests.json`. |
| `benchmark-bulk` | Time eleven synthetic queue entries and verify that pending native inventory rows retain their identities. |
| `benchmark` | Time three synthetic sword confirmations with the copied inventory; verify retained and removed native rows. |
| `run-ui-tests` | Check selection, cancellation, reopening, and native confirmation callbacks; write `ui-tests.json`. |
| `test-vanilla-crafting` | Close a test Dismantle menu, open native Craft, and select a learned recipe without crafting. |
| `preview-item` | Open Dismantle through its native bench button and preview an existing eligible item for screenshots. |
| `preview-currency` | Render a synthetic Cobweb ingredient preview and verify unchanged wallets and inventory after cleanup. |
| `save-test` | Dismantle a synthetic item and create a new isolated persistence-test save. |
| `verify-persistence` | Compare recovered material counts and wallet values after reloading the persistence-test save. |
| `screenshot` | Capture the current rendered game frame into `screenshot.png`. |
| `resolution1080` | Temporarily request 1920 × 1080 rendering through Unity. |
| `resolution4k` | Temporarily request 3840 × 2160 rendering through Unity. |
| `resolution-restore` | Restore the original render dimensions, display mode, and refresh rate. |
| `quit` | Restore any temporary resolution, then request normal game exit. |

`open-bench` prefers a loaded station with both weapon and armor recipes.
Gameplay commands wait until the hero initializes and the native loading screen closes.
Queued gameplay commands write their remaining readiness conditions into `readiness.json`.
The load command waits for native title initialization; it does not bypass startup popups or hide title canvases.
The harness leaves the bench open for screenshots and manual navigation checks.
The vanilla crafting check leaves its selected recipe open and writes `vanilla-crafting.json`.
Run `close-bench` before the vanilla crafting check.
Run `close-bench` before `preview-item`.
The item preview prefers an upgraded Hammer of Stupefaction, then an automatic recipe match, then any eligible carried item.
It creates no items and performs no transaction. It writes item details, recipe ranges, and screen dimensions into `item-preview.json`.
Resolution commands call Unity directly and do not change the game's settings model or write preferences.
Use `resolution-restore` or `quit` afterward so Unity exits with the original display mode.
Check `resolution.json` before capturing screenshots; unsupported display sizes fail the command.

Native tests cover exact recipes, automatic hammer matching, ammunition batches, upgrade estimates, and separate New Game Plus bonuses.
They also cover cancellation, favourite, locked, and attached relic protection, clean stolen-item recovery, rejected additions, replacements, post-add exceptions, and reentry.
The relic fixture attaches a synthetic weapon relic through the native `ItemGems.AttachGem` method.
Additional protection checks use suitable existing items from the copied profile.
The report marks unavailable fixtures as skipped. A skipped case is not a passing result.

Run `close-bench` before `run-bulk-tests`.
Bulk fixtures temporarily lock original eligible copied-profile items, so only synthetic sources enter test queues.
Each fixture removes those temporary locks and restores inventory afterward.
The tests disable their view's automatic update and call the controller's queue tick explicitly.
They count recovery calls to verify that review and cancellation do not roll material returns.
The frozen-queue fixture temporarily returns a Shortsword from its own recipe, then restores the recipe definition.
This fixture verifies that newly recovered eligible equipment never extends the confirmed queue.
The suite also checks excessive upgrade estimates and a disabled inventory between queue entries.
The disabled-state fixture restores the original flag before cleanup.
Run `close-bench` before `benchmark-bulk`.
This benchmark consumes ten synthetic equipment items and a complete 100-arrow stack, then restores the copied inventory.
Its report records review time, each queue tick, and total elapsed time including yielded render frames.

Close the dismantling menu before `run-ui-tests`.
UI tests call native prompt events and callbacks. They verify interrupted holds and repeated confirmation without sending physical input.
They also check Unity's horizontal navigation graph between Dismantle and the preceding native bench button.
The currency preview uses a synthetic quote because loaded equipment recipes might contain no wallet materials.
It keeps confirmation disabled and performs no dismantling transaction.

Each native test restores inventory quantities, wallets, and known-item state afterward.
All created items and recovered material changes remain inside the isolated process and copied profile.
The persistence command intentionally retains its synthetic recovery result in the new test save.

For persistence validation:

1. Run `close-bench`, then `save-test` after gameplay allows saving.
2. Wait for the slot recorded in `persistence-expected.json` to appear in the copied `Saved` directory.
3. Run `quit` and restart with the same isolated environment.
4. Run `load-latest`, then `verify-persistence` after the hero loads.

## Limits and cleanup

The harness does not prove controller navigation, visual layout, or compatibility through screenshots alone.
Inspect those behaviors during the isolated session.
The copied profile isolates game saves and preferences; ordinary Unity logs and unrelated mod files can still use their normal locations.
Arbitrary third-party event effects are not fully reversible. Stop testing when the report identifies an inventory rollback failure.

After the game closes, remove the harness DLL from the installed plugins.
Start normal gameplay without `AVALON_DISMANTLING_TEST_PROFILE`.
Keep the artifact reports and production backup until validation finishes.
