# Avalon Dismantling 1.0.0

Adds **Dismantle** to crafting benches in Tainted Grail: The Fall of Avalon **1.30.012 Mono**.

- Recover 50–100% of direct recipe ingredients, with upward rounding.
- Use exact recipes or automatic compatible matches, shown before confirmation.
- Include estimated materials for positive upgrade levels.
- Consume complete ammunition batches and protect equipped, favourite, quest, locked, and relic-bearing items.
- Charge no gold and grant no crafting XP or rewards.

Single-unit ingredients always return one. Upgrade materials are estimates, including levels granted by crafting quality.
Crafting discounts and bonus outputs can permit material gains. Recovered materials can appear as separate stacks.

Requires **BepInEx 5.4.23.6 x64**. Close the game, back up saves, then extract the ZIP into the game directory.
Remove `BepInEx/plugins/AvalonDismantling` to uninstall. Recovered materials remain ordinary items.

Validation includes 121 calculation checks, 21 native inventory cases, five UI checks, and isolated save/reload.
The installed collection includes Better UI, No Crafting Gold, Four Rings, and Soul Cube Slot.
Physical controller input and other game builds remain untested.

See [validation notes](https://github.com/eugeny-dementev/avalon-dismantling/blob/main/docs/VALIDATION.md) for the complete scope.
Verify the ZIP against `SHA256SUMS.txt`. The archive contains only the mod DLLs, documentation, and MIT license.
