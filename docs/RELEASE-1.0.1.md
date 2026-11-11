# Avalon Dismantling 1.0.1

Fixes the multi-second pause after dismantling an item.

The mod rebuilt every native inventory row after each transaction.
Version 1.0.1 uses a soft refresh and preserves unchanged rows.
It rebuilds the list only when new equipment needs a row.
It also revalidates only the selected live recipe instead of rescanning the complete catalog.

On a copied Slot 72 save, repeated processing fell from 4.3–4.7 seconds to 0.21–0.27 seconds.
The inventory refresh fell from 4.2–4.3 seconds to 12–27 milliseconds.
These measurements exclude the 0.65-second confirmation hold. Recovery rules and protection checks remain the same.

Passed 121 calculation checks, 22 native inventory cases, five UI checks, and the normal crafting menu check.
Physical controller and save/reload tests were not repeated for this update.

Close the game and replace both DLLs in `BepInEx/plugins/AvalonDismantling` with the release ZIP contents.
Requires game 1.30.012 Mono and BepInEx 5.4.23.6 x64.
See [validation notes](https://github.com/eugeny-dementev/avalon-dismantling/blob/main/docs/VALIDATION.md) for details.
