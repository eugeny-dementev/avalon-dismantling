# Validation

Target: Tainted Grail: The Fall of Avalon 1.30.012 Mono, BepInEx 5.4.23.6, Unity 6000.0.41f1.

## Completed

### 1.1.0 filters and bulk dismantling

Tested on November 12, 2026, using an isolated copy of the latest bench save, Slot 76.
The test process uses separate save storage and cache paths. The release contains no test harness.

- Independent calculation, matching, filtering, and aggregation checks: 152 passed.
- Native inventory cases: 22 passed, zero failed, zero skipped.
- Bulk and eligibility cases: 11 passed, zero failed.
- Native prompt and menu cases: five passed, zero failed.
- Plugin and runtime-harness compilation: zero warnings and zero errors.

Bulk cases cover every protection restriction, invalid quotes, type/rarity intersections, dynamic choices, and the two-filter Craft Many gate.
They cover review cancellation, repeated confirmation, recipe changes, mid-queue protection changes, partial cancellation, rejected delivery, rollback, and disabled inventory.
Complete ammunition batches preserve remainders. Recovery outputs never join the frozen queue.
The tests inspect native inventory changes, wallet balances, recovery calls, and pending row identities.

An isolated save/reload preserves recovered materials and both currency balances.
Normal handcrafting opens and selects a learned recipe after Dismantle closes, without changing inventory or gold.
Visual checks cover 1920 × 1080 and 3840 × 2160 rendering.
Mouse category and rarity selection, keyboard category navigation, F sorting, G review, scrollbar dragging, and Escape cancellation work.
Eligible purple equipment and jewelry remain visible under their native quality filter.
Choosing All removes G, and pressing G then leaves the ordinary item list open.
Confirmation holds were checked through native prompt events and callbacks; physical held-key duration was not tested.
Normal startup reaches the main menu after removing the temporary harness. The release startup log contains no Avalon Dismantling errors.
All 63 original profile files match the backup after testing. The original saves were never overwritten.

| Measurement | 1.0.1 baseline | 1.1.0 |
| --- | --- | --- |
| Single confirmation processing, three samples | 212–273 ms | 336–446 ms |
| Bulk review, 11 entries | Not available | 55 ms |
| Bulk processing, 11 entries / 110 units | Not available | 2341 ms |
| Individual bulk transaction ticks | Not available | 159–245 ms |

Timings exclude the 0.65-second confirmation hold. Bulk elapsed time includes yielded render frames and final refresh.
The baseline uses the earlier 1.0.1 benchmark on this machine. Inventory contents and runtime load can affect these measurements.
The new single-item path also refreshes eligibility and filter choices. It retains the incremental row update from 1.0.1.
The bulk benchmark consumes ten synthetic equipment items and 100 arrows in 11 inventory entries.
It checks unchanged row identities throughout processing and avoids rebuilding the full list between transactions.

### 1.0.1 performance regression check

Tested against a copy of the latest bench save, Slot 72, with the installed mod collection.
Three synthetic sword confirmations used the carried inventory and native Dismantle UI.
Timings exclude the unchanged 0.65-second confirmation hold.

| Measurement | Before | 1.0.1 |
| --- | --- | --- |
| Repeated confirmation processing | 4343–4709 ms | 212–273 ms |
| Inventory refresh | 4212–4308 ms | 12–27 ms |

The full native list rebuild caused the pause. Soft refresh preserves unchanged rows and removes consumed rows.
The benchmark checks both row identity preservation and removal of the consumed item.
New equipment still triggers a full rebuild so recovered equipment can appear in the list.
Confirmation revalidates the selected live recipe without scanning every loaded recipe again.

All 121 calculation checks, 22 native inventory cases, and five UI checks passed for 1.0.1.
The added inventory case changes a raw recipe quantity and verifies rejection without inventory changes.
Normal crafting opens after Dismantle closes. Production saves are excluded from the test profile.
Save/reload and physical controller input were not repeated for this performance update.

- Calculation tests: 121 assertions passed.
- Plugin compilation: zero warnings and zero errors.
- Static inspection confirmed the retained layout and native bench entry point.
- Game startup and copied-save loading succeeded with the installed mod collection.
- Native inventory tests: 21 passed, zero failed, zero skipped.
- Native prompt tests: interrupted holds preserve inventory; completed callbacks consume once and preserve gold.
- Repeated confirmation callbacks produce no additional materials.
- Protected selection and three menu reopenings preserve inventory.
- A synthetic Ethereal Cobweb preview preserves both wallets before and after disposal.
- Isolated save/reload preserves recovered materials and both wallet balances.
- Native bench navigation links reach Dismantle and return to the preceding button.
- Normal handcrafting opens and selects a learned recipe after Dismantle closes.
- The handcrafting regression check preserves inventory and gold; it does not craft another item.
- Final rendering was inspected at 1920 × 1080 and 3840 × 2160.
- Mouse selection and Escape cancellation work in the final menu.
- Normal startup reaches the title screen after removing the temporary runtime harness.
- The final startup and UI session contain no Avalon Dismantling errors.

Native inventory cases include exact and automatic recipes, full ammunition batches, positive and negative upgrades, and separate New Game Plus bonuses.
They cover clean stolen-item returns, all protected-item restrictions, rejected additions, replacements, callback exceptions, rollback, and reentry.

Calculation tests cover recovery endpoints, upward rounding, merged ingredients, duplicate recipes, missing tiers, and matching boundaries.

## Tested mod environment

The runtime session loaded the complete installed collection, including these mods:

| Mod | Installed version |
| --- | --- |
| Better UI | 1.0.4 |
| No Crafting Gold | 1.0.0 |
| Four Rings | 1.0.3, local slot compatibility adjustment |
| Soul Cube Slot | 1.0.0 |

These results establish compatibility for the tested operations and configuration. They do not prove every feature combination.

## Limits

Physical controller input remains untested. Native navigation and prompt checks do not replace hardware testing.
Other game builds, unowned DLC content, ultrawide displays, and arbitrary third-party inventory callbacks remain unverified.
The tests use a copied local profile. They do not modify production saves.
No custom save data or runtime harness is included in the release ZIP.
