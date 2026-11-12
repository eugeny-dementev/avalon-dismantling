# Changelog

## 1.1.0

- Show only items with valid recovery previews and no protection restrictions.
- Add dynamic item-type and native rarity filters while preserving existing sorting controls.
- Reuse the native inventory category icons and tab-row structure.
- Add reviewed bulk dismantling through the native Craft Many action, enabled only with two specific filters.
- Consume complete ammunition batches, retain remainders, and freeze the reviewed item queue.
- Add progress, cancellation between entries, live revalidation, and actual recovery summaries.
- Preserve incremental inventory updates and coalesce eligibility refreshes.

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
