using System;
using System.Collections.Generic;
using System.Linq;
using AvalonDismantling.Core;

int passed = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAILED: " + name);
    passed++;
}
void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); } catch (T) { passed++; return; }
    throw new Exception("FAILED: " + name);
}
ItemProfile Profile(string id, string family = "melee:blunt:2", int? tier = 4, string weight = "heavy", params string[] bases)
    => new ItemProfile(id, family, tier, weight, bases);
KeyValuePair<string, int> Material(string id, int count) => new KeyValuePair<string, int>(id, count);
Recipe Recipe(string id, ItemProfile output, int count = 1, params KeyValuePair<string, int>[] materials)
    => new Recipe(id, output, count, materials.Length == 0 ? new[] { Material("iron", 3) } : materials);

var source = Profile("hammer", bases: new[] { "blunt", "wide-grip" });
var wrongFamily = Recipe("wrong", Profile("sword", "melee:sword:2"));
var lower = Recipe("lower", Profile("lower", tier: 3));
var higher = Recipe("higher", Profile("higher", tier: 5));
var peer = Recipe("peer", Profile("peer", bases: new[] { "blunt", "wide-grip" }));
Check(RecipeMatcher.Match(source, new[] { wrongFamily, lower, higher, peer }) == peer, "same tier and family");
Check(RecipeMatcher.Match(source, new[] { higher, lower }) == lower, "lower tier wins equal distance");
Check(RecipeMatcher.Match(source, new[] { wrongFamily }) == null, "no cross-family match");
Check(RecipeMatcher.Match(Profile("unknown", tier: null), new[] { higher, lower }) == lower, "unknown source tier uses lowest");
Check(RecipeMatcher.Match(Profile("unknown", family: ""), new[] { peer }) == null, "unknown family disabled");
var exact = Recipe("exact", source, 1, Material("iron", 8));
Check(RecipeMatcher.Match(source, new[] { peer, exact }) == exact, "exact wins over cheaper similar");
var exactCheap = Recipe("cheap", source, 10, Material("iron", 5));
Check(RecipeMatcher.Match(source, new[] { exact, exactCheap }) == exactCheap, "duplicates normalized by output count");
var tieA = Recipe("a", source);
var tieZ = Recipe("z", source);
Check(RecipeMatcher.Match(source, new[] { tieZ, tieA }) == tieA, "GUID tie is stable");
Check(RecipeMatcher.Match(source, new[] { Recipe("a", Profile("less-shared")), peer }) == peer, "shared abstracts win tie");

var shortsword = Recipe("shortsword", Profile("shortsword", "melee:sword:1"), 1,
    Material("iron", 3), Material("leather", 1), Material("wood", 2));
