using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using AvalonDismantling.Core;
using Awaken.TG.Main.Crafting.HandCrafting;
using Awaken.TG.Main.Crafting.Recipes;
using Awaken.TG.Main.General.Configs;
using Awaken.TG.Main.Heroes;
using Awaken.TG.Main.Heroes.Items;
using Awaken.TG.Main.Heroes.Items.Attachments;
using Awaken.TG.Main.Locations.Shops;
using Awaken.TG.Main.Scenes.SceneConstructors;
using Awaken.TG.Main.SocialServices;
using Awaken.TG.Main.Templates;
using Awaken.TG.MVC;
using HarmonyLib;

namespace AvalonDismantling.Plugin;

internal sealed class Quote
{
    public Item Source { get; }
    public RecoveryPlan Plan { get; }
    public int SourceQuantity { get; }
    public int DisplayLevel { get; }
    public string MatchedName { get; }
    public bool IsExact => Source.Template.GUID == Plan.Recipe.Outcome.Id;

    public Quote(Item source, RecoveryPlan plan, string matchedName)
    {
        Source = source;
        Plan = plan;
        SourceQuantity = source.Quantity;
        DisplayLevel = source.ModifiedLevelWithoutNewGamePlus;
        MatchedName = matchedName;
    }
}

internal sealed class GameCatalog
{
    private static readonly FieldInfo RawCount = RequireField(typeof(Ingredient), "count", typeof(int));
    private static readonly FieldInfo RawIngredients = RequireField(typeof(BaseRecipe), "ingredients", typeof(Ingredient[]));
    private readonly List<Recipe> _recipes = new List<Recipe>();
    private readonly Dictionary<string, ItemTemplate> _materials = new Dictionary<string, ItemTemplate>(StringComparer.Ordinal);
    private readonly Dictionary<string, ItemTemplate> _outcomes = new Dictionary<string, ItemTemplate>(StringComparer.Ordinal);
    private readonly Dictionary<string, Recipe?> _matches = new Dictionary<string, Recipe?>(StringComparer.Ordinal);

