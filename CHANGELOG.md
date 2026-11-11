# Changelog

## 1.0.1

- Replace the full inventory rebuild after dismantling with a native soft refresh.
- Preserve unchanged inventory rows and rebuild only when new equipment appears.
- Revalidate the selected live recipe instead of rebuilding the entire recipe catalog for each item.
- Log transaction, result, and refresh timings for performance diagnosis.
- Keep recovery rules, protection checks, and rollback behavior unchanged.

## 1.0.0

- Add a native Dismantle action to crafting benches.
- Recover 50–100% of base recipe materials with upward rounding.
- Estimate upgrade-material returns from the displayed positive item level.
- Match unsupported equipment to compatible crafting recipes automatically.
- Protect quest, favourite, equipped, locked, loadout, and relic-bearing items.
- Return clean materials without charging gold or granting crafting rewards.
