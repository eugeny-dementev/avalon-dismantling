using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using AvalonDismantling.Core;
using AvalonDismantling.Plugin;
using Awaken.TG.Main.Crafting;
using Awaken.TG.Main.Crafting.Cooking;
using Awaken.TG.Main.Crafting.HandCrafting;
using Awaken.TG.Main.Crafting.HandCrafting.RecipeView;
using Awaken.TG.Main.Heroes;
using Awaken.TG.Main.Heroes.Items;
using Awaken.TG.Main.Heroes.Items.Attachments;
using Awaken.TG.Main.Heroes.Items.Gems;
using Awaken.TG.Main.Heroes.Thievery;
using Awaken.TG.Main.Locations.Gems.Upgrades;
using Awaken.TG.Main.Locations.Shops;
using Awaken.TG.Main.NewGamePlus;
using Awaken.TG.Main.Saving;
using Awaken.TG.Main.Saving.Cloud.Services;
using Awaken.TG.Main.Saving.SaveSlots;
using Awaken.TG.Main.Saving.Utils;
using Awaken.TG.Main.Templates;
using Awaken.TG.Main.UI.ButtonSystem;
using Awaken.TG.Main.UI.TitleScreen;
using Awaken.TG.Main.UI.TitleScreen.Loading;
using Awaken.TG.Main.Utility;
using Awaken.TG.Main.Utility.Patchers;
using Awaken.TG.Main.Utility.UI;
using Awaken.TG.MVC;
using Awaken.TG.MVC.UI.Events;
using Awaken.Utility.Collections;
using BepInEx;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UI;

namespace AvalonDismantling.Harness;

[BepInPlugin("local.avalon.dismantling.harness", "Avalon Dismantling Runtime Harness", "1.0.0")]
[BepInDependency("com.eugenydementev.avalondismantling", BepInDependency.DependencyFlags.HardDependency)]
public sealed class Harness : BaseUnityPlugin
{
    private const string MarkerName = "AVALON_DISMANTLING_ISOLATED_PROFILE.txt";
    private const string MarkerContent = "avalon-dismantling-runtime-test";
    private const string Shortsword = "142922f8147b6354f88b41b48f614108";
    private const string Hammer = "dfed842fae5e7f8488f1e82b1e53fae8";
    private const string CopperArrows = "52452824dbe969b40977ca61a68e371f";
    private static string Profile = "";
    private static string Cache = "";
    private static bool Testing;
    private static bool FaultTriggered;
    private static string FaultGuid = "";
    private static Fault FaultMode;
    private static Quote? FaultQuote;
    private static GameCatalog? FaultCatalog;
    private static Item? FaultExisting;
    private static bool ReentryRejected;
    private static bool CountRecoveryRolls;
    private static int RecoveryRollCalls;
    private Harmony? _harmony;
    private bool _ready;
    private bool _nativePatched;
    private bool _processing;
    private float _nextPoll;
    private float _nextReadinessReport;
    private int _originalWidth;
    private int _originalHeight;
    private FullScreenMode _originalScreenMode;
    private RefreshRate _originalRefreshRate;

    private enum Fault { None, Reject, Replace, Existing, ThrowAfter, Reenter }

    private void Awake()
    {
        try
        {
            string? requested = Environment.GetEnvironmentVariable("AVALON_DISMANTLING_TEST_PROFILE");
            if (string.IsNullOrWhiteSpace(requested))
            {
                Logger.LogWarning("Harness inactive: AVALON_DISMANTLING_TEST_PROFILE is not set.");
                return;
            }
            if (!Path.IsPathRooted(requested)) throw new InvalidOperationException("Test profile must be an absolute path.");
            Profile = Path.GetFullPath(requested).TrimEnd(Path.DirectorySeparatorChar);
            string artifactRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex", "artifacts");
            Require(IsBelow(Profile, artifactRoot), "Test profile must be under the local Codex artifacts directory.");
            Require(!IsBelow(Profile, Application.persistentDataPath), "Refusing the production profile directory.");
            Require(!IsBelow(Profile, Paths.GameRootPath), "Refusing a test profile inside the game directory.");
            Require(Directory.Exists(Profile), "Copy the profile before starting the harness.");
            for (DirectoryInfo? directory = new DirectoryInfo(Profile); directory != null; directory = directory.Parent)
                Require((directory.Attributes & FileAttributes.ReparsePoint) == 0, "Test profile ancestors must not be junctions or symbolic links.");
            Require(File.ReadAllText(Path.Combine(Profile, MarkerName)).Trim() == MarkerContent, "Missing explicit isolated-profile marker.");
            Require(!CloudService.IsInitialized, "CloudService initialized before isolation. Do not load a save.");
            Cache = Path.Combine(Profile, "HarnessCache");
            Directory.CreateDirectory(Cache);
            _harmony = new Harmony("local.avalon.dismantling.harness");
            // HeroItems initializes audio references, which are unavailable during BepInEx startup.
            foreach (Type patch in new[] { typeof(LocalService), typeof(ProfilePath), typeof(DomainCachePath),
                typeof(NoDefaultMigration), typeof(NoGogMigration), typeof(NoLegacyCacheMigration), typeof(ArchiveCachePath) })
                _harmony.CreateClassProcessor(patch).Patch();
            Write("cache-isolation.json", new
            {
                mode = "Explicit ZIP field-read and domain-directory patches; original readonly fields remain unchanged.",
                reflectedTemporary = AccessTools.Field(typeof(IOUtil), nameof(IOUtil.TemporaryCachePath)).GetValue(null),
                directTemporary = IOUtil.TemporaryCachePath,
                reflectedZip = AccessTools.Field(typeof(ZipArchiveUtils), nameof(ZipArchiveUtils.ZipPath)).GetValue(null),
                directZip = ZipArchiveUtils.ZipPath,
                isolatedZip = TestArchivePath(),
                isolatedDomains = AccessTools.PropertyGetter(typeof(LoadSaveDomainsCache), "CachedDomainsDirectoryPath").Invoke(null, null)
            });
            _ready = true;
            Write("startup.json", new { status = "isolated", profile = Profile, cache = Cache, utc = DateTime.UtcNow });
            Logger.LogInfo("Isolated runtime harness ready: " + Profile);
        }
        catch (Exception exception)
        {
            _ready = false;
            Logger.LogError("Harness isolation failed; no commands will run: " + exception);
            // A requested isolation failure must not continue into the real profile.
            Application.Quit(91);
        }
    }