    public GameCatalog()
    {
        TemplatesProvider provider = World.Services.Get<TemplatesProvider>();
        if (!provider.AllLoaded) throw new InvalidOperationException("Item templates are still loading. Reopen the bench shortly.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (HandcraftingTemplate station in provider.GetAllOfType<HandcraftingTemplate>(TemplateTypeFlag.Regular))
        foreach (TemplateReference reference in station.recipes)
        {
            try
            {
                BaseRecipe recipe = reference.Get<BaseRecipe>();
                if (recipe == null || !seen.Add(recipe.GUID) || recipe.IsHidden ||
                    !string.IsNullOrEmpty(recipe.StoryOnCreation.GUID) || recipe.Quantity <= 0) continue;
                ItemTemplate outcome = recipe.Outcome;
                if (!IsSupported(outcome) || !AllowedTemplate(outcome)) continue;
                Ingredient[] ingredients = ((Ingredient[])RawIngredients.GetValue(recipe))?
                    .Where(i => i == null || !IsGoldFee(i.Template)).ToArray()!;
                if (ingredients == null || ingredients.Length == 0 || ingredients.Any(i => i == null || !AllowedMaterial(i.Template))) continue;
                var counts = ingredients.Select(i => new KeyValuePair<string, int>(i.Template.GUID, (int)RawCount.GetValue(i))).ToArray();
                var entry = new Recipe(recipe.GUID, Profile(outcome), recipe.Quantity, counts);
                _recipes.Add(entry);
                _outcomes[outcome.GUID] = outcome;
                foreach (Ingredient ingredient in ingredients) _materials[ingredient.Template.GUID] = ingredient.Template;
            }
            catch (Exception exception)
            {
                Plugin.Log.LogWarning($"Skipped unavailable dismantling recipe {reference.GUID}: {exception.Message}");
            }
        }
        if (_recipes.Count == 0) throw new InvalidOperationException("No eligible handcrafting recipes were loaded.");
        Plugin.Log.LogInfo($"Dismantling catalog: {_recipes.Count} eligible recipes, {_outcomes.Count} exact output templates.");
    }

    public ItemTemplate Material(string guid) => _materials[guid];

    public static bool IsSupported(ItemTemplate template) => template != null && !template.IsAbstract &&
        !template.IsMagic && !template.IsSoulCube && !template.IsSpectralWeapon && !template.IsDefaultFists &&
        (template.IsArmor || template.IsMelee || template.IsShield || template.IsJewelry || template.IsArrow ||
         template.IsThrowable || template.IsShortBow || template.IsMediumBow || template.IsHeavyBow);

    public string? Eligibility(Item item)
    {
        if (NativeInventory.Disabled) return "Dismantling stopped after an inventory error. Restart the game and review the log.";
        if (item == null || item.HasBeenDiscarded || item.IsBeingDiscarded) return "Select an item.";
        Hero hero = Hero.Current;
        if (hero == null || item.Inventory != hero.HeroItems || !hero.HeroItems.Inventory.Contains(item) || item.IsStashed)
            return "Move this item into your carried inventory.";
        if (!IsSupported(item.Template) || item.HiddenOnUI) return "This item type cannot be dismantled.";
        if (item.IsQuestItem || item.IsQualityQuest) return "Quest items cannot be dismantled.";
        if (item.Locked) return "This item is locked.";
        if (item.Favourite) return "Remove the favourite mark first.";
        if (item.IsEquipped || item.IsUsedInLoadout()) return "Unequip this item and remove it from every loadout first.";
        if (item.HasGemAttached) return "Remove attached relics first.";
        if (!item.IsDroppable) return "This item cannot be discarded.";
        if (!AllowedTemplate(item.Template)) return "This item is unavailable or protected.";
        Recipe? recipe = Match(item.Template);
        if (recipe == null) return "No compatible crafting recipe exists.";
        if (item.Quantity < recipe.Quantity) return $"Requires a complete batch of {recipe.Quantity} items.";
        if (!item.CanStack && recipe.Quantity != 1) return "This recipe requires a stackable output batch.";
        return null;
    }

    public Quote Quote(Item item, int batches = 1)
    {
        string? reason = Eligibility(item);
        if (reason != null) throw new InvalidOperationException(reason);
        Recipe recipe = Match(item.Template)!;
        var upgrades = new List<KeyValuePair<string, int>>();
        int level = Math.Max(0, item.ModifiedLevelWithoutNewGamePlus);
        if (level > 1000) throw new InvalidOperationException("The item level is outside the supported range.");
        if (item.Template.CanHaveItemLevel && !item.IsArrow && !item.IsThrowable)
        {
            ItemUpgradeConfigData config = item.Template.ItemUpgradeConfigConfig;
            for (int step = 0; config != null && step < level; step++)
            foreach (CountedItem ingredient in config.GetIngredients(step))
            {
                if (IsGoldFee(ingredient.itemTemplate)) continue;
                int count = checked(ingredient.quantity * GameConstants.Get.sharpeningHeroIngredientMultiplier);
                if (count == 0) continue;
                if (count < 0 || !AllowedMaterial(ingredient.itemTemplate))
                    throw new InvalidOperationException("An upgrade ingredient is unavailable or protected.");
                _materials[ingredient.itemTemplate.GUID] = ingredient.itemTemplate;
                upgrades.Add(new KeyValuePair<string, int>(ingredient.itemTemplate.GUID, count));
            }
        }
        var plan = new RecoveryPlan(recipe, batches, upgrades);
        if (plan.Consumed > item.Quantity) throw new InvalidOperationException("Not enough items for this batch.");
        return new Quote(item, plan, _outcomes[recipe.Outcome.Id].ItemName);
    }

    private Recipe? Match(ItemTemplate template)
    {
        if (!_matches.TryGetValue(template.GUID, out Recipe? match))
            _matches[template.GUID] = match = RecipeMatcher.Match(Profile(template), _recipes);
        return match;
    }

    private static bool AllowedMaterial(ItemTemplate template) => AllowedTemplate(template) &&
        (!template.CannotBeDropped || template.GetAttachment<CurrencyAsCraftingIngredientAttachment>() != null);

    private static bool IsGoldFee(ItemTemplate template) => template != null &&
        template.GetAttachment<CurrencyAsCraftingIngredientAttachment>()?.Type == CurrencyType.Money;

    private static bool AllowedTemplate(ItemTemplate template) => template != null && !template.IsAbstract &&
        !template.IsQuestItem() && template.Quality != ItemQuality.Quest && !template.HiddenOnUI &&
        template.GetAttachments().OfType<ItemFromDlcSpec>().All(spec => SocialService.Get.HasDlc(spec.RequiredDlcCategory));

    private static FieldInfo RequireField(Type type, string name, Type fieldType)
    {
        FieldInfo field = AccessTools.Field(type, name);
        if (field == null || field.FieldType != fieldType) throw new MissingFieldException(type.FullName, name);
        return field;
    }

    private static ItemProfile Profile(ItemTemplate template)
    {
        string family = "";
        EquipmentType? equipment = template.GetAttachment<ItemEquipSpec>()?.EquipmentType;
        if (template.IsArrow) family = "arrow";
        else if (template.IsThrowable) family = "throwable";
        else if (template.IsShield) family = "shield";
        else if (template.IsArmor && equipment != null)
        {
            string armorClass = template.IsHeavyArmor ? "heavy" : template.IsMediumArmor ? "medium" : template.IsLightArmor ? "light" : "";
            if (armorClass.Length > 0) family = "armor:" + equipment.EnumName + ":" + armorClass;
        }
        else if (template.IsJewelry && equipment != null) family = "jewelry:" + equipment.EnumName;
        else if (template.IsShortBow || template.IsMediumBow || template.IsHeavyBow) family = "bow";
        else if (template.IsMelee)
        {
            string weapon = template.IsDagger ? "dagger" : template.IsAxe ? "axe" : template.IsSword ? "sword" :
                template.IsBlunt ? "blunt" : template.IsPolearm ? "polearm" : template.IsFists ? "fists" : "";
            if (weapon.Length > 0 && (template.IsTwoHanded || template.IsOneHanded))
                family = "melee:" + weapon + ":" + (template.IsTwoHanded ? "2" : "1");
        }
        int? tier = null;
        foreach (string tag in template.Tags)
            if (tag.StartsWith("item:tier", StringComparison.Ordinal) && int.TryParse(tag.Substring(9), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                tier = number;
        var abstractTypes = template.AbstractTypes;
        try
        {
            string[] ids = abstractTypes.value.Select(t => t.GUID).ToArray();
            string weight = abstractTypes.value.Select(t => t.name).Where(n => n.Contains("Weight_"))
                .OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault() ?? "";
            return new ItemProfile(template.GUID, family, tier, weight, ids);
        }
        finally { abstractTypes.Release(); }
    }
}
