using System;
using System.Collections.Generic;
using System.Linq;

namespace AvalonDismantling.Core;

public sealed class ItemProfile
{
    public string Id { get; }
    public string Family { get; }
    public int? Tier { get; }
    public string WeightClass { get; }
    public HashSet<string> Abstracts { get; }

    public ItemProfile(string id, string family, int? tier, string weightClass, IEnumerable<string> abstracts)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Missing item identity.", nameof(id));
        Id = id;
        Family = family ?? throw new ArgumentNullException(nameof(family));
        Tier = tier;
        WeightClass = weightClass ?? "";
        Abstracts = new HashSet<string>(abstracts, StringComparer.Ordinal);
    }
}

public sealed class Recipe
{
    public string Id { get; }
    public ItemProfile Outcome { get; }
    public int Quantity { get; }
    public IReadOnlyDictionary<string, int> Ingredients { get; }
    public decimal UnitsPerOutput { get; }

    public Recipe(string id, ItemProfile outcome, int quantity, IEnumerable<KeyValuePair<string, int>> ingredients)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Missing recipe identity.", nameof(id));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        Id = id;
        Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
        Quantity = quantity;
        Ingredients = Recovery.Merge(ingredients);
        if (Ingredients.Count == 0) throw new ArgumentException("Recipe has no ingredients.", nameof(ingredients));
        UnitsPerOutput = Ingredients.Values.Sum(value => (decimal)value) / quantity;
    }
}

public static class RecipeMatcher
{
    public static Recipe? Match(ItemProfile source, IEnumerable<Recipe> recipes)
    {
        Recipe[] candidates = recipes.ToArray();
        Recipe? exact = candidates.Where(r => r.Outcome.Id == source.Id)
            .OrderBy(r => r.UnitsPerOutput).ThenBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault();
        if (exact != null) return exact;
        if (source.Family.Length == 0) return null;

        return candidates.Where(r => r.Outcome.Family == source.Family)
            .OrderBy(r => TierDistance(source.Tier, r.Outcome.Tier))
            .ThenBy(r => r.Outcome.Tier ?? int.MaxValue)
            .ThenByDescending(r => r.Outcome.Abstracts.Count(source.Abstracts.Contains))
            .ThenByDescending(r => source.WeightClass.Length > 0 && r.Outcome.WeightClass == source.WeightClass)
            .ThenBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    private static long TierDistance(int? source, int? candidate) => candidate.HasValue
        ? (source.HasValue ? Math.Abs((long)source.Value - candidate.Value) : candidate.Value)
        : long.MaxValue;
}

public sealed class RecoveryPlan
{
    public Recipe Recipe { get; }
    public int Batches { get; }
    public int Consumed { get; }
    public IReadOnlyDictionary<string, int> PerBatch { get; }
    public IReadOnlyDictionary<string, int> Upgrades { get; }
    public IReadOnlyDictionary<string, int> Minimum { get; }
    public IReadOnlyDictionary<string, int> Maximum { get; }

    public RecoveryPlan(Recipe recipe, int batches, IEnumerable<KeyValuePair<string, int>> upgrades)
    {
        if (batches <= 0) throw new ArgumentOutOfRangeException(nameof(batches));
        Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        Batches = batches;
        Consumed = checked(recipe.Quantity * batches);
        Upgrades = Recovery.Merge(upgrades);
        PerBatch = Recovery.Merge(recipe.Ingredients.Concat(Upgrades));
        Minimum = PerBatch.ToDictionary(p => p.Key, p => checked(Recovery.Scale(p.Value, 50) * batches), StringComparer.Ordinal);
        Maximum = PerBatch.ToDictionary(p => p.Key, p => checked(p.Value * batches), StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, int> Roll(Func<int> nextPercentage)
    {
        var result = PerBatch.ToDictionary(p => p.Key, _ => 0, StringComparer.Ordinal);
        for (int batch = 0; batch < Batches; batch++)
        {
            int percentage = nextPercentage();
            if (percentage < 50 || percentage > 100) throw new ArgumentOutOfRangeException(nameof(nextPercentage));
            foreach (KeyValuePair<string, int> ingredient in PerBatch)
                result[ingredient.Key] = checked(result[ingredient.Key] + Recovery.Scale(ingredient.Value, percentage));
        }
        return result;
    }
}

public static class Recovery
{
    public static int Scale(int count, int percentage)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (percentage < 50 || percentage > 100) throw new ArgumentOutOfRangeException(nameof(percentage));
        return checked((int)(((long)count * percentage + 99) / 100));
    }

    public static IReadOnlyDictionary<string, int> Merge(IEnumerable<KeyValuePair<string, int>> values)
    {
        var result = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, int> value in values)
        {
            if (string.IsNullOrWhiteSpace(value.Key) || value.Value <= 0)
                throw new ArgumentException("Ingredients require an identity and a positive quantity.", nameof(values));
            result.TryGetValue(value.Key, out int existing);
            result[value.Key] = checked(existing + value.Value);
        }
        return result;
    }
}
