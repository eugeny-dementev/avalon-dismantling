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
Console.WriteLine($"PASS: {passed} recovery and matching checks.");