var plan = new RecoveryPlan(shortsword, 1, Array.Empty<KeyValuePair<string, int>>());
Check(plan.Minimum["iron"] == 2 && plan.Minimum["leather"] == 1 && plan.Minimum["wood"] == 1, "50 percent rounds upward");
Check(plan.Roll(() => 100).SequenceEqual(plan.Maximum), "100 percent returns full recipe");
Check(plan.Roll(() => 50).SequenceEqual(plan.Minimum), "50 percent equals preview minimum");
var upgraded = new RecoveryPlan(shortsword, 1, new[] { Material("iron", 2), Material("stone", 1) });
Check(upgraded.PerBatch["iron"] == 5 && upgraded.Minimum["iron"] == 3, "merge upgrades before rounding");
Check(upgraded.Upgrades["iron"] == 2 && upgraded.Upgrades["stone"] == 1, "upgrade preview retained");
var arrows = Recipe("arrows", Profile("arrows", "arrow"), 10, Material("copper", 1), Material("wood", 1), Material("feather", 1));
var arrowPlan = new RecoveryPlan(arrows, 2, Array.Empty<KeyValuePair<string, int>>());
int rolls = 0;
var arrowResult = arrowPlan.Roll(() => { rolls++; return 50; });
Check(arrowPlan.Consumed == 20 && arrowResult["copper"] == 2 && rolls == 2, "one roll per ammunition batch");
for (int percentage = 50; percentage <= 100; percentage++)
{
    Check(Recovery.Scale(1, percentage) == 1, "single-unit return");
    Check(Recovery.Scale(int.MaxValue, percentage) > 0, "large counts do not overflow multiplication");
}
Throws<ArgumentOutOfRangeException>(() => new RecoveryPlan(shortsword, 0, Array.Empty<KeyValuePair<string, int>>()), "zero batches rejected");
Throws<ArgumentException>(() => Recipe("invalid", source, 1, Material("iron", -1)), "negative ingredients rejected");
Throws<ArgumentOutOfRangeException>(() => plan.Roll(() => 49), "invalid random draw rejected");
Throws<OverflowException>(() => new RecoveryPlan(shortsword, int.MaxValue, Array.Empty<KeyValuePair<string, int>>()), "overflow rejected");
var filterItems = new[] { new FilterItem("Weapons", "Normal"), new FilterItem("Weapons", "Story"), new FilterItem("Other", "Magic") };
Check(DismantleFilters.AvailableTypes(filterItems).SequenceEqual(new[] { "All", "Weapons", "Other" }), "only populated type filters");
Check(DismantleFilters.AvailableRarities(filterItems, "Weapons").SequenceEqual(new[] { "Normal", "Story" }), "rarities constrained by type");
Check(DismantleFilters.Matches(filterItems[0], "Weapons", "Normal"), "both filters match");
Check(!DismantleFilters.Matches(filterItems[1], "Weapons", "Normal"), "rarity excludes a different quality");
Check(!DismantleFilters.Matches(filterItems[2], "Weapons", null), "type excludes jewelry");
Check(DismantleFilters.Matches(filterItems[2], "All", null), "all filters include eligible candidates");
Check(DismantleFilters.Normalize(filterItems, "Armor", "Story") == ("All", "Story"), "reset empty type before rarity");
Check(DismantleFilters.Normalize(filterItems, "Other", "Story") == ("Other", null), "reset missing rarity");
Check(DismantleFilters.Normalize(Array.Empty<FilterItem>(), "Weapons", "Normal") == ("All", null), "empty inventory resets both");
Check(DismantleFilters.AvailableTypes(Array.Empty<FilterItem>()).SequenceEqual(new[] { "All" }), "all remains available for empty inventory");
foreach (string type in new[] { "All", "Weapons" })
foreach (string? rarity in new string?[] { null, "Normal" })
    Check(DismantleFilters.BulkAvailable(type, rarity, 1) == (type != "All" && rarity != null), "bulk requires both specific filters");
Check(!DismantleFilters.BulkAvailable("Weapons", "Normal", 0), "empty result disables bulk");
Check(DismantleFilters.WholeBatches(25, 10) == 2 && DismantleFilters.WholeBatches(9, 10) == 0, "whole batches leave remainders");
Throws<ArgumentOutOfRangeException>(() => DismantleFilters.WholeBatches(10, 0), "zero batch size rejected");
Throws<ArgumentException>(() => new FilterItem("All", "Normal"), "candidate needs concrete type");
var bulk = new BulkRecovery(new[] { plan, arrowPlan });
Check(bulk.Consumed == plan.Consumed + arrowPlan.Consumed && bulk.Batches == plan.Batches + arrowPlan.Batches, "bulk quantities aggregate");
foreach (string ingredient in plan.Maximum.Keys.Union(arrowPlan.Maximum.Keys))
{
    int first = plan.Minimum.TryGetValue(ingredient, out int firstMinimum) ? firstMinimum : 0;
    int second = arrowPlan.Minimum.TryGetValue(ingredient, out int secondMinimum) ? secondMinimum : 0;
    Check(bulk.Minimum[ingredient] == first + second, "bulk minimum preserves per-entry rounding");
    int maxFirst = plan.Maximum.TryGetValue(ingredient, out int a) ? a : 0;
    int maxSecond = arrowPlan.Maximum.TryGetValue(ingredient, out int b) ? b : 0;
    Check(bulk.Maximum[ingredient] == maxFirst + maxSecond, "bulk maximum merges materials");
}
Throws<ArgumentException>(() => new BulkRecovery(Array.Empty<RecoveryPlan>()), "empty bulk plan rejected");
var huge = new RecoveryPlan(Recipe("huge", source, 1, Material("iron", int.MaxValue)), 1, Array.Empty<KeyValuePair<string, int>>());
Throws<OverflowException>(() => new BulkRecovery(new[] { huge, huge }), "bulk ranges reject overflow before rolling");
Console.WriteLine($"PASS: {passed} recovery, matching, filtering, and bulk checks.");
