# Avalon Dismantling 1.1.0

Adds item-type and native rarity filters, hides unavailable items, and adds reviewed bulk dismantling.

- Filter by All, Weapons, Armor, or Other. Other includes jewelry.
- Reuse the game's inventory category icons and tab-row structure.
- Choose from rarity tiers present within the selected type. Existing F sorting remains available.
- Equipped, loadout-assigned, favourite, locked, quest, relic-bearing, and otherwise unavailable items stay hidden.
- G / Dismantle all appears only when both filters have specific selections.
- Review exact items and combined material ranges before holding confirmation.
- Consume all matching equipment and complete ammunition batches; retain batch remainders.
- Cancel the review without changes, or stop processing after the current transaction. Completed dismantles remain completed.
- Newly recovered items never join the reviewed queue.

Recovery rules and protection checks remain in place. Dismantling costs zero gold and grants no crafting XP or rewards.
Empty filter choices disappear and unavailable selections reset to All. Each menu opening starts with both filters at All.

Close the game and replace both DLLs in `BepInEx/plugins/AvalonDismantling` using the release ZIP.
Requires Tainted Grail: The Fall of Avalon 1.30.012 Mono and BepInEx 5.4.23.6 x64.

Validation passed: 152 independent checks and 38 runtime cases, plus isolated save/reload and normal crafting regression checks.
The bulk benchmark processed 11 inventory entries in 2.34 seconds, excluding confirmation holds.
Physical controller input remains untested. Native controller action bindings and navigation were checked.
See [validation notes](https://github.com/eugeny-dementev/avalon-dismantling/blob/main/docs/VALIDATION.md) for testing and limitations.
