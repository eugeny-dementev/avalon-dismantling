using System;
using Awaken.TG.MVC;
using Awaken.TG.Main.Locations.Gems.Upgrades;
using Awaken.TG.Main.Heroes;
using Awaken.TG.Main.UI.Helpers;
using Awaken.TG.Main.Utility.UI;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace AvalonDismantling.Plugin;

[BepInPlugin("com.eugenydementev.avalondismantling", "Avalon Dismantling", "1.1.1")]
public sealed class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;
    private const string ButtonName = "AvalonDismantleButton";
    private Harmony? _harmony;
    private static bool _available;

    private void Awake()
    {
        Log = Logger;
        try
        {
            if (AccessTools.Field(typeof(VUpgradesUI), "upgradeButton") == null)
                throw new MissingFieldException(typeof(VUpgradesUI).FullName, "upgradeButton");
            var initialize = AccessTools.Method(typeof(VUpgradesUI), "OnInitialize")
                ?? throw new MissingMethodException(typeof(VUpgradesUI).FullName, "OnInitialize");
            _harmony = new Harmony("com.eugenydementev.avalondismantling");
            _harmony.Patch(initialize, postfix: new HarmonyMethod(typeof(Plugin), nameof(AddBenchButton)));
            _available = true;
            Log.LogInfo("Avalon Dismantling 1.1.1 loaded. Bench integration is ready.");
        }
        catch (Exception error)
        {
            Disable(error);
        }
    }

    private void OnDestroy()
    {
        _available = false;
        _harmony?.UnpatchSelf();
    }

    private static void AddBenchButton(VUpgradesUI __instance)
    {
        if (!_available) return;
        GameObject? clone = null;
        try
        {
            var original = (ButtonConfig)AccessTools.Field(typeof(VUpgradesUI), "upgradeButton").GetValue(__instance);
            if (original == null || original.button == null)
                throw new InvalidOperationException("The crafting bench button is unavailable.");
            Transform parent = original.transform.parent;
            if (parent.Find(ButtonName) != null) return;
            clone = Instantiate(original.gameObject, parent, false);
            clone.name = ButtonName;
            var button = clone.GetComponent<ButtonConfig>();
            if (button == null || button.button == null)
                throw new InvalidOperationException("The cloned crafting bench button has no native button binding.");
            button.button.ClearAllOnClickEvents();
            button.button.navigation = new Navigation { mode = Navigation.Mode.Automatic };
            button.InitializeButton(() => Open(__instance), "Dismantle");
            clone.SetActive(true);
        }
        catch (Exception error)
        {
            if (clone != null) Destroy(clone);
            Disable(error);
        }
    }

    private static void Open(VUpgradesUI parent)
    {
        if (!_available || World.Any<DismantleUI>() != null) return;
        DismantleUI? model = null;
        try
        {
            var inventory = Hero.Current.HeroItems;
            NativeInventory.StackItems(inventory, inventory.Inventory);
            model = new DismantleUI(new GameCatalog());
            World.Add(model);
            UIUtils.AddOverlayUIView(model, parent);
        }
        catch (Exception error)
        {
            if (model != null && !model.HasBeenDiscarded)
            {
                try { model.Discard(); }
                catch (Exception cleanupError) { Log.LogError(cleanupError); }
            }
            Disable(error);
        }
    }

    private static void Disable(Exception error)
    {
        _available = false;
        Log.LogError("Dismantling disabled because a required game binding failed. " + error);
    }
}