    private void Update()
    {
        if (!_ready || _processing || Time.realtimeSinceStartup < _nextPoll) return;
        _nextPoll = Time.realtimeSinceStartup + 0.4f;
        string request = Path.Combine(Profile, "command.txt");
        if (!File.Exists(request)) return;
        try
        {
            Require(new FileInfo(request).Length <= 512, "Command is too long.");
            string command = File.ReadAllText(request).Trim();
            if (!CloudService.IsInitialized) return;
            AssertIsolation();
            if (RequiresHero(command) && !HeroReady())
            {
                if (Time.realtimeSinceStartup >= _nextReadinessReport)
                {
                    _nextReadinessReport = Time.realtimeSinceStartup + 2f;
                    WriteReadiness(command);
                }
                return;
            }
            File.Move(request, Path.Combine(Profile, "command-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".done"));
            if (RequiresHero(command)) EnsureNativePatches();
            StartCoroutine(Process(command));
        }
        catch (Exception exception) { LogFailure("read-command", exception); }
    }

    private void EnsureNativePatches()
    {
        if (_nativePatched) return;
        Require(HeroReady(), "Native test patches require an initialized hero.");
        foreach (Type patch in new[] { typeof(NoTestArrowEquip), typeof(NoTestFoodEquip), typeof(InjectInventoryFault), typeof(ObserveRecoveryRolls) })
            _harmony!.CreateClassProcessor(patch).Patch();
        _nativePatched = true;
        Logger.LogInfo("Native inventory test patches installed after hero initialization.");
    }

    private IEnumerator Process(string command)
    {
        _processing = true;
        Write("last-command.json", new { command, status = "running", utc = DateTime.UtcNow });
        Exception? failure = null;
        IEnumerator runner = RunCommand(command);
        while (true)
        {
            bool next;
            try { next = runner.MoveNext(); }
            catch (Exception exception) { failure = exception; break; }
            if (!next) break;
            yield return runner.Current;
        }
        (runner as IDisposable)?.Dispose();
        if (failure != null) LogFailure(command, failure);
        else Write("last-command.json", new { command, status = "complete", utc = DateTime.UtcNow });
        _processing = false;
    }

    private IEnumerator RunCommand(string command)
    {
        AssertIsolation();
        switch (command)
        {
            case "status":
                WriteStatus();
                break;
            case "quit":
                if (_originalWidth > 0)
                {
                    IEnumerator restore = ChangeResolution("resolution-restore");
                    while (restore.MoveNext()) yield return restore.Current;
                }
                Application.Quit();
                break;
            case "resolution1080":
            case "resolution4k":
            case "resolution-restore":
                IEnumerator resolution = ChangeResolution(command);
                while (resolution.MoveNext()) yield return resolution.Current;
                break;
            case "screenshot":
                string screenshot = Path.Combine(Profile, "screenshot.png");
                if (File.Exists(screenshot)) File.Delete(screenshot);
                ScreenCapture.CaptureScreenshot(screenshot);
                float captureDeadline = Time.realtimeSinceStartup + 10f;
                while (!File.Exists(screenshot) && Time.realtimeSinceStartup < captureDeadline) yield return null;
                Require(File.Exists(screenshot), "Screenshot was not written before the capture timeout.");
                break;
            case "load-latest":
                IEnumerator titleLoad = LoadLatestFromTitle();
                while (titleLoad.MoveNext()) yield return titleLoad.Current;
                break;
            case "open-bench":
                OpenBench();
                break;
            case "close-bench":
                var interfaces = new List<Model>();
                foreach (DismantleUI ui in World.All<DismantleUI>()) interfaces.Add(ui);
                foreach (CraftingTabsUI ui in World.All<CraftingTabsUI>()) interfaces.Add(ui);
                foreach (UpgradesUI ui in World.All<UpgradesUI>()) interfaces.Add(ui);
                foreach (Model ui in interfaces) ui.Discard();
                break;
            case "run-tests":
                OpenBench();
                RunNativeTests();
                break;
            case "run-bulk-tests":
                RunBulkTests();
                break;
            case "benchmark-bulk":
                IEnumerator bulkBenchmark = RunBulkBenchmark();
                try { while (bulkBenchmark.MoveNext()) yield return bulkBenchmark.Current; }
                finally { (bulkBenchmark as IDisposable)?.Dispose(); }
                break;
            case "benchmark":
                using (var baseline = new InventorySnapshot())
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var catalog = new GameCatalog();
                    double catalogMs = timer.Elapsed.TotalMilliseconds;
                    Item[] sources = Enumerable.Range(0, 3).Select(_ => Create(Shortsword)).ToArray();
                    var ui = World.Add(new DismantleUI(catalog));
                    yield return null;
                    yield return null;
                    var samples = new List<object>();
                    try
                    {
                        for (int sample = 0; sample < 3; sample++)
                        {
                            Item source = sources[sample];
                            var nativeList = Field<Awaken.TG.Main.Heroes.CharacterSheet.Items.Panel.ItemsUI>(
                                Field<DismantleItemList>(ui, "_list"), "_itemsUI")
                                .Element<Awaken.TG.Main.Heroes.CharacterSheet.Items.Panel.List.ItemsListUI>();
                            var retained = nativeList.Elements<Awaken.TG.Main.Heroes.CharacterSheet.Items.Panel.List.ItemsListElementUI>()
                                .GetManagedEnumerator().First(row => row.Item != source);
                            timer.Restart();
                            ui.Select(source);
                            double selectMs = timer.Elapsed.TotalMilliseconds;
                            timer.Restart();
                            AccessTools.Method(typeof(DismantleUI), "Confirm").Invoke(ui, null);
                            double confirmMs = timer.Elapsed.TotalMilliseconds;
                            Require(source.HasBeenDiscarded && !NativeInventory.Disabled, "Benchmark transaction failed.");
                            Require(!retained.HasBeenDiscarded, "Unchanged inventory row was rebuilt.");
                            Require(!nativeList.Elements<Awaken.TG.Main.Heroes.CharacterSheet.Items.Panel.List.ItemsListElementUI>()
                                .GetManagedEnumerator().Any(row => row.Item == source), "Consumed item row survived.");
                            samples.Add(new { sample, selectMs, confirmMs });
                            yield return null;
                        }
                        Write("benchmark.json", new { version = typeof(Plugin.Plugin).Assembly.GetName().Version?.ToString(), catalogMs, samples });
                    }
                    finally { ui.Close(); }
                }
                break;
            case "run-ui-tests":
                IEnumerator uiTests = RunUiTests();
                try { while (uiTests.MoveNext()) yield return uiTests.Current; }
                finally { (uiTests as IDisposable)?.Dispose(); }
                break;
            case "test-vanilla-crafting":
                IEnumerator craftingTest = TestVanillaCrafting();
                try { while (craftingTest.MoveNext()) yield return craftingTest.Current; }
                finally { (craftingTest as IDisposable)?.Dispose(); }
                break;
            case "preview-item":
                IEnumerator itemPreview = PreviewExistingItem();
                while (itemPreview.MoveNext()) yield return itemPreview.Current;
                break;
            case "preview-currency":
                OpenBench();
                using (var snapshot = new InventorySnapshot())
                {
                    Testing = true;
                    DismantleUI? preview = null;
                    try
                    {
                        var catalog = new GameCatalog();
                        ItemTemplate currency = World.Services.Get<TemplatesProvider>().GetAllOfType<ItemTemplate>(TemplateTypeFlag.Regular)
                            .First(template => template.GetAttachment<CurrencyAsCraftingIngredientAttachment>()?.Type == CurrencyType.Cobweb);
                        Item source = Create(Shortsword);
                        Quote original = catalog.Quote(source);
                        var materials = Field<Dictionary<string, ItemTemplate>>(catalog, "_materials");
                        materials[currency.GUID] = currency;
                        var recipe = new Recipe("harness-cobweb-preview", original.Plan.Recipe.Outcome, 1,
                            new[] { new KeyValuePair<string, int>(currency.GUID, 3) });
                        var quote = new Quote(source, new RecoveryPlan(recipe, 1, Array.Empty<KeyValuePair<string, int>>()), source.DisplayName);
                        var before = new InventorySnapshot();
                        preview = World.Add(new DismantleUI(catalog));
                        preview.Select(null);
                        VDismantleUI view = Field<VDismantleUI>(preview, "_view");
                        view.ShowQuote(catalog, quote);
                        yield return null;
                        yield return null;
                        before.AssertUnchanged();
                        view.ClearIngredients();
                        preview.Close();
                        yield return null;
                        before.AssertUnchanged();
                        Write("currency-preview.json", new { status = "passed", fixture = "synthetic Cobweb ingredient quote; no transaction",
                            material = currency.GUID, name = currency.ItemName, gold = Hero.Current.Wealth.BaseValue, cobweb = Hero.Current.Cobweb.BaseValue });
                    }
                    finally
                    {
                        if (preview != null && !preview.HasBeenDiscarded) preview.Discard();
                        Testing = false;
                    }
                }
                break;
            case "save-test":
                SavePersistenceTest();
                break;
            case "verify-persistence":
                VerifyPersistence();
                break;
            default:
                throw new ArgumentException("Unknown harness command: " + command);
        }
    }

    private void RunNativeTests()
    {
        var results = new List<TestResult>();
        var catalog = new GameCatalog();
        Testing = true;
        try
        {
            Case("exact recipe, recovery bounds, source consumption, gold stable", () =>
            {
                Item source = Create(Shortsword);
                Quote quote = catalog.Quote(source);
                Require(quote.IsExact, "Shortsword did not match its exact recipe.");
                VerifyExecution(catalog, quote);
            });
            Case("preview cancellation leaves inventory unchanged", () =>
            {
                Item source = Create(Shortsword);
                var before = new InventorySnapshot();
                _ = catalog.Quote(source);
                before.AssertUnchanged();
            });
            Case("changed raw recipe rejects confirmation without inventory changes", () =>
            {
                Item source = Create(Shortsword);
                Quote quote = catalog.Quote(source);
                var definitions = Field<Dictionary<string, Awaken.TG.Main.Crafting.Recipes.BaseRecipe>>(catalog, "_definitions");
                var ingredientsField = AccessTools.Field(typeof(Awaken.TG.Main.Crafting.Recipes.BaseRecipe), "ingredients");
                var ingredients = (Array)ingredientsField.GetValue(definitions[quote.Plan.Recipe.Id]);
                object ingredient = ingredients.GetValue(0)!;
                var countField = AccessTools.Field(ingredient.GetType(), "count");
                int count = (int)countField.GetValue(ingredient);
                var before = new InventorySnapshot();
                bool rejected = false;
                try
                {
                    countField.SetValue(ingredient, count + 1);
                    try { NativeInventory.Execute(catalog, quote); }
                    catch (InvalidOperationException) { rejected = true; }
                }
                finally { countField.SetValue(ingredient, count); }
                Require(rejected, "Changed recipe was accepted.");
                before.AssertUnchanged();
            });
            Case("positive level includes upgrade materials", () =>
            {
                Item source = Create(Shortsword);
                Quote zero = catalog.Quote(source);
                source.Level.IncreaseBy(2);
                Quote upgraded = catalog.Quote(source);
                Require(upgraded.DisplayLevel == zero.DisplayLevel + 2, "Synthetic upgrade level did not increase by two.");
                Require(upgraded.Plan.Upgrades.Values.Sum() > zero.Plan.Upgrades.Values.Sum(), "Positive upgrade levels added no materials.");
                VerifyExecution(catalog, upgraded);
            });
            Case("negative level adds no upgrade materials", () =>
            {
                Item source = Create(Shortsword);
                source.Level.DecreaseBy(source.ModifiedLevelWithoutNewGamePlus + 1);
                Quote quote = catalog.Quote(source);
                Require(quote.DisplayLevel == -1 && quote.Plan.Upgrades.Count == 0, "Negative level incorrectly refunds upgrades.");
            });
            Case("separate New Game Plus level bonus is excluded", () =>
            {
                Item source = Create(Shortsword, newGamePlusLevel: 2);
                Quote quote = catalog.Quote(source);
                Require(quote.DisplayLevel == source.Template.LevelBonus && quote.Plan.Upgrades.Count == 0,
                    "Separate New Game Plus bonus was refunded as an upgrade.");
            });
            Case("Hammer of Stupefaction automatically matches compatible recipe", () =>
            {
                Item source = Create(Hammer);
                Quote quote = catalog.Quote(source);
                Require(!quote.IsExact && quote.Plan.Recipe.Outcome.Family == "melee:blunt:2", "Hammer fallback crossed weapon families.");
                results.Add(new TestResult("hammer comparison", "information", quote.MatchedName + " / " + quote.Plan.Recipe.Id));
                VerifyExecution(catalog, quote);
            });
            Case("ammunition consumes whole batches and retains remainder", () =>
            {
                Item source = Create(CopperArrows, 25);
                Quote quote = catalog.Quote(source, 2);
                Require(quote.Plan.Consumed == 20, "Copper arrow recipe did not consume two ten-arrow batches.");
                VerifyExecution(catalog, quote);
                Require(source.Quantity == 5 && source.Inventory == Hero.Current.HeroItems, "Arrow remainder changed incorrectly.");
            });
            Case("incomplete ammunition batch is unavailable", () =>
            {
                Item source = Create(CopperArrows, 9);
                Require(catalog.Eligibility(source) != null, "Nine arrows can dismantle a ten-arrow recipe.");
            });
            Case("favourite protection", () =>
            {
                Item source = Create(Shortsword);
                source.ToggleFavourite();
                Require(catalog.Eligibility(source) != null, "Favourite item remained eligible.");
            });
            Case("locked protection", () =>
            {
                Item source = Create(Shortsword);
                source.AddElement(new LockItemSlot());
                Require(catalog.Eligibility(source) != null, "Locked item remained eligible.");
            });
            Case("attached relic protection using native attachment", () =>
            {
                Item source = Create(Shortsword);
                ItemTemplate gemTemplate = World.Services.Get<TemplatesProvider>().GetAllOfType<ItemTemplate>(TemplateTypeFlag.Regular)
                    .Where(template => !template.IsAbstract && template.GetAttachment<GemAttachment>()?.Type == GemType.Weapon)
                    .OrderBy(template => template.GUID, StringComparer.Ordinal).First();
                Item gem = Create(gemTemplate.GUID);
                ItemGems sockets = source.TryGetElement<ItemGems>() ?? source.AddElement(new ItemGems(1, source.MaxGemSlots));
                if (!sockets.CanAttach) sockets.IncreaseLimit();
                GemAttached attached = sockets.AttachGem(gem);
                Require(attached != null && source.HasGemAttached && gem.HasBeenDiscarded, "Native relic attachment did not consume the synthetic relic.");
                Require(catalog.Eligibility(source)?.IndexOf("relic", StringComparison.OrdinalIgnoreCase) >= 0, "Attached relic did not block dismantling.");
                var before = new InventorySnapshot();
                bool rejected = false;
                try { catalog.Quote(source); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "Attached relic source still produced an executable quote.");
                before.AssertUnchanged();
            });
            foreach (Fault mode in new[] { Fault.Reject, Fault.Replace, Fault.Existing, Fault.ThrowAfter })
            {
                Fault selected = mode;
                Case("rollback on inventory " + selected, () =>
                {
                    Item source = Create(Shortsword);
                    Quote quote = catalog.Quote(source);
                    var before = new InventorySnapshot();
                    SetFault(selected, catalog, quote);
                    bool rejected = false;
                    try { NativeInventory.Execute(catalog, quote); }
                    catch (InvalidOperationException) { rejected = true; }
                    finally { FaultMode = Fault.None; }
                    Require(FaultTriggered && rejected, "Injected inventory fault was not rejected.");
                    Require(!NativeInventory.Disabled && !NativeInventory.Busy, "A recoverable fault disabled or locked dismantling.");
                    before.AssertUnchanged();
                });
            }
            Case("reentrant confirmation is rejected without duplicate delivery", () =>
            {
                Item source = Create(Shortsword);
                Quote quote = catalog.Quote(source);
                SetFault(Fault.Reenter, catalog, quote);
                VerifyExecution(catalog, quote);
                Require(FaultTriggered && ReentryRejected, "Reentrant execution was not rejected.");
            });
            Item? stolenReference = Hero.Current.HeroItems.Inventory.FirstOrDefault(item => item.IsStolen);
            if (stolenReference == null)
                results.Add(new TestResult("stolen source returns clean materials", "skipped", "Copied profile contains no stolen item with usable crime metadata."));
            else
                Case("stolen source returns clean materials", () =>
                {
                    Item source = Create(Shortsword);
                    source.AddElement(new StolenItemElement(stolenReference.Element<StolenItemElement>().GetCrimeData()));
                    Require(source.IsStolen, "Synthetic item was not marked stolen.");
                    VerifyExecution(catalog, catalog.Quote(source));
                });
            CheckExistingProtection("quest protection", item => item.IsQuestItem || item.IsQualityQuest);
            CheckExistingProtection("equipped protection", item => item.IsEquipped);
            CheckExistingProtection("loadout protection", item => item.IsUsedInLoadout());
            CheckExistingProtection("non-droppable protection", item => item.Template.CannotBeDropped);
        }
        finally
        {
            FaultMode = Fault.None;
            FaultQuote = null;
            FaultCatalog = null;
            FaultExisting = null;
            Testing = false;
            Write("native-tests.json", new { utc = DateTime.UtcNow, profile = Profile, results,
                passed = results.Count(result => result.Status == "passed"), failed = results.Count(result => result.Status == "failed"),
                skipped = results.Count(result => result.Status == "skipped"), disabled = NativeInventory.Disabled });
        }
        Require(results.All(result => result.Status != "failed"), "One or more native tests failed. Read native-tests.json.");

        void Case(string name, Action action)
        {
            if (NativeInventory.Disabled) { results.Add(new TestResult(name, "skipped", "Dismantling disabled after an earlier failure.")); return; }
            try
            {
                using (var snapshot = new InventorySnapshot()) action();
                results.Add(new TestResult(name, "passed", ""));
            }
            catch (Exception exception) { results.Add(new TestResult(name, "failed", exception.ToString())); }
            finally { FaultMode = Fault.None; }
        }

        void CheckExistingProtection(string name, Func<Item, bool> predicate)
        {
            Item? candidate = Hero.Current.HeroItems.Items.FirstOrDefault(predicate);
            if (candidate == null) results.Add(new TestResult(name, "skipped", "No matching item exists in the copied profile."));
            else Case(name, () => Require(catalog.Eligibility(candidate) != null, "Protected item remained eligible."));
        }
    }

    private static void VerifyExecution(GameCatalog catalog, Quote quote)
    {
        Hero hero = Hero.Current;
        var before = quote.Plan.Maximum.Keys.ToDictionary(id => id, id => CleanCount(catalog.Material(id)));
        float gold = hero.Wealth.BaseValue;
        IReadOnlyDictionary<string, int> returned = NativeInventory.Execute(catalog, quote);
        foreach (KeyValuePair<string, int> output in returned)
        {
            Require(output.Value >= quote.Plan.Minimum[output.Key] && output.Value <= quote.Plan.Maximum[output.Key], "Recovery exceeded preview bounds.");
            Require(CleanCount(catalog.Material(output.Key)) - before[output.Key] == output.Value, "Clean ingredient delta differs from the transaction result.");
        }
        Require(hero.Wealth.BaseValue == gold, "Gold changed during a material-only test.");
        Require(!NativeInventory.Disabled && !NativeInventory.Busy, "Successful operation disabled or locked dismantling.");
        if (quote.SourceQuantity == quote.Plan.Consumed)
            Require(quote.Source.HasBeenDiscarded || !hero.HeroItems.Items.Contains(quote.Source), "Source item survived full consumption.");
        else Require(quote.Source.Quantity == quote.SourceQuantity - quote.Plan.Consumed, "Source stack delta is incorrect.");
    }

    private static Item Create(string guid, int quantity = 1, int newGamePlusLevel = 0)
    {
        ItemTemplate template = World.Services.Get<TemplatesProvider>().Get<ItemTemplate>(guid)
            ?? throw new InvalidOperationException("Missing test item template: " + guid);
        int level = checked(template.LevelBonus + NewGamePlusSystem.CalculateBonusItemLevelValue(newGamePlusLevel));
        var item = new Item(template, quantity, level, newGamePlusLevel: newGamePlusLevel);
        Item result = Hero.Current.HeroItems.Add(item, allowStacking: false);
        Require(ReferenceEquals(item, result) && item.Inventory == Hero.Current.HeroItems && item.Quantity == quantity, "Could not create an isolated synthetic test item.");
        return item;
    }

    private static T Field<T>(object owner, string name) => (T)(AccessTools.Field(owner.GetType(), name).GetValue(owner)
        ?? throw new InvalidOperationException("Missing runtime fixture field: " + name));

    private void RunBulkTests()
    {
        Require(!World.HasAny<DismantleUI>() && !World.HasAny<CraftingTabsUI>(), "Run close-bench before bulk regression tests.");
        var results = new List<TestResult>();
        var catalog = new GameCatalog();
        Testing = true;
        CountRecoveryRolls = true;
        try
        {
            Case("eligible filters exclude protected sources and use native categories", fixture =>
            {
                Item sword = Create(Shortsword);
                Create(CopperArrows, 25);
                Create(CopperArrows, 9);
                Create(Hammer);
                var outcomes = Field<Dictionary<string, ItemTemplate>>(catalog, "_outcomes").Values;
                Create(outcomes.First(template => template.IsArmor).GUID);
                Create(outcomes.First(template => template.IsJewelry).GUID);
                Item favourite = Create(Shortsword);
                favourite.ToggleFavourite();
                Create(Shortsword).AddElement(new LockItemSlot());
                Item relicSource = Create(Shortsword);
                AttachSyntheticRelic(relicSource);
                DismantleUI ui = fixture.Open(catalog);
                Item[] eligible = Hero.Current.HeroItems.Inventory.Where(item => catalog.Eligibility(item) == null).ToArray();
                Require(new HashSet<Item>(ui.PossibleItems).SetEquals(eligible), "Eligible snapshot includes protected items or omits valid items.");
                foreach (string type in new[] { "Weapons", "Armor", "Other" })
                {
                    ui.ChangeType(type);
                    Require(ui.SelectedType == type && new HashSet<Item>(ui.PossibleItems).SetEquals(eligible.Where(item => TestType(item) == type)),
                        "Native category filter differs from the eligible inventory: " + type);
                }
                ui.ChangeType("Weapons");
                ui.SetRarity(sword.Quality);
                Require(ui.PossibleItems.All(item => TestType(item) == "Weapons" && item.Quality == sword.Quality), "Type and rarity filters are not combined.");
                ui.Select(favourite);
                Require(!Field<Prompt>(ui, "_confirm").IsActive, "Direct selection bypassed favourite protection.");
            });
            Case("CraftMany gate and rarity normalization", fixture =>
            {
                Item source = Create(Shortsword);
                Item hammer = Create(Hammer);
                Create(Field<Dictionary<string, ItemTemplate>>(catalog, "_outcomes").Values.First(template => template.IsArmor).GUID);
                DismantleUI ui = fixture.Open(catalog);
                Prompt bulk = ui.Prompts.Elements<Prompt>().GetManagedEnumerator()
                    .First(prompt => ReferenceEquals(Field<KeyBindings>(prompt, "_key"), KeyBindings.UI.Crafting.CraftMany));
                Require(!ui.BulkAvailable && !bulk.IsActive, "All types enabled bulk dismantling.");
                ui.SetRarity(source.Quality);
                Require(!ui.BulkAvailable && !bulk.IsActive, "A rarity alone enabled bulk dismantling.");
                ui.ChangeType("Weapons");
                Require(ui.BulkAvailable && bulk.IsActive, "Specific type and rarity did not enable bulk dismantling.");
                ui.SetRarity(null);
                Require(!ui.BulkAvailable && !bulk.IsActive, "All rarities enabled bulk dismantling.");
                ItemQuality[] rarities = ui.PossibleItems.Select(item => item.Quality).Distinct().OrderBy(quality => quality.Priority).ToArray();
                foreach (ItemQuality quality in rarities)
                {
                    ui.CycleRarity();
                    Require(ui.SelectedRarity == quality, "Rarity cycling does not follow native priority.");
                }
                ui.CycleRarity();
                Require(ui.SelectedRarity == null, "Rarity cycling did not return to All.");
                Require(hammer.Quality != source.Quality, "Rarity normalization fixture requires two weapon qualities.");
                ui.SetRarity(hammer.Quality);
                hammer.AddElement(new LockItemSlot());
                ui.RebuildSnapshot();
                Require(ui.SelectedType == "Weapons" && ui.SelectedRarity == null, "An emptied rarity was not reset while its type remained available.");
                source.AddElement(new LockItemSlot());
                ui.RebuildSnapshot();
                Require(ui.SelectedType == "All" && ui.SelectedRarity == null, "An emptied type was not reset to All before rarity normalization.");
            });
            Case("unsupported upgrade estimate stays outside the eligible snapshot", fixture =>
            {
                Item excessive = Create(Shortsword);
                excessive.Level.IncreaseBy(1001);
                Item valid = Create(Shortsword);
                DismantleUI ui = fixture.Open(catalog);
                Require(excessive.ModifiedLevelWithoutNewGamePlus > 1000 && !ui.PossibleItems.Contains(excessive) && ui.PossibleItems.Contains(valid),
                    "Unsupported upgrade estimate was listed or a valid source disappeared.");
                var before = new InventorySnapshot();
                ui.Select(excessive);
                Require(!Field<Prompt>(ui, "_confirm").IsActive && RecoveryRollCalls == 0, "Unsupported direct selection enabled confirmation or rolled recovery.");
                before.AssertUnchanged();
            });
            Case("bulk review freezes full batches without rolling or consuming", fixture =>
            {
                Item arrows = Create(CopperArrows, 25);
                DismantleUI ui = fixture.Open(catalog);
                SelectBulk(ui, arrows);
                var before = new InventorySnapshot();
                ui.OpenBulkReview();
                Require(ui.BulkReviewing && ui.BulkReviewEntries.Length == 1 && ui.BulkReviewEntries[0].Plan.Consumed == 20,
                    "Review did not capture complete ammunition batches.");
                Require(ui.BulkReviewEntries[0].Plan.Batches == 2 && RecoveryRollCalls == 0, "Review rolled recovery or lost a batch.");
                before.AssertUnchanged();
                ui.Close();
                Require(!ui.BulkReviewing && !ui.BulkProcessing && !ui.HasBeenDiscarded && RecoveryRollCalls == 0,
                    "Cancelling review discarded the menu or rolled recovery.");
                before.AssertUnchanged();
            });
            Case("bulk completion keeps ammo remainder and ignores repeated confirmation", fixture =>
            {
                Item remainder = Create(CopperArrows, 25);
                Create(CopperArrows, 30);
                DismantleUI ui = fixture.Open(catalog);
                SelectBulk(ui, remainder);
                ui.OpenBulkReview();
                Quote[] entries = ui.BulkReviewEntries.ToArray();
                Require(entries.Length == 2 && entries.Sum(entry => entry.Plan.Consumed) == 50, "Review omitted a full ammunition stack.");
                var minima = Recovery.Merge(entries.SelectMany(entry => entry.Plan.Minimum));
                var maxima = Recovery.Merge(entries.SelectMany(entry => entry.Plan.Maximum));
                var counts = maxima.Keys.ToDictionary(id => id, id => CleanCount(catalog.Material(id)));
                float gold = Hero.Current.Wealth.BaseValue;
                InvokeConfirm(ui);
                InvokeConfirm(ui);
                Require(ui.BulkProcessing && ui.BulkCompleted == 0 && RecoveryRollCalls == 0, "Confirm consumed inventory before a queue tick.");
                FinishBulk(ui, entries.Length);
                Require(ui.BulkCompleted == entries.Length && RecoveryRollCalls == entries.Length, "Queue consumed entries more than once.");
                Require(remainder.Quantity == 5 && !remainder.HasBeenDiscarded, "Bulk dismantling lost the incomplete ammunition remainder.");
                foreach (var count in counts)
                {
                    int delta = CleanCount(catalog.Material(count.Key)) - count.Value;
                    Require(delta >= minima[count.Key] && delta <= maxima[count.Key], "Bulk material delta exceeds the review range.");
                }
                Require(Hero.Current.Wealth.BaseValue == gold, "Bulk dismantling changed gold.");
                var completed = new InventorySnapshot();
                InvokeConfirm(ui);
                ui.Tick();
                completed.AssertUnchanged();
            });
            Case("changed raw recipe stops preflight before the first source", fixture =>
            {
                Item source = Create(Shortsword);
                Create(Shortsword);
                DismantleUI ui = fixture.Open(catalog);
                SelectBulk(ui, source);
                ui.OpenBulkReview();
                Quote entry = ui.BulkReviewEntries[0];
                var before = new InventorySnapshot();
                using (var change = new RecipeFixture(catalog, entry, selfIngredient: false))
                {
                    InvokeConfirm(ui);
                    ui.Tick();
                    Require(!ui.BulkProcessing && ui.BulkCompleted == 0 && RecoveryRollCalls == 0, "Changed raw recipe passed bulk preflight.");
                    before.AssertUnchanged();
                }
            });
            Case("mid-queue protection stops remaining sources", fixture =>
            {
                Item source = Create(Shortsword);
                Create(Shortsword);
                Create(Shortsword);
                DismantleUI ui = fixture.Open(catalog);
                SelectBulk(ui, source);
                ui.OpenBulkReview();
                Quote[] entries = ui.BulkReviewEntries.ToArray();
                InvokeConfirm(ui);
                ui.Tick();
                Require(ui.BulkCompleted == 1, "First queue tick did not complete exactly one source.");
                entries[1].Source.AddElement(new LockItemSlot());
                ui.Tick();
                ui.Tick();
                Require(!ui.BulkProcessing && ui.BulkCompleted == 1 && RecoveryRollCalls == 1, "Queue continued after a source became locked.");
                Require(entries[0].Source.HasBeenDiscarded && entries.Skip(1).All(entry => !entry.Source.HasBeenDiscarded && entry.Source.Quantity == entry.SourceQuantity),
                    "A queued source changed after the protection failure.");
            });
            Case("disabled inventory stops between entries without another roll", fixture =>
            {
                Item source = Create(Shortsword);
                Create(Shortsword);
                Create(Shortsword);
                DismantleUI ui = fixture.Open(catalog);
                SelectBulk(ui, source);
                ui.OpenBulkReview();
                Quote[] entries = ui.BulkReviewEntries.ToArray();
                InvokeConfirm(ui);
                ui.Tick();
                Require(ui.BulkCompleted == 1 && RecoveryRollCalls == 1, "Fixture did not complete its first source.");
                var completed = new InventorySnapshot();
                FieldInfo disabled = AccessTools.Field(typeof(NativeInventory), "<Disabled>k__BackingField");
                bool previous = NativeInventory.Disabled;
                try
                {
                    disabled.SetValue(null, true);
                    ui.Tick();
                    ui.Tick();
                    Require(!ui.BulkProcessing && ui.BulkCompleted == 1 && RecoveryRollCalls == 1,
                        "Disabled inventory processed another source or lost completed progress.");
                    Require(entries[0].Source.HasBeenDiscarded && entries.Skip(1).All(entry => !entry.Source.HasBeenDiscarded && entry.Source.Quantity == entry.SourceQuantity),
                        "Disabled inventory changed a remaining source.");
                    completed.AssertUnchanged();
                }
                finally { disabled.SetValue(null, previous); }
            });
            Case("cancellation between entries preserves completed recovery", fixture =>
            {
                Item source = Create(Shortsword);
                Create(Shortsword);
                Create(Shortsword);
                DismantleUI ui = fixture.Open(catalog);
                SelectBulk(ui, source);
                ui.OpenBulkReview();
                Quote[] entries = ui.BulkReviewEntries.ToArray();
                InvokeConfirm(ui);
                ui.Tick();
                var completed = new InventorySnapshot();
                ui.Close();
                ui.Tick();
                Require(!ui.BulkProcessing && ui.BulkCompleted == 1 && RecoveryRollCalls == 1, "Cancellation processed another source or lost its completion count.");
                Require(entries.Skip(1).All(entry => !entry.Source.HasBeenDiscarded), "Cancellation consumed a remaining source.");
                completed.AssertUnchanged();
            });
            Case("rejected material delivery stops and rolls back one bulk entry", fixture =>
            {
                Item source = Create(Shortsword);
                Create(Shortsword);
                DismantleUI ui = fixture.Open(catalog);
                SelectBulk(ui, source);
                ui.OpenBulkReview();
                Quote first = ui.BulkReviewEntries[0];
                var before = new InventorySnapshot();
                InvokeConfirm(ui);
                SetFault(Fault.Reject, catalog, first);
                ui.Tick();
                ui.Tick();
                Require(FaultTriggered && !ui.BulkProcessing && ui.BulkCompleted == 0 && !NativeInventory.Disabled,
                    "Rejected material delivery did not stop the bulk operation safely.");
                before.AssertUnchanged();
            });
            Case("newly recovered eligible equipment never joins the frozen queue", fixture =>
            {
                Item source = Create(Shortsword);
                Quote original = catalog.Quote(source);
                using (var change = new RecipeFixture(catalog, original, selfIngredient: true))
                {
                    var selfCatalog = new GameCatalog();
                    DismantleUI ui = fixture.Open(selfCatalog);
                    SelectBulk(ui, source);
                    ui.OpenBulkReview();
                    Require(ui.BulkReviewEntries.Length == 1 && ui.BulkReviewEntries[0].Plan.Maximum[Shortsword] == 1,
                        "Self-returning equipment fixture did not enter the review.");
                    var existing = new HashSet<Item>(Hero.Current.HeroItems.Inventory);
                    InvokeConfirm(ui);
                    FinishBulk(ui, 1);
                    Item[] recovered = Hero.Current.HeroItems.Inventory.Where(item => !existing.Contains(item) && item.Template.GUID == Shortsword).ToArray();
                    Require(source.HasBeenDiscarded && recovered.Length == 1 && !ReferenceEquals(source, recovered[0]) &&
                        !recovered[0].IsStolen && !recovered[0].Locked && !recovered[0].HasBeenDiscarded && selfCatalog.Eligibility(recovered[0]) == null,
                        "Self-returning source did not become a new clean eligible inventory item.");
                    Require(ui.BulkCompleted == 1 && RecoveryRollCalls == 1 && !ui.BulkProcessing, "Recovered equipment extended the queue.");
                    var finished = new InventorySnapshot();
                    ui.Tick();
                    finished.AssertUnchanged();
                }
            });
        }
        finally
        {
            FaultMode = Fault.None;
            CountRecoveryRolls = false;
            Testing = false;
            Write("bulk-tests.json", new { utc = DateTime.UtcNow, results,
                passed = results.Count(result => result.Status == "passed"), failed = results.Count(result => result.Status == "failed"),
                detail = "Synthetic queues only; original eligible copied-profile items temporarily locked. Explicit controller ticks; no physical input." });
        }
        Require(results.All(result => result.Status != "failed"), "Bulk regression tests failed. Read bulk-tests.json.");

        void Case(string name, Action<BulkFixture> action)
        {
            RecoveryRollCalls = 0;
            if (NativeInventory.Disabled) { results.Add(new TestResult(name, "skipped", "Inventory feature was disabled.")); return; }
            try
            {
                using (var fixture = new BulkFixture(catalog)) action(fixture);
                results.Add(new TestResult(name, "passed", ""));
            }
            catch (Exception exception) { results.Add(new TestResult(name, "failed", exception.ToString())); }
            finally { FaultMode = Fault.None; }
        }
    }

    private IEnumerator RunBulkBenchmark()
    {
        Require(!World.HasAny<DismantleUI>() && !World.HasAny<CraftingTabsUI>(), "Run close-bench before benchmark-bulk.");
        var samples = new List<object>();
        double reviewMs = 0;
        double totalMs = 0;
        int completed = 0;
        int consumed = 0;
        Testing = true;
        CountRecoveryRolls = true;
        RecoveryRollCalls = 0;
        try
        {
            var catalog = new GameCatalog();
            using var fixture = new BulkFixture(catalog);
            Item arrows = Create(CopperArrows, 100);
            ItemTemplate equipment = Field<Dictionary<string, ItemTemplate>>(catalog, "_outcomes").Values
                .Where(template => !template.CanStack && template.Quality == arrows.Quality &&
                    (template.IsMelee || template.IsShield || template.IsShortBow || template.IsMediumBow || template.IsHeavyBow))
                .OrderBy(template => template.GUID, StringComparer.Ordinal).First();
            for (int index = 0; index < 10; index++) Create(equipment.GUID);
            DismantleUI ui = fixture.Open(catalog);
            SelectBulk(ui, arrows);
            yield return null;
            yield return null;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            ui.OpenBulkReview();
            reviewMs = timer.Elapsed.TotalMilliseconds;
            Quote[] entries = ui.BulkReviewEntries.ToArray();
            Require(entries.Length == 11 && entries.Single(entry => ReferenceEquals(entry.Source, arrows)).Plan.Consumed == 100,
                "Bulk benchmark did not capture ten equipment items and the complete ammunition stack.");
            var nativeList = Field<Awaken.TG.Main.Heroes.CharacterSheet.Items.Panel.ItemsUI>(Field<DismantleItemList>(ui, "_list"), "_itemsUI")
                .Element<Awaken.TG.Main.Heroes.CharacterSheet.Items.Panel.List.ItemsListUI>();
            var rows = nativeList.Elements<Awaken.TG.Main.Heroes.CharacterSheet.Items.Panel.List.ItemsListElementUI>()
                .GetManagedEnumerator().ToDictionary(row => row.Item, row => row);
            Require(entries.All(entry => rows.ContainsKey(entry.Source)), "Native benchmark rows are not initialized.");
            var total = System.Diagnostics.Stopwatch.StartNew();
            InvokeConfirm(ui);
            for (int tick = 0; ui.BulkProcessing && tick <= entries.Length + 1; tick++)
            {
                int previous = ui.BulkCompleted;
                timer.Restart();
                ui.Tick();
                double tickMs = timer.Elapsed.TotalMilliseconds;
                Require(ui.BulkCompleted >= previous && ui.BulkCompleted <= previous + 1, "One tick completed multiple queue entries.");
                Require(!nativeList.HasBeenDiscarded, "Bulk processing rebuilt the native inventory list.");
                Require(entries.Skip(ui.BulkCompleted).All(entry => !rows[entry.Source].HasBeenDiscarded),
                    "An unchanged pending row was rebuilt during bulk processing.");
                if (ui.BulkCompleted > previous)
                    Require(rows[entries[previous].Source].HasBeenDiscarded, "Consumed source retained its native inventory row.");
                samples.Add(new { tick, completed = ui.BulkCompleted, tickMs });
                yield return null;
            }
            totalMs = total.Elapsed.TotalMilliseconds;
            completed = ui.BulkCompleted;
            consumed = entries.Sum(entry => entry.Plan.Consumed);
            Require(!ui.BulkProcessing && completed == entries.Length && RecoveryRollCalls == entries.Length && !NativeInventory.Disabled,
                "Bulk benchmark did not complete its frozen queue exactly once.");
        }
        finally
        {
            CountRecoveryRolls = false;
            Testing = false;
        }
        Write("bulk-benchmark.json", new { status = "passed", version = typeof(Plugin.Plugin).Assembly.GetName().Version?.ToString(),
            entries = completed, units = consumed, reviewMs, totalMs, samples,
            detail = "Ten synthetic equipment items plus 100 arrows. Explicit ticks with native UI updates; total includes yielded render frames. Pending row identities preserved." });
    }

    private static string TestType(Item item) => item.Template.IsArmor ? "Armor" :
        item.Template.IsMelee || item.Template.IsShield || item.Template.IsArrow || item.Template.IsThrowable ||
        item.Template.IsShortBow || item.Template.IsMediumBow || item.Template.IsHeavyBow ? "Weapons" : "Other";

    private static void SelectBulk(DismantleUI ui, Item source)
    {
        ui.ChangeType(TestType(source));
        ui.SetRarity(source.Quality);
        Require(ui.BulkAvailable, "Synthetic filter selection did not enable bulk review.");
    }

    private static void InvokeConfirm(DismantleUI ui) => AccessTools.Method(typeof(DismantleUI), "Confirm").Invoke(ui, null);

    private static void FinishBulk(DismantleUI ui, int expectedEntries)
    {
        for (int tick = 0; ui.BulkProcessing && tick <= expectedEntries + 1; tick++) ui.Tick();
        Require(!ui.BulkProcessing && !NativeInventory.Disabled, "Bulk operation did not stop within its frozen queue length.");
    }

    private static void AttachSyntheticRelic(Item source)
    {
        ItemTemplate template = World.Services.Get<TemplatesProvider>().GetAllOfType<ItemTemplate>(TemplateTypeFlag.Regular)
            .Where(item => !item.IsAbstract && item.GetAttachment<GemAttachment>()?.Type == GemType.Weapon)
            .OrderBy(item => item.GUID, StringComparer.Ordinal).First();
        Item gem = Create(template.GUID);
        ItemGems sockets = source.TryGetElement<ItemGems>() ?? source.AddElement(new ItemGems(1, source.MaxGemSlots));
        if (!sockets.CanAttach) sockets.IncreaseLimit();
        Require(sockets.AttachGem(gem) != null && source.HasGemAttached, "Synthetic relic could not attach through native item sockets.");
    }

    private IEnumerator RunUiTests()
    {
        Require(!World.HasAny<DismantleUI>(), "Close the dismantling menu before running UI lifecycle tests.");
        OpenBench();
        var results = new List<TestResult>();
        var catalog = new GameCatalog();
        var baseline = new InventorySnapshot();
        DismantleUI? ui = null;
        string stage = "bench button supports incoming and outgoing horizontal navigation";
        Testing = true;
        try
        {
            yield return null;
            Canvas.ForceUpdateCanvases();
            CheckBenchNavigation();
            results.Add(new TestResult(stage, "passed", "Unity Selectable navigation graph only; no physical controller input."));
            stage = "selection and interrupted hold preserve inventory";
            Item source = Create(Shortsword);
            Item protectedSource = Create(Shortsword);
            protectedSource.ToggleFavourite();
            var unchanged = new InventorySnapshot();
            ui = World.Add(new DismantleUI(catalog));
            yield return null;
            ui.Select(source);
            Prompt confirm = Field<Prompt>(ui, "_confirm");
            Require(confirm.IsActive && confirm.PressType == IButton.PressType.Hold && Math.Abs(confirm.HoldTime - 0.65f) < 0.001f,
                "Eligible selection did not enable the configured hold prompt.");
            HoldEvent(confirm, "OnKeyDown");
            HoldEvent(confirm, "OnKeyHeld", 0.5f);
            Require(confirm.HoldPercent == 0.5f, "Native hold progress did not update.");
            ui.Select(null);
            Require(!confirm.IsActive && confirm.HoldPercent == 0, "Changing selection did not cancel the held confirmation.");
            confirm.InvokeCallback();
            unchanged.AssertUnchanged();
            results.Add(new TestResult(stage, "passed", "Native prompt events and callback; no physical input."));

            stage = "protected selection disables confirmation";
            ui.Select(protectedSource);
            Require(!confirm.IsActive, "Favourite item enabled the confirmation prompt.");
            confirm.InvokeCallback();
            unchanged.AssertUnchanged();
            results.Add(new TestResult(stage, "passed", ""));

            stage = "cancellation and three menu reopenings preserve inventory";
            ui.Close();
            yield return null;
            Require(ui.HasBeenDiscarded && !World.HasAny<DismantleUI>(), "Closed dismantling model remained registered.");
            for (int opening = 0; opening < 3; opening++)
            {
                ui = World.Add(new DismantleUI(catalog));
                yield return null;
                ui.Select(source);
                Require(Field<Prompt>(ui, "_confirm").IsActive, "Reopened menu lost its confirmation binding.");
                ui.Close();
                yield return null;
                Require(ui.HasBeenDiscarded && !World.HasAny<DismantleUI>(), "Reopened dismantling model did not close.");
                unchanged.AssertUnchanged();
            }
            results.Add(new TestResult(stage, "passed", ""));

            stage = "completed hold callback consumes once and preserves gold";
            ui = World.Add(new DismantleUI(catalog));
            yield return null;
            ui.Select(source);
            Quote quote = Field<Quote>(ui, "_quote");
            confirm = Field<Prompt>(ui, "_confirm");
            var counts = quote.Plan.Maximum.Keys.ToDictionary(id => id, id => CleanCount(catalog.Material(id)));
            float gold = Hero.Current.Wealth.BaseValue;
            HoldEvent(confirm, "OnKeyDown");
            HoldEvent(confirm, "OnKeyHeld", 1f);
            HoldEvent(confirm, "OnKeyUp", true);
            ((IButton)confirm).Invoke();
            Require(source.HasBeenDiscarded, "Held-confirm callback did not consume the selected synthetic source.");
            Require(!confirm.IsActive && !NativeInventory.Busy && !NativeInventory.Disabled, "Confirmation did not return to an idle state.");
            Require(Hero.Current.Wealth.BaseValue == gold, "UI confirmation changed gold.");
            foreach (var count in counts)
            {
                int delta = CleanCount(catalog.Material(count.Key)) - count.Value;
                Require(delta >= quote.Plan.Minimum[count.Key] && delta <= quote.Plan.Maximum[count.Key], "UI recovery differs from the displayed range.");
            }
            var committed = new InventorySnapshot();
            confirm.InvokeCallback();
            ((IButton)confirm).Invoke();
            committed.AssertUnchanged();
            results.Add(new TestResult(stage, "passed", "Native completed-hold callback path; repeated callback delivered no additional materials."));
            stage = "";
        }
        finally
        {
            if (stage.Length > 0) results.Add(new TestResult(stage, "failed", "Read last-command.json for the exception."));
            try
            {
                if (ui != null && !ui.HasBeenDiscarded) ui.Close();
                baseline.Dispose();
            }
            catch (Exception exception) { results.Add(new TestResult("fixture cleanup", "failed", exception.ToString())); }
            Testing = false;
            Write("ui-tests.json", new { utc = DateTime.UtcNow, results,
                passed = results.Count(result => result.Status == "passed"), failed = results.Count(result => result.Status == "failed"),
                limits = "Model and native prompt callbacks only. Physical keyboard, mouse, controller navigation, and visual scaling require separate checks." });
        }
        Require(results.All(result => result.Status != "failed"), "UI lifecycle tests failed. Read ui-tests.json.");
    }

    private static void HoldEvent(Prompt prompt, string name, params object[] arguments) =>
        AccessTools.Method(typeof(IButtonHold), name).Invoke(prompt, arguments);

    private IEnumerator LoadLatestFromTitle()
    {
        Require(Hero.Current == null && !LoadingScreenUI.IsLoading, "Load-latest requires an idle title screen.");
        float deadline = Time.realtimeSinceStartup + 120f;
        bool advancedPressStart = false;
        VTitleScreenUI? menu = null;
        while (Time.realtimeSinceStartup < deadline)
        {
            PressStartUI pressStart = World.Any<PressStartUI>();
            if (pressStart != null && !advancedPressStart)
            {
                VPressStartUI pressView = pressStart.View<VPressStartUI>();
                if (pressView != null)
                {
                    // Run the native animation and title initialization before invoking Continue.
                    pressView.Handle(new UIEKeyDown());
                    advancedPressStart = true;
                }
            }
            TitleScreenUI title = World.Any<TitleScreenUI>();
            menu = title?.Views.OfType<VTitleScreenUI>().FirstOrDefault();
            if (menu != null && menu.gameObject.activeInHierarchy && !World.HasAny<PressStartUI>()) break;
            yield return null;
        }
        Require(menu != null && menu.gameObject.activeInHierarchy, "Native title initialization did not finish. Resolve any title popup before loading.");
        SaveSlot latest = SaveSlot.LastSaveSlot ?? throw new InvalidOperationException("Copied save slots are not loaded yet.");
        ButtonConfig continueButton = Field<ButtonConfig>(menu!, "continueGame");
        Require(continueButton.button.IsActive() && continueButton.button.Interactable, "Native Continue button is unavailable.");
        Write("load-request.json", new { slot = latest.SaveFileName, flow = "Native PressStart transition and Continue callback", utc = DateTime.UtcNow });
        Field<Action>(continueButton.button, "OnClick").Invoke();
        deadline = Time.realtimeSinceStartup + 30f;
        while (!LoadingScreenUI.IsLoading && !World.HasAny<LoadingScreenUI>() && Hero.Current == null && Time.realtimeSinceStartup < deadline)
            yield return null;
        Require(LoadingScreenUI.IsLoading || World.HasAny<LoadingScreenUI>() || Hero.Current != null, "Native Continue did not start loading.");
    }

    private static void CheckBenchNavigation()
    {
        VUpgradesUI view = World.Any<UpgradesUI>().View<VUpgradesUI>();
        Transform parent = Field<ButtonConfig>(view, "upgradeButton").transform.parent;
        Transform added = parent.Find("AvalonDismantleButton")
            ?? throw new InvalidOperationException("Dismantle bench button is missing.");
        Selectable target = added.GetComponent<ButtonConfig>().button;
        Selectable? previous = null;
        for (int index = 0; index < added.GetSiblingIndex(); index++)
        {
            ButtonConfig candidate = parent.GetChild(index).GetComponent<ButtonConfig>();
            if (candidate != null && candidate.gameObject.activeInHierarchy && candidate.button.IsInteractable()) previous = candidate.button;
        }
        Require(previous != null, "No preceding native bench button exists.");
        Require(target.IsActive() && target.IsInteractable(), "Dismantle bench button is inactive or not interactable.");
        Require(ReferenceEquals(previous!.FindSelectableOnRight(), target), "Native button's right navigation cannot reach Dismantle.");
        Require(ReferenceEquals(target.FindSelectableOnLeft(), previous), "Dismantle's left navigation cannot return to the native button.");
    }

    private IEnumerator PreviewExistingItem()
    {
        Require(!World.HasAny<DismantleUI>() && !World.HasAny<CraftingTabsUI>(), "Run close-bench before preview-item.");
        var before = new InventorySnapshot();
        var catalog = new GameCatalog();
        var candidates = new List<Quote>();
        foreach (Item item in Hero.Current.HeroItems.Inventory)
        {
            if (catalog.Eligibility(item) != null) continue;
            try { candidates.Add(catalog.Quote(item)); }
            catch (InvalidOperationException) { /* An unavailable upgrade estimate cannot provide a preview. */ }
        }
        Quote? chosen = candidates.OrderBy(quote => quote.Source.Template.GUID == Hammer && quote.DisplayLevel > 0 ? 0 : !quote.IsExact ? 1 : 2)
            .ThenByDescending(quote => quote.DisplayLevel).ThenBy(quote => quote.Source.Template.GUID, StringComparer.Ordinal).FirstOrDefault();
        Require(chosen != null, "No existing carried item is eligible for a dismantling preview.");
        OpenBench();
        yield return null;
        VUpgradesUI bench = World.Any<UpgradesUI>().View<VUpgradesUI>();
        Transform parent = Field<ButtonConfig>(bench, "upgradeButton").transform.parent;
        ButtonConfig button = parent.Find("AvalonDismantleButton")?.GetComponent<ButtonConfig>()
            ?? throw new InvalidOperationException("Native Dismantle bench button is missing.");
        Field<Action>(button.button, "OnClick").Invoke();
        float deadline = Time.realtimeSinceStartup + 10f;
        DismantleUI? preview;
        while ((preview = World.Any<DismantleUI>()) == null || !preview.IsFullyInitialized)
        {
            Require(Time.realtimeSinceStartup < deadline, "Dismantle preview did not initialize before timeout.");
            yield return null;
        }
        yield return null;
        yield return null;
        preview.Select(chosen!.Source);
        yield return null;
        yield return null;
        Quote displayed = Field<Quote>(preview, "_quote");
        Require(ReferenceEquals(displayed.Source, chosen.Source), "Dismantle selection changed before the screenshot preview was ready.");
        before.AssertUnchanged();
        Write("item-preview.json", new
        {
            status = "passed",
            source = displayed.Source.Template.GUID,
            name = displayed.Source.DisplayName,
            level = displayed.DisplayLevel,
            recipe = displayed.Plan.Recipe.Id,
            exactMatch = displayed.IsExact,
            materialsBasedOn = displayed.MatchedName,
            consumed = displayed.Plan.Consumed,
            materials = displayed.Plan.Maximum.Select(entry => new { id = entry.Key, name = catalog.Material(entry.Key).ItemName,
                minimum = displayed.Plan.Minimum[entry.Key], maximum = entry.Value,
                upgrade = displayed.Plan.Upgrades.TryGetValue(entry.Key, out int quantity) ? quantity : 0 }).ToArray(),
            width = Screen.width,
            height = Screen.height,
            detail = "Existing copied-save item; native Dismantle button; preview only; inventory and wallets unchanged."
        });
    }

    private IEnumerator TestVanillaCrafting()
    {
        Require(!World.HasAny<DismantleUI>() && !World.HasAny<CraftingTabsUI>(), "Close existing crafting menus before the vanilla regression check.");
        OpenBench();
        using var baseline = new InventorySnapshot();
        DismantleUI? dismantle = null;
        CraftingTabsUI? crafting = null;
        bool passed = false;
        try
        {
            dismantle = World.Add(new DismantleUI(new GameCatalog()));
            yield return null;
            dismantle.Select(null);
            dismantle.Close();
            yield return null;
            Require(!World.HasAny<DismantleUI>(), "Dismantle remained open before native Craft.");
            VUpgradesUI bench = World.Any<UpgradesUI>().View<VUpgradesUI>();
            Field<Action>(Field<ButtonConfig>(bench, "craftButton").button, "OnClick").Invoke();
            float deadline = Time.realtimeSinceStartup + 15f;
            Handcrafting? handcrafting = null;
            RecipeGridUI? grid = null;
            VRecipeSlot? slotView = null;
            while (Time.realtimeSinceStartup < deadline)
            {
                crafting = World.Any<CraftingTabsUI>();
                if (crafting != null && crafting.FullyCreated && crafting.CurrentType == CraftingTabTypes.RecipeHandcrafting)
                {
                    handcrafting = crafting.TryGetElement<Handcrafting>();
                    grid = handcrafting?.TryGetElement<RecipeGridUI>();
                    RecipeTabContents? tab = grid?.TryGetElement<RecipeTabContents>();
                    slotView = tab?.RecipeHost.GetComponentsInChildren<VRecipeSlot>(false)
                        .FirstOrDefault(view => view.isActiveAndEnabled && view.Target != null && !view.Target.Recipe.IsHidden);
                    if (slotView != null) break;
                }
                yield return null;
            }
            Require(slotView != null && grid != null && handcrafting != null, "Native handcrafting did not display a learned recipe before timeout.");
            // Off-screen recipe models have no recyclable view and cannot select the native workbench.
            RecipeSlot slot = slotView!.Target;
            slot.UnselectSlot();
            slot.SelectSlot();
            deadline = Time.realtimeSinceStartup + 5f;
            while ((!ReferenceEquals(grid!.SelectedRecipe, slot.Recipe) || !ReferenceEquals(handcrafting!.CurrentRecipe, slot.Recipe)) &&
                Time.realtimeSinceStartup < deadline) yield return null;
            Require(ReferenceEquals(grid!.SelectedRecipe, slot.Recipe) && ReferenceEquals(handcrafting!.CurrentRecipe, slot.Recipe),
                "Native recipe selection did not update the crafting workbench.");
            baseline.AssertUnchanged();
            passed = true;
            Write("vanilla-crafting.json", new { status = "passed", station = crafting!.TemplateFromType().GUID,
                recipe = slot.Recipe.Outcome.GUID, name = slot.Recipe.Outcome.ItemName,
                detail = "Opened native Craft after Dismantle closed, selected a learned recipe, and preserved inventory and gold. No item crafted." });
        }
        finally
        {
            if (dismantle != null && !dismantle.HasBeenDiscarded) dismantle.Close();
            if (!passed && crafting != null && !crafting.HasBeenDiscarded) crafting.Close();
        }
    }

    private IEnumerator ChangeResolution(string command)
    {
        bool restoring = command == "resolution-restore";
        if (restoring && _originalWidth == 0) throw new InvalidOperationException("No temporary test resolution needs restoration.");
        if (_originalWidth == 0)
        {
            _originalWidth = Screen.width;
            _originalHeight = Screen.height;
            _originalScreenMode = Screen.fullScreenMode;
            _originalRefreshRate = Screen.currentResolution.refreshRateRatio;
        }
        int width = restoring ? _originalWidth : command == "resolution1080" ? 1920 : 3840;
        int height = restoring ? _originalHeight : command == "resolution1080" ? 1080 : 2160;
        Screen.SetResolution(width, height, _originalScreenMode, _originalRefreshRate);
        float deadline = Time.realtimeSinceStartup + 10f;
        do { yield return null; }
        while ((Screen.width != width || Screen.height != height) && Time.realtimeSinceStartup < deadline);
        yield return null;
        Write("resolution.json", new { command, requestedWidth = width, requestedHeight = height,
            actualWidth = Screen.width, actualHeight = Screen.height, mode = Screen.fullScreenMode.ToString() });
        Require(Screen.width == width && Screen.height == height, "Unity did not apply the requested test resolution.");
        if (restoring) _originalWidth = 0;
    }

    private void SavePersistenceTest()
    {
        Testing = true;
        try
        {
            var catalog = new GameCatalog();
            Item source = Create(Shortsword);
            Quote quote = catalog.Quote(source);
            IReadOnlyDictionary<string, int> returned = NativeInventory.Execute(catalog, quote);
            var expected = new PersistenceExpectation
            {
                MaterialCounts = returned.Keys.ToDictionary(id => id, id => CleanCount(catalog.Material(id))),
                Gold = Hero.Current.Wealth.BaseValue,
                Cobweb = Hero.Current.Cobweb.BaseValue
            };
            SaveSlot slot = SaveSlot.CreateAndSave("Avalon Dismantling isolated persistence test", isCustomName: true);
            expected.Slot = slot.SaveFileName;
            Write("persistence-expected.json", expected);
        }
        finally { Testing = false; }
    }

    private void VerifyPersistence()
    {
        PersistenceExpectation expected = JsonConvert.DeserializeObject<PersistenceExpectation>(File.ReadAllText(Path.Combine(Profile, "persistence-expected.json")))
            ?? throw new InvalidOperationException("Persistence expectations are missing.");
        TemplatesProvider provider = World.Services.Get<TemplatesProvider>();
        foreach (KeyValuePair<string, int> count in expected.MaterialCounts)
            Require(CleanCount(provider.Get<ItemTemplate>(count.Key)) == count.Value, "Saved material count differs after reload: " + count.Key);
        Require(Hero.Current.Wealth.BaseValue == expected.Gold && Hero.Current.Cobweb.BaseValue == expected.Cobweb, "Wallet differs after reload.");
        Write("persistence-result.json", new { status = "passed", expected.Slot, utc = DateTime.UtcNow });
    }

    private static int CleanCount(ItemTemplate template)
    {
        CurrencyAsCraftingIngredientAttachment currency = template.GetAttachment<CurrencyAsCraftingIngredientAttachment>();
        if (currency != null) return currency.Type == Awaken.TG.Main.Locations.Shops.CurrencyType.Cobweb ? Hero.Current.Cobweb.BaseInt : Hero.Current.Wealth.BaseInt;
        return Hero.Current.HeroItems.Items.Where(item => item.Template == template && !item.IsStolen).Sum(item => item.Quantity);
    }

    private static void OpenBench()
    {
        if (World.HasAny<UpgradesUI>()) return;
        HeroRecipes known = Hero.Current.Element<HeroRecipes>();
        HandcraftingTemplate station = World.Services.Get<TemplatesProvider>().GetAllOfType<HandcraftingTemplate>(TemplateTypeFlag.Regular)
            .Select(template => new { template, recipes = template.Recipes.Where(recipe => recipe?.Outcome != null).ToArray() })
            .Where(candidate => candidate.recipes.Length > 0)
            .OrderByDescending(candidate => candidate.recipes.Any(recipe => recipe.Outcome.IsMelee) && candidate.recipes.Any(recipe => recipe.Outcome.IsArmor))
            .ThenByDescending(candidate => candidate.recipes.Count(recipe => !recipe.IsHidden && known.IsLearned(recipe)))
            .ThenBy(candidate => candidate.template.GUID, StringComparer.Ordinal).First().template;
        World.Add(new UpgradesUI(new TabSetConfig(new Dictionary<CraftingTabTypes, CraftingTemplate>
        {
            [CraftingTabTypes.RecipeHandcrafting] = station
        })));
    }

    private static bool HeroReady() => Hero.Current != null && Hero.Current.IsFullyInitialized && !Hero.Current.HasBeenDiscarded &&
        !LoadingScreenUI.IsLoading && !World.HasAny<LoadingScreenUI>() &&
        World.Services.Get<TemplatesProvider>().AllLoaded;

    private static bool RequiresHero(string command) => command != "status" && command != "load-latest" && command != "quit" && command != "screenshot";

    private static bool IsBelow(string path, string root) => path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string TestArchivePath() => Path.Combine(Cache, "SaveFiles.zip");

    private static void AssertIsolation()
    {
        Require(CloudService.Get is DebugCloudService, "Runtime is not using the isolated local save service.");
        Require(Path.GetFullPath(CloudService.Get.DataPath).TrimEnd(Path.DirectorySeparatorChar) == Profile, "Save profile isolation changed.");
        Require(File.ReadAllText(Path.Combine(Profile, MarkerName)).Trim() == MarkerContent, "Isolation marker was removed.");
    }

    private static void SetFault(Fault mode, GameCatalog catalog, Quote quote)
    {
        FaultMode = mode;
        FaultTriggered = false;
        ReentryRejected = false;
        FaultGuid = quote.Plan.PerBatch.Keys.Last(id => catalog.Material(id).GetAttachment<CurrencyAsCraftingIngredientAttachment>() == null);
        FaultQuote = quote;
        FaultCatalog = catalog;
        FaultExisting = quote.Source;
    }

    private void WriteStatus() => Write("status.json", new
    {
        utc = DateTime.UtcNow,
        isolated = CloudService.Get is DebugCloudService,
        profile = CloudService.Get.DataPath,
        heroReady = HeroReady(),
        loading = LoadingScreenUI.IsLoading || World.HasAny<LoadingScreenUI>(),
        width = Screen.width,
        height = Screen.height,
        hero = Hero.Current?.ID,
        gold = Hero.Current?.Wealth?.BaseValue,
        cobweb = Hero.Current?.Cobweb?.BaseValue,
        benchOpen = World.HasAny<UpgradesUI>(),
        dismantlingOpen = World.HasAny<DismantleUI>(),
        inventoryDisabled = NativeInventory.Disabled,
        saveFiles = Directory.GetFiles(Path.Combine(Profile, "Saved"), "*.zip").Select(Path.GetFileName).ToArray()
    });

    private void WriteReadiness(string command) => Write("readiness.json", new
    {
        command,
        status = "waiting-for-gameplay",
        utc = DateTime.UtcNow,
        heroExists = Hero.Current != null,
        heroInitialized = Hero.Current?.IsFullyInitialized,
        nativeLoadingFlag = LoadingScreenUI.IsLoading,
        loadingScreenExists = World.HasAny<LoadingScreenUI>(),
        loadingScreenFpsStable = World.Any<LoadingScreenUI>()?.IsFPSStable,
        titleScreenExists = World.HasAny<TitleScreenUI>(),
        pressStartExists = World.HasAny<PressStartUI>()
    });

    private void Write(string name, object value) => File.WriteAllText(Path.Combine(Profile, name), JsonConvert.SerializeObject(value, Formatting.Indented));

    private void LogFailure(string command, Exception exception)
    {
        Logger.LogError("Harness command failed: " + command + ": " + exception);
        if (Profile.Length > 0) Write("last-command.json", new { command, status = "failed", error = exception.ToString(), utc = DateTime.UtcNow });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestResult
    {
        public string Name { get; }
        public string Status { get; }
        public string Detail { get; }
        public TestResult(string name, string status, string detail) { Name = name; Status = status; Detail = detail; }
    }

    private sealed class PersistenceExpectation
    {
        public string Slot { get; set; } = "";
        public Dictionary<string, int> MaterialCounts { get; set; } = new Dictionary<string, int>();
        public float Gold { get; set; }
        public float Cobweb { get; set; }
    }

    private sealed class BulkFixture : IDisposable
    {
        private readonly InventorySnapshot _snapshot = new InventorySnapshot();
        private readonly List<LockItemSlot> _locks = new List<LockItemSlot>();
        private DismantleUI? _ui;

        public BulkFixture(GameCatalog catalog)
        {
            try
            {
                foreach (Item item in Hero.Current.HeroItems.Inventory.Where(item => catalog.Eligibility(item) == null).ToArray())
                    _locks.Add(item.AddElement(new LockItemSlot()));
            }
            catch { Dispose(); throw; }
        }

        public DismantleUI Open(GameCatalog catalog)
        {
            Require(_ui == null, "A bulk fixture can own only one menu.");
            _ui = World.Add(new DismantleUI(catalog));
            Field<VDismantleUI>(_ui, "_view").enabled = false;
            return _ui;
        }

        public void Dispose()
        {
            try { if (_ui != null && !_ui.HasBeenDiscarded) _ui.Discard(); }
            finally
            {
                try { foreach (LockItemSlot itemLock in _locks) if (!itemLock.HasBeenDiscarded) itemLock.Discard(); }
                finally { _snapshot.Dispose(); }
            }
        }
    }

    private sealed class RecipeFixture : IDisposable
    {
        private readonly Awaken.TG.Main.Crafting.Recipes.BaseRecipe _definition;
        private readonly FieldInfo _ingredients = AccessTools.Field(typeof(Awaken.TG.Main.Crafting.Recipes.BaseRecipe), "ingredients");
        private readonly Awaken.TG.Main.Crafting.Recipes.Ingredient[] _original;

        public RecipeFixture(GameCatalog catalog, Quote quote, bool selfIngredient)
        {
            _definition = Field<Dictionary<string, Awaken.TG.Main.Crafting.Recipes.BaseRecipe>>(catalog, "_definitions")[quote.Plan.Recipe.Id];
            _original = (Awaken.TG.Main.Crafting.Recipes.Ingredient[])_ingredients.GetValue(_definition);
            var count = AccessTools.Field(typeof(Awaken.TG.Main.Crafting.Recipes.Ingredient), "count");
            if (selfIngredient)
            {
                var ingredient = new Awaken.TG.Main.Crafting.Recipes.Ingredient { templateReference = new TemplateReference(quote.Source.Template) };
                count.SetValue(ingredient, 1);
                _ingredients.SetValue(_definition, new[] { ingredient });
            }
            else
            {
                var changed = _original.ToArray();
                int index = Array.FindIndex(changed, ingredient => ingredient.Template.GetAttachment<CurrencyAsCraftingIngredientAttachment>()?.Type != CurrencyType.Money);
                Require(index >= 0, "Recipe has no material ingredient to change.");
                var ingredient = new Awaken.TG.Main.Crafting.Recipes.Ingredient { templateReference = changed[index].templateReference };
                count.SetValue(ingredient, checked((int)count.GetValue(changed[index]) + 1));
                changed[index] = ingredient;
                _ingredients.SetValue(_definition, changed);
            }
        }

        public void Dispose() => _ingredients.SetValue(_definition, _original);
    }

    private sealed class InventorySnapshot : IDisposable
    {
        private readonly HeroItems _inventory = Hero.Current.HeroItems;
        private readonly Dictionary<Item, int> _counts;
        private readonly HashSet<string> _known;
        private readonly float _gold = Hero.Current.Wealth.BaseValue;
        private readonly float _cobweb = Hero.Current.Cobweb.BaseValue;

        public InventorySnapshot()
        {
            _counts = _inventory.Items.Where(item => !item.HasElement<CurrencyAsCraftingIngredient>()).ToDictionary(item => item, item => item.Quantity);
            _known = new HashSet<string>(_inventory.KnownItems);
        }

        public void AssertUnchanged()
        {
            Require(Hero.Current.Wealth.BaseValue == _gold && Hero.Current.Cobweb.BaseValue == _cobweb, "Wallet changed during a cancelled operation.");
            Item[] current = _inventory.Items.Where(item => !item.HasElement<CurrencyAsCraftingIngredient>()).ToArray();
            Require(current.Length == _counts.Count && current.All(item => _counts.TryGetValue(item, out int count) && count == item.Quantity), "Inventory objects or quantities changed during cancellation.");
            Require(_known.SetEquals(_inventory.KnownItems), "Known item state changed during cancellation.");
        }

        public void Dispose()
        {
            foreach (Item item in _inventory.Items.Where(item => !item.HasElement<CurrencyAsCraftingIngredient>() && !_counts.ContainsKey(item)).ToArray())
                _inventory.Remove(item, discard: true, announce: false);
            foreach (KeyValuePair<Item, int> item in _counts)
            {
                Require(!item.Key.HasBeenDiscarded && _inventory.Items.Contains(item.Key), "A test changed an original copied-profile item irreversibly.");
                if (item.Key.Quantity != item.Value) item.Key.SetQuantity(item.Value);
            }
            Hero.Current.Wealth.SetTo(_gold, runHooks: false);
            Hero.Current.Cobweb.SetTo(_cobweb, runHooks: false);
            foreach (string guid in _inventory.KnownItems.Where(guid => !_known.Contains(guid)).ToArray()) _inventory.KnownItems.Remove(guid);
            AssertUnchanged();
        }
    }

    [HarmonyPatch(typeof(CloudService), "CreateCloudService")]
    private static class LocalService
    {
        private static bool Prefix(ref CloudService __result) { __result = new DebugCloudService(); return false; }
    }

    [HarmonyPatch(typeof(CloudService), "get_DataPath")]
    private static class ProfilePath
    {
        private static bool Prefix(ref string __result) { __result = Profile; return false; }
    }

    [HarmonyPatch(typeof(LoadSaveDomainsCache), "get_CachedDomainsDirectoryPath")]
    private static class DomainCachePath
    {
        private static bool Prefix(ref string __result) { __result = Path.Combine(Cache, "CachedDomains"); return false; }
    }

    [HarmonyPatch]
    private static class ArchiveCachePath
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in new[] { "CreateZipArchive", "MoveArchiveFromTemp", "GetSaveSlotDataLength" })
                yield return AccessTools.Method(typeof(ZipArchiveUtils), name) ?? throw new MissingMethodException(typeof(ZipArchiveUtils).FullName, name);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replacements = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldsfld && instruction.operand is FieldInfo field &&
                    field.DeclaringType == typeof(ZipArchiveUtils) && field.Name == nameof(ZipArchiveUtils.ZipPath))
                {
                    // Replace the actual consumer; Mono may embed a readonly field before reflection changes it.
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(Harness), nameof(TestArchivePath));
                    replacements++;
                }
                yield return instruction;
            }
            Require(replacements > 0, "The save ZIP method no longer reads the expected cache-path field.");
        }
    }

    [HarmonyPatch(typeof(SaveFileMigrationToSteam.DefaultSaveMigration), nameof(SaveFileMigrationToSteam.DefaultSaveMigration.FilesExist))]
    private static class NoDefaultMigration
    {
        private static bool Prefix(ref bool __result) { __result = false; return false; }
    }

    [HarmonyPatch(typeof(SaveFileMigrationToSteam.GoGSaveMigration), nameof(SaveFileMigrationToSteam.GoGSaveMigration.FilesExist))]
    private static class NoGogMigration
    {
        private static bool Prefix(ref bool __result) { __result = false; return false; }
    }

    [HarmonyPatch(typeof(Patcher115_120), nameof(Patcher115_120.StartGamePatch))]
    private static class NoLegacyCacheMigration
    {
        private static bool Prefix(ref UniTask __result) { __result = UniTask.CompletedTask; return false; }
    }

    [HarmonyPatch(typeof(HeroItems), "EquipPickedArrows")]
    private static class NoTestArrowEquip { private static bool Prefix() => !Testing; }

    [HarmonyPatch(typeof(HeroItems), "EquipPickedFood")]
    private static class NoTestFoodEquip { private static bool Prefix() => !Testing; }

    [HarmonyPatch(typeof(RecoveryPlan), nameof(RecoveryPlan.Roll))]
    private static class ObserveRecoveryRolls
    {
        private static void Prefix() { if (CountRecoveryRolls) RecoveryRollCalls++; }
    }

    [HarmonyPatch(typeof(HeroItems), nameof(HeroItems.Add))]
    private static class InjectInventoryFault
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(ref Item item, ref Item __result)
        {
            if (!NativeInventory.Busy || FaultMode == Fault.None || FaultTriggered || item.Template.GUID != FaultGuid) return true;
            if (FaultMode == Fault.ThrowAfter) return true;
            FaultTriggered = true;
            switch (FaultMode)
            {
                case Fault.Reject: __result = item; return false;
                case Fault.Existing: __result = FaultExisting!; return false;
                case Fault.Replace: item = new Item(FaultExisting!.Template, 1); return true;
                case Fault.Reenter:
                    try { NativeInventory.Execute(FaultCatalog!, FaultQuote!); }
                    catch (InvalidOperationException) { ReentryRejected = true; }
                    return true;
                default: return true;
            }
        }

        private static void Postfix(Item item)
        {
            if (NativeInventory.Busy && FaultMode == Fault.ThrowAfter && !FaultTriggered && item.Template.GUID == FaultGuid)
            {
                FaultTriggered = true;
                throw new InvalidOperationException("Injected post-add failure for isolated rollback validation.");
            }
        }
    }
}
