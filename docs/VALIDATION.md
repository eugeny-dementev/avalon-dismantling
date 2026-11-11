# Validation

Target: Tainted Grail: The Fall of Avalon 1.30.012 Mono, BepInEx 5.4.23.6, Unity 6000.0.41f1.

## Completed

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
