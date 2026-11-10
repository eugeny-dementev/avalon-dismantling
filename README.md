# Avalon Dismantling

Recover crafting materials from equipment at crafting benches in **Tainted Grail: The Fall of Avalon**.

Version 1.0.0 targets the **1.30.012 Mono build** with **BepInEx 5.4.23.6 x64**. IL2CPP builds are unsupported.

Downloads: [GitHub releases](https://github.com/eugeny-dementev/avalon-dismantling/releases) · [Nexus Mods](https://www.nexusmods.com/taintedgrailthefallofavalon/mods/395)

## Use

1. Open a crafting bench.
2. Select **Dismantle**.
3. Select an item and inspect the material ranges.
4. Hold the displayed confirmation control to dismantle one item or ammunition batch.

Dismantling costs zero gold. It grants no crafting XP and triggers no crafting rewards.

## Recovery rules

- Recover a random 50–100% of the recipe's base ingredient quantities, ignoring crafting discounts.
- Round every ingredient upward. Single-unit ingredients always return one.
- Use one random percentage per equipment item or ammunition batch.
- Consume complete ammunition batches. A ten-arrow recipe requires ten arrows.
- Include estimated upgrade ingredients for the displayed positive `+level`.
- Exclude the separate New Game Plus stat bonus, weight-reduction expenses, and service-currency charges.
- Exclude gold recipe fees from recovered materials. Gold stays unchanged.
- Return clean materials from stolen equipment.
- Preserve direct ingredients instead of recursively converting them to raw resources.

Upgrade returns are estimates. Crafted quality levels also count, even when the player never paid for those upgrades.
These recovery rules can permit material gains with crafting discounts or bonus outputs.

## Similar recipes

Exact crafting recipes take priority. Otherwise, the mod automatically selects the nearest compatible recipe.
The preview identifies the comparison item. Its complete recipe can include unusual ingredients, such as frost shards.

Melee matches require the same family and handedness. Armor matches require the same slot and weight class.
Jewelry matches require the same slot. Bows, shields, arrows, and throwables remain separate categories.

Matching prefers the nearest tier, then the lower tier at equal distances, shared abstract types, and matching weight class.
Recipe GUIDs resolve remaining ties. Missing source tiers use the lowest available compatible tier.
Duplicate exact recipes use the lowest total ingredient count per output, followed by recipe GUID.

Items without a compatible recipe remain unavailable. Unlearned recipes can still provide recovery.
Hidden, story-dependent, protected, unresolved, and abstract-ingredient recipes are excluded. Unowned DLC ingredients are excluded.

## Protected items

Quest items, locked items, favourites, equipped items, loadout items, and non-droppable items cannot be dismantled.
Remove attached relics first. Only carried inventory is used.

## Install or remove

Close the game before changing installed mods. Back up your saves before installing a new mod.

Extract the release ZIP into the game directory. The resulting files must be:

```text
BepInEx/plugins/AvalonDismantling/AvalonDismantling.Plugin.dll
BepInEx/plugins/AvalonDismantling/AvalonDismantling.Core.dll
```

To remove the mod, close the game and remove that plugin folder.
Recovered materials remain ordinary inventory items. No custom save data or migration is required.

## Compatibility and testing

The mod adds a bench button and uses native inventory operations. It does not replace game bundles or modify crafting-cost methods.
No Crafting Gold remains responsible for normal crafting and upgrading fees.

See [validation notes](docs/VALIDATION.md) for completed checks and remaining limitations.
Inventory transactions validate delivery and attempt rollback before consuming the source.
Recovered materials can appear as separate stacks. This avoids unsafe merging after the source item has been consumed.
Arbitrary third-party callbacks can have irreversible side effects. The mod disables further transactions after an unrecoverable error.

## Build

Install a .NET 10 SDK and provide your own supported game installation:

```powershell
./scripts/build.ps1 -GameRoot 'C:/Games/Fall of Avalon' -DotnetPath dotnet
./scripts/package.ps1
```

The independent calculation tests also run without game files:

```text
dotnet run --project tests/AvalonDismantling.Tests.csproj -c Release
```

Game assemblies, extracted assets, test profiles, and the runtime harness are not included in release packages.
Original mod code uses the MIT license. Game assets remain owned by their respective owners.

Credits: Questline and Awaken Realms; BepInEx and Harmony contributors.

AI disclosure: OpenAI Codex generated the code, build scripts, tests, and documentation from the author's requirements.
Published gameplay screenshots show the running game.
