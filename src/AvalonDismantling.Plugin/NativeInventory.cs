using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Awaken.TG.Main.Heroes;
using Awaken.TG.Main.Heroes.Items;
using Awaken.TG.Main.Heroes.Items.Attachments;
using Awaken.TG.Main.Heroes.Stats;
using Awaken.TG.Main.Locations.Shops;
using HarmonyLib;

namespace AvalonDismantling.Plugin;

internal static class NativeInventory
{
    private static readonly Random Random = new Random();
    private static int _busy;
    private static bool _patched;
    internal static bool Busy => _busy != 0;
    internal static bool Disabled { get; private set; }

    internal static IReadOnlyDictionary<string, int> Execute(GameCatalog catalog, Quote preview)
    {
        if (Disabled) throw new InvalidOperationException("Dismantling is disabled after an inventory error.");
        // HeroItems has a game-dependent static initializer; patch only after the hero exists.
        if (!_patched)
        {
            var harmony = new Harmony("com.eugenydementev.avalondismantling");
            harmony.CreateClassProcessor(typeof(SuppressArrowEquip)).Patch();
            harmony.CreateClassProcessor(typeof(SuppressFoodEquip)).Patch();
            _patched = true;
        }
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("A dismantling operation is already running.");
        try { return ExecuteLocked(catalog, preview); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private static IReadOnlyDictionary<string, int> ExecuteLocked(GameCatalog catalog, Quote preview)
    {
        // Re-read live recipe definitions and DLC availability at the confirmation boundary.
        catalog = new GameCatalog();
        Quote current = catalog.Quote(preview.Source, preview.Plan.Batches);
        if (current.SourceQuantity != preview.SourceQuantity || current.DisplayLevel != preview.DisplayLevel ||
            current.Plan.Recipe.Id != preview.Plan.Recipe.Id || !current.Plan.PerBatch.SequenceEqual(preview.Plan.PerBatch))
            throw new InvalidOperationException("The item or recipe changed. Select it again before dismantling.");

        Hero hero = Hero.Current;
        HeroItems inventory = hero.HeroItems;
        Item source = current.Source;
        var beforeItems = new HashSet<Item>(inventory.Items);
        var beforeCounts = beforeItems.Where(i => !i.HasElement<CurrencyAsCraftingIngredient>()).ToDictionary(i => i, i => i.Quantity);
        var beforeKnown = new HashSet<string>(inventory.KnownItems);
        float moneyBefore = hero.Wealth.BaseValue;
        float cobwebBefore = hero.Cobweb.BaseValue;
        var created = new HashSet<Item>();
        var delivered = new List<Item>();
        IReadOnlyDictionary<string, int> recovery = current.Plan.Roll(() => Random.Next(50, 101));
        int moneyDelta = 0;
        int cobwebDelta = 0;
        bool committed = false;
        bool commitAttempted = false;
        int remaining = current.SourceQuantity - current.Plan.Consumed;

        try
        {
            foreach (KeyValuePair<string, int> material in recovery)
            {
                ItemTemplate template = catalog.Material(material.Key);
                CurrencyAsCraftingIngredientAttachment currency = template.GetAttachment<CurrencyAsCraftingIngredientAttachment>();
                if (currency != null)
                {
                    if (currency.Type == CurrencyType.Money) moneyDelta = checked(moneyDelta + material.Value);
                    else if (currency.Type == CurrencyType.Cobweb) cobwebDelta = checked(cobwebDelta + material.Value);
                    else throw new InvalidOperationException("Unsupported ingredient currency.");
                    continue;
                }
                int objects = template.CanStack ? 1 : material.Value;
                for (int index = 0; index < objects; index++)
                {
                    int quantity = template.CanStack ? material.Value : 1;
                    var staged = new Item(template, quantity);
                    created.Add(staged);
                    Item added = inventory.Add(staged, allowStacking: false);
                    if (added != null && !beforeItems.Contains(added)) created.Add(added);
                    if (added == null || beforeItems.Contains(added) || added.Template != template || added.Quantity != quantity ||
                        added.IsStolen || added.HasBeenDiscarded || added.IsBeingDiscarded ||
                        added.Inventory != inventory || !inventory.Inventory.Contains(added))
                        throw new InvalidOperationException("An inventory hook rejected or changed a recovered material.");
                    delivered.Add(added);
                }
            }

            ApplyCurrency(hero.Wealth, moneyBefore, moneyDelta);
            ApplyCurrency(hero.Cobweb, cobwebBefore, cobwebDelta);
            foreach (KeyValuePair<Item, int> prior in beforeCounts)
                if (prior.Key.HasBeenDiscarded || prior.Key.Quantity != prior.Value || !inventory.Items.Contains(prior.Key))
                    throw new InvalidOperationException("An inventory hook changed an existing item during delivery.");
            if (inventory.Items.Any(i => !beforeItems.Contains(i) && !delivered.Contains(i)))
                throw new InvalidOperationException("An inventory hook added an unexpected item during delivery.");
            string? eligibility = catalog.Eligibility(source);
            if (eligibility != null) throw new InvalidOperationException(eligibility);

            commitAttempted = true;
            if (remaining == 0)
            {
                // Detach first: Discard callbacks cannot be rolled back after destruction starts.
                inventory.Remove(source, discard: false, announce: false);
                if (source.Inventory != null || inventory.Items.Contains(source))
                    throw new InvalidOperationException("The source item could not be detached.");
                committed = true;
            }
            else
            {
                source.SetQuantity(remaining);
                committed = source.Quantity == remaining && source.Inventory == inventory;
                if (!committed) throw new InvalidOperationException("The source batch could not be consumed.");
            }
        }
        catch (Exception exception)
        {
            // SetQuantity/Remove can throw after applying their changes; inspect the commit boundary.
            committed = committed || (commitAttempted && (remaining == 0
                ? source.Inventory == null && !inventory.Items.Contains(source)
                : !source.HasBeenDiscarded && source.Inventory == inventory && source.Quantity == remaining));
            if (committed)
            {
                Disabled = true;
                Plugin.Log.LogError($"Dismantling committed but an inventory callback failed. Outputs were preserved. Source={source.ID}: {exception}");
            }
            else
            {
                try
                {
                    foreach (Item added in inventory.Items.Where(i => !beforeItems.Contains(i)).ToArray()) created.Add(added);
                    foreach (Item staged in created) Cleanup(inventory, staged, beforeItems);
                    hero.Wealth.SetTo(moneyBefore, runHooks: false);
                    hero.Cobweb.SetTo(cobwebBefore, runHooks: false);
                    foreach (string id in inventory.KnownItems.Where(id => !beforeKnown.Contains(id)).ToArray()) inventory.KnownItems.Remove(id);
                    foreach (KeyValuePair<Item, int> prior in beforeCounts)
                    {
                        if (prior.Key.HasBeenDiscarded || !inventory.Items.Contains(prior.Key))
                            throw new InvalidOperationException("A third-party hook destroyed an existing inventory item.");
                        if (prior.Key.Quantity != prior.Value) prior.Key.SetQuantity(prior.Value);
                    }
                    if (source.Inventory != inventory || !inventory.Inventory.Contains(source))
                        throw new InvalidOperationException("The original item is no longer in its original inventory.");
                }
                catch (Exception rollbackError)
                {
                    Disabled = true;
                    Plugin.Log.LogError($"Dismantling rollback failed. Source={source.ID}; expected quantity={current.SourceQuantity}; recovery={string.Join(",", recovery.Select(p => p.Key + ":" + p.Value))}: {rollbackError}");
                    throw new InvalidOperationException("An inventory hook prevented rollback. Dismantling is disabled; review the game log before saving.", rollbackError);
                }
                throw new InvalidOperationException("Dismantling cancelled; the original item was kept. " + exception.Message, exception);
            }
        }

        try
        {
            if (remaining == 0 && !source.HasBeenDiscarded && !source.IsBeingDiscarded) source.Discard();
            foreach (Item staged in created.Where(i => !delivered.Contains(i))) Cleanup(inventory, staged, beforeItems);
            // Native merging changes the destination before discarding the source and can duplicate on callback errors.
            // shortcut: keep recovered stacks separate until a transactional native merge is available.
            inventory.TriggerChange();
        }
        catch (Exception cleanupError)
        {
            Disabled = true;
            Plugin.Log.LogError("Dismantling completed, but cleanup failed. Restart before another operation: " + cleanupError);
        }
        Plugin.Log.LogInfo($"Dismantled {current.Plan.Consumed} x {source.Template.GUID}; recipe={current.Plan.Recipe.Id}; returned={string.Join(",", recovery.Select(p => p.Key + ":" + p.Value))}");
        return recovery;
    }

    private static void ApplyCurrency(CurrencyStat currency, float before, int amount)
    {
        if (currency.BaseValue != before) throw new InvalidOperationException("An inventory hook changed a wallet balance.");
        if (amount != 0) currency.IncreaseBy(amount);
        if (!float.IsFinite(currency.BaseValue) || (double)currency.BaseValue - before != amount)
            throw new InvalidOperationException("A currency ingredient could not be delivered exactly.");
    }

    private static void Cleanup(HeroItems inventory, Item item, HashSet<Item> beforeItems)
    {
        if (beforeItems.Contains(item) || item.HasBeenDiscarded) return;
        if (item.Inventory == inventory) inventory.Remove(item, discard: false, announce: false);
        if (item.IsInitialized && !item.IsBeingDiscarded) item.Discard();
    }

    [HarmonyPatch(typeof(HeroItems), "EquipPickedArrows")]
    private static class SuppressArrowEquip
    {
        [HarmonyPrefix] private static bool Prefix() => !Busy;
    }

    [HarmonyPatch(typeof(HeroItems), "EquipPickedFood")]
    private static class SuppressFoodEquip
    {
        [HarmonyPrefix] private static bool Prefix() => !Busy;
    }
}
