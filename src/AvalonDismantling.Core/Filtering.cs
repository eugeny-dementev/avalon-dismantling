using System;
using System.Collections.Generic;
using System.Linq;

namespace AvalonDismantling.Core;

public sealed class FilterItem
{
    public string Type { get; }
    public string Rarity { get; }
    public FilterItem(string type, string rarity)
    {
        if (!DismantleFilters.Types.Contains(type) || type == "All") throw new ArgumentException("Invalid item type.", nameof(type));
        Type = type;
        Rarity = string.IsNullOrEmpty(rarity) ? throw new ArgumentException("Missing rarity.", nameof(rarity)) : rarity;
    }
}

public static class DismantleFilters
{
    public static readonly IReadOnlyList<string> Types = new[] { "All", "Weapons", "Armor", "Other" };
    public static bool Matches(FilterItem item, string type, string? rarity) =>
        (type == "All" || item.Type == type) && (rarity == null || item.Rarity == rarity);
    public static string[] AvailableTypes(IEnumerable<FilterItem> items)
    {
        var present = new HashSet<string>(items.Select(item => item.Type), StringComparer.Ordinal);
        return Types.Where(type => type == "All" || present.Contains(type)).ToArray();
    }
    public static string[] AvailableRarities(IEnumerable<FilterItem> items, string type) => items
        .Where(item => Matches(item, type, null)).Select(item => item.Rarity).Distinct(StringComparer.Ordinal).ToArray();
    public static (string Type, string? Rarity) Normalize(IEnumerable<FilterItem> items, string type, string? rarity)
    {
        FilterItem[] current = items.ToArray();
        if (!AvailableTypes(current).Contains(type)) type = "All";
        if (rarity != null && !AvailableRarities(current, type).Contains(rarity)) rarity = null;
        return (type, rarity);
    }
    public static bool BulkAvailable(string type, string? rarity, int count) => type != "All" && rarity != null && count > 0;
    public static int WholeBatches(int quantity, int batchSize)
    {
        if (quantity < 0 || batchSize <= 0) throw new ArgumentOutOfRangeException();
        return quantity / batchSize;
    }
}

public sealed class BulkRecovery
{
    public IReadOnlyDictionary<string, int> Minimum { get; }
    public IReadOnlyDictionary<string, int> Maximum { get; }
    public int Consumed { get; }
    public int Batches { get; }
    public BulkRecovery(IEnumerable<RecoveryPlan> plans)
    {
        RecoveryPlan[] entries = plans.ToArray();
        if (entries.Length == 0) throw new ArgumentException("The bulk plan is empty.", nameof(plans));
        Minimum = Recovery.Merge(entries.SelectMany(plan => plan.Minimum));
        Maximum = Recovery.Merge(entries.SelectMany(plan => plan.Maximum));
        foreach (RecoveryPlan plan in entries)
        {
            Consumed = checked(Consumed + plan.Consumed);
            Batches = checked(Batches + plan.Batches);
        }
    }
}
