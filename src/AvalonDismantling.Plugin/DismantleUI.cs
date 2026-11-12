using System;
using System.Collections.Generic;
using System.Linq;
using AvalonDismantling.Core;
using Awaken.TG.Main.Character;
using Awaken.TG.MVC;
using Awaken.TG.MVC.Domains;
using Awaken.TG.MVC.UI.Handlers.States;
using Awaken.TG.MVC.UI.Universal;
using Awaken.TG.Main.Heroes;
using Awaken.TG.Main.Heroes.CharacterSheet.Items.Choose;
using Awaken.TG.Main.Heroes.CharacterSheet.Items.Panel.List;
using Awaken.TG.Main.Heroes.CharacterSheet.Items.Panel.Slot;
using Awaken.TG.Main.Heroes.CharacterSheet.Items.Panel.Tabs;
using Awaken.TG.Main.Heroes.Items;
using Awaken.TG.Main.Heroes.Items.Tooltips;
using Awaken.TG.Main.Heroes.Items.Tooltips.Descriptors;
using Awaken.TG.Main.Heroes.Items.Tooltips.Views;
using Awaken.TG.Main.Locations.Gems;
using Awaken.TG.Main.UI.ButtonSystem;
using Awaken.TG.Main.UI.Components.PadShortcuts;
using Awaken.TG.Main.UI.HUD;
using Awaken.TG.Main.Utility;
using Awaken.TG.Main.Utility.UI;
using Awaken.TG.Main.Utility.UI.Keys;
using Awaken.TG.Utility;
using Awaken.Utility.Animations;
using Awaken.Utility.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AvalonDismantling.Plugin;

internal sealed class DismantleUI : Model, IUIStateSource, IItemChooseParent, IPromptHost, IClosable
{
    private GameCatalog _catalog;
    private VDismantleUI _view = null!;
    private DismantleItemList _list = null!;
    private ItemTooltipUI _tooltip = null!;
    private Prompt _confirm = null!;
    private Prompt _bulkPrompt = null!;
    private Prompt _back = null!;
    private readonly Dictionary<Item, Quote> _eligible = new Dictionary<Item, Quote>();
    private readonly HashSet<Item> _observed = new HashSet<Item>();
    private Item[] _visible = Array.Empty<Item>();
    private BulkEntry[] _review = Array.Empty<BulkEntry>();
    private readonly Dictionary<string, int> _bulkReturns = new Dictionary<string, int>(StringComparer.Ordinal);
    private bool _dirty;
    private bool _cancelRequested;
    internal string SelectedType { get; private set; } = "All";
    internal ItemQuality? SelectedRarity { get; private set; }
    internal bool BulkReviewing { get; private set; }
    internal bool BulkProcessing { get; private set; }
    internal int BulkCompleted { get; private set; }
    internal Quote[] BulkReviewEntries => _review.Select(entry => entry.Quote).ToArray();
    internal bool BulkAvailable => !_busy && !BulkReviewing &&
        DismantleFilters.BulkAvailable(SelectedType, SelectedRarity?.EnumName, _visible.Length);
    private Item? _selected;
    private ExistingItemDescriptor? _selectedDescriptor;
    private Quote? _quote;
    private bool _busy;
    private string _lastResult = "";

    public override Domain DefaultDomain => Domain.Gameplay;
    public override bool IsNotSaved => true;
    public UIState UIState => UIState.ModalState(HUDState.EverythingHidden).WithPauseTime();
    public IEnumerable<Item> PossibleItems => _visible;
    public Transform ChooseHost => _view.InventoryHost;
    public Transform PromptsHost => _view.FooterHost;
    public Prompts Prompts { get; private set; } = null!;

    internal DismantleUI(GameCatalog catalog) => _catalog = catalog;

    protected override void OnInitialize()
    {
        World.SpawnView<VModalBlocker>(this);
        var prefab = Resources.Load<GameObject>("Prefabs/MapViews/Gems/Layout_DismantleUI")
            ?? throw new InvalidOperationException("The retained dismantling layout is missing.");
        GameObject clone = Object.Instantiate(prefab, World.Services.Get<ViewHosting>().OnMainCanvas(), false);
        try
        {
            clone.name = "AvalonDismantling";
            foreach (var price in clone.GetComponentsInChildren<VCServicePricePreview>(true))
                Object.DestroyImmediate(price);
            _view = clone.AddComponent<VDismantleUI>();
            World.BindView(this, _view, isMainView: true, removeAutomatically: true);
        }
        catch
        {
            Object.Destroy(clone);
            throw;
        }
    }

    protected override void OnFullyInitialized()
    {
        Prompts = AddElement(new Prompts(this));
        _back = Prompts.AddPrompt(Prompt.Tap(KeyBindings.UI.Generic.Cancel, "Back", Close, Prompt.Position.Last), this);
        _confirm = Prompts.BindPrompt(Prompt.Hold(KeyBindings.UI.Crafting.CraftOne, "Dismantle", Confirm,
            holdTime: 0.65f), this, _view.ConfirmView, active: false);
        _tooltip = AddElement(new ItemTooltipUI(typeof(VStaticSingleItemTooltipSystemUI), _view.TooltipHost,
            isStatic: true, comparerActive: false, prompts: Prompts));
        _bulkPrompt = Prompts.AddPrompt(Prompt.Tap(KeyBindings.UI.Crafting.CraftMany, "Dismantle all", OpenBulkReview), this);
        ModelExtensions.ListenTo(Hero.Current.HeroItems, Model.Events.AfterChanged, MarkDirty, this);
        ModelExtensions.ListenTo(Hero.Current.HeroItems, ICharacterInventory.Events.AfterEquipmentChanged, MarkDirty, this);
        ModelExtensions.ListenTo(Hero.Current.HeroItems, ICharacterInventory.Events.PickedUpItem, MarkDirty, this);
        RebuildSnapshot();
        _list = AddElement(new DismantleItemList());
        _view.SetStatus(_selected != null ? "" : PossibleItems.Any()
            ? "Select an item to see material recovery." : "No items available to dismantle.");
        _list.FocusFirst();
    }

    internal static string Category(Item item) => item.IsArmor ? "Armor" :
        item.Template.IsMelee || item.IsShield || item.IsArrow || item.IsThrowable ||
        item.Template.IsShortBow || item.Template.IsMediumBow || item.Template.IsHeavyBow ? "Weapons" : "Other";

    private static FilterItem FilterValue(Item item) => new FilterItem(Category(item), item.Quality.EnumName);
    private void MarkDirty() => _dirty = true;

    internal void RebuildSnapshot()
    {
        if (_busy) { _dirty = true; return; }
        _dirty = false;
        _eligible.Clear();
        foreach (Item item in Hero.Current.HeroItems.Items.ToArray())
        {
            if (_observed.Add(item))
            {
                ModelExtensions.ListenTo(item, Model.Events.AfterChanged, MarkDirty, this);
                ModelExtensions.ListenTo(item, Model.Events.AfterElementsCollectionModified, MarkDirty, this);
                ModelExtensions.ListenTo(item, Model.Events.AfterDiscarded, MarkDirty, this);
                ModelExtensions.ListenTo(item, Item.Events.FavouriteChanged, MarkDirty, this);
                ModelExtensions.ListenTo(item, Item.Events.QuantityChanged, MarkDirty, this);
            }
            try
            {
                if (_catalog.Eligibility(item) != null) continue;
                Quote quote = _catalog.Quote(item);
                _catalog.Revalidate(quote.Plan.Recipe);
                _eligible.Add(item, quote);
            }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is OverflowException)
            { /* An unavailable quote is excluded from this temporary menu snapshot. */ }
        }
        ApplyFilters(false);
    }

    private void ApplyFilters(bool forceRebuild)
    {
        Item[] old = _visible;
        var values = _eligible.Keys.Select(FilterValue).ToArray();
        var normalized = DismantleFilters.Normalize(values, SelectedType, SelectedRarity?.EnumName);
        SelectedType = normalized.Type;
        SelectedRarity = normalized.Rarity == null ? null : _eligible.Keys.Select(item => item.Quality)
            .First(quality => quality.EnumName == normalized.Rarity);
        _visible = _eligible.Keys.Where(item => DismantleFilters.Matches(FilterValue(item), SelectedType, SelectedRarity?.EnumName)).ToArray();
        var rarities = _eligible.Keys.Where(item => DismantleFilters.Matches(FilterValue(item), SelectedType, null))
            .Select(item => item.Quality).Distinct().OrderBy(quality => quality.Priority).ToArray();
        _view.SetFilters(DismantleFilters.AvailableTypes(values), SelectedType, rarities, SelectedRarity, !_busy && !BulkReviewing);
        if (_list != null)
        {
            if (forceRebuild || (old.Length == 0 && _visible.Length > 0))
            {
                _list.Discard();
                _list = AddElement(new DismantleItemList());
                _list.FocusFirst();
            }
            else _list.Refresh();
        }
        if (_selected != null && !_visible.Contains(_selected)) Select(null);
        if (_visible.Length == 0)
        {
            _view.SetDetails("No items available to dismantle.", "Change a filter or return to the bench.");
            if (_lastResult.Length == 0) _view.SetStatus("");
        }
        _bulkPrompt.SetActive(BulkAvailable);
        _bulkPrompt.SetVisible(BulkAvailable);
    }

    internal void ChangeType(string type)
    {
        if (_busy || BulkReviewing || !DismantleFilters.AvailableTypes(_eligible.Keys.Select(FilterValue)).Contains(type)) return;
        SelectedType = type;
        Select(null);
        ApplyFilters(true);
    }

    internal void SetRarity(ItemQuality? quality)
    {
        if (_busy || BulkReviewing || (quality != null && !_eligible.Keys.Any(item =>
            DismantleFilters.Matches(FilterValue(item), SelectedType, quality.EnumName)))) return;
        SelectedRarity = quality;
        Select(null);
        ApplyFilters(true);
    }

    internal void CycleRarity()
    {
        ItemQuality?[] choices = new ItemQuality?[] { null }.Concat(_eligible.Keys
            .Where(item => DismantleFilters.Matches(FilterValue(item), SelectedType, null)).Select(item => item.Quality)
            .Distinct().OrderBy(quality => quality.Priority)).ToArray();
        int current = Array.IndexOf(choices, SelectedRarity);
        SetRarity(choices[(current + 1) % choices.Length]);
    }

    private sealed class BulkEntry
    {
        internal Quote Quote { get; }
        internal ItemQuality Quality { get; }
        internal string Type { get; }
        internal BulkEntry(Quote quote) { Quote = quote; Quality = quote.Source.Quality; Type = Category(quote.Source); }
    }

    internal void OpenBulkReview()
    {
        if (!BulkAvailable) return;
        RebuildSnapshot();
        if (!BulkAvailable) return;
        try
        {
            _review = _visible.Select(item =>
            {
                Quote single = _eligible[item];
                int batches = DismantleFilters.WholeBatches(item.Quantity, single.Plan.Recipe.Quantity);
                return new BulkEntry(_catalog.Quote(item, batches));
            }).ToArray();
            var totals = new BulkRecovery(_review.Select(entry => entry.Quote.Plan));
            Select(null);
            BulkCompleted = 0;
            BulkReviewing = true;
            string entries = string.Join("\n\n", _review.Select(entry =>
            {
                Quote q = entry.Quote;
                string comparison = q.IsExact ? "Exact recipe" : "Materials based on: " + q.MatchedName;
                return $"{q.Source.DisplayName} | {q.Source.Quality.DisplayName}\nConsume {q.Plan.Consumed}; upgrade level {q.DisplayLevel}; {comparison}" +
                    (q.Source.IsArrow || q.Source.IsThrowable ? $"\n{q.Plan.Batches} complete batches; retain {q.SourceQuantity - q.Plan.Consumed}" : "");
            }));
            string materials = string.Join("\n", totals.Maximum.Select(p => $"{totals.Minimum[p.Key]}–{p.Value} {_catalog.Material(p.Key).ItemName}"));
            _view.ShowBulkReview($"DISMANTLE ALL — {SelectedType} / {SelectedRarity!.DisplayName}\n\n" +
                $"{_review.Length} inventory entries; {totals.Consumed} units consumed. Gold cost: 0.\n" +
                "Cancel before confirmation to keep everything. After starting, cancellation keeps completed dismantles.\n\n" +
                entries + "\n\nTOTAL MATERIAL RETURN RANGES\n" + materials);
            _view.SetStatus("Review every item, then hold the confirmation control.");
            UpdateBulkControls();
        }
        catch (Exception error)
        {
            _review = Array.Empty<BulkEntry>();
            BulkReviewing = false;
            _view.HideBulkReview();
            _view.SetStatus("Cannot prepare bulk dismantling: " + error.Message);
            UpdateBulkControls();
        }
    }

    private void UpdateBulkControls()
    {
        _confirm.OnHoldInterrupted();
        _confirm.ChangeName(BulkReviewing ? "Confirm dismantle all" : "Dismantle");
        _confirm.SetActive(BulkReviewing && !_busy);
        _bulkPrompt.SetActive(BulkAvailable);
        _bulkPrompt.SetVisible(BulkAvailable);
        _back.ChangeName(BulkProcessing ? "Stop after current item" : BulkReviewing ? "Cancel review" : "Back");
        _view.SetProcessing(_busy);
        _view.SetFilters(DismantleFilters.AvailableTypes(_eligible.Keys.Select(FilterValue)), SelectedType,
            _eligible.Keys.Where(item => DismantleFilters.Matches(FilterValue(item), SelectedType, null))
                .Select(item => item.Quality).Distinct().OrderBy(quality => quality.Priority).ToArray(), SelectedRarity, !_busy && !BulkReviewing);
    }

    private void ValidateEntry(BulkEntry entry)
    {
        Quote prior = entry.Quote;
        _catalog.Revalidate(prior.Plan.Recipe);
        Quote current = _catalog.Quote(prior.Source, prior.Plan.Batches);
        if (Category(prior.Source) != entry.Type || prior.Source.Quality != entry.Quality ||
            current.SourceQuantity != prior.SourceQuantity || current.DisplayLevel != prior.DisplayLevel ||
            current.Plan.Recipe.Id != prior.Plan.Recipe.Id || !current.Plan.PerBatch.SequenceEqual(prior.Plan.PerBatch))
            throw new InvalidOperationException("An item or recipe changed after the review.");
    }

    private void StartBulk()
    {
        try { foreach (BulkEntry entry in _review) ValidateEntry(entry); }
        catch (Exception error)
        {
            BulkReviewing = false;
            _view.HideBulkReview();
            try
            {
                _catalog = new GameCatalog();
                RebuildSnapshot();
                OpenBulkReview();
                _view.SetStatus((BulkReviewing ? "Review updated; confirm again. " : "Bulk review is no longer available. ") + error.Message);
            }
            catch (Exception refreshError)
            {
                _view.SetStatus("Cannot refresh the review. Reopen the menu. " + refreshError.Message);
            }
            UpdateBulkControls();
            return;
        }
        BulkProcessing = true;
        _busy = true;
        _cancelRequested = false;
        _bulkReturns.Clear();
        UpdateBulkControls();
        _view.SetBulkProgress($"Dismantling 0 / {_review.Length}. Completed dismantles are kept if you stop.");
    }

    internal void Tick()
    {
        if (HasBeenDiscarded || !IsFullyInitialized) return;
        if (!BulkProcessing)
        {
            if (_dirty && !BulkReviewing && !_busy) RebuildSnapshot();
            return;
        }
        if (_cancelRequested || BulkCompleted == _review.Length)
        {
            FinishBulk(BulkCompleted == _review.Length ? "Complete." : "Stopped by player.");
            return;
        }
        try
        {
            BulkEntry entry = _review[BulkCompleted];
            ValidateEntry(entry);
            IReadOnlyDictionary<string, int> returned = NativeInventory.Execute(_catalog, entry.Quote);
            foreach (var p in returned)
            {
                _bulkReturns.TryGetValue(p.Key, out int count);
                _bulkReturns[p.Key] = checked(count + p.Value);
            }
            BulkCompleted++;
            _view.SetBulkProgress($"Dismantling {BulkCompleted} / {_review.Length}. Completed dismantles are kept if you stop.");
            if (NativeInventory.Disabled) FinishBulk("Stopped after an inventory callback error. Review the log before saving.");
        }
        catch (Exception error)
        {
            Plugin.Log.LogError("Bulk dismantling stopped: " + error);
            FinishBulk(error.Message);
        }
    }

    private void FinishBulk(string reason)
    {
        BulkProcessing = false;
        BulkReviewing = false;
        _busy = false;
        _view.HideBulkReview();
        _lastResult = $"{reason} Dismantled {BulkCompleted} / {_review.Length} entries; {_review.Length - BulkCompleted} remaining.\nRecovered: " +
            (_bulkReturns.Count == 0 ? "none" : string.Join(", ", _bulkReturns.Select(p => $"{p.Value} {_catalog.Material(p.Key).ItemName}")));
        _view.SetStatus(_lastResult);
        _view.SetDetails("Bulk dismantling finished", "Select an item or change filters to continue.");
        UpdateBulkControls();
        RebuildSnapshot();
    }

    internal void Select(Item? item)
    {
        if (_busy || BulkReviewing || HasBeenDiscarded) return;
        _view.SetStatus(_lastResult);
        _confirm.OnHoldInterrupted();
        _confirm.SetActive(false);
        _quote = null;
        _selected = item;
        _view.ClearIngredients();
        _selectedDescriptor = item != null && !item.HasBeenDiscarded ? new ExistingItemDescriptor(item) : null;
        _tooltip.SetDescriptor(_selectedDescriptor);
        if (item == null || item.HasBeenDiscarded)
        {
            _view.SetDetails("Select an item", "");
            return;
        }
        try
        {
            string? reason = _catalog.Eligibility(item);
            if (reason != null)
            {
                _view.SetDetails(item.DisplayName, reason);
                return;
            }
            _quote = _catalog.Quote(item);
            _view.ShowQuote(_catalog, _quote);
            _confirm.SetActive(true);
        }
        catch (Exception error)
        {
            Plugin.Log.LogWarning("Could not preview dismantling: " + error);
            _view.SetDetails(item.DisplayName, error.Message);
        }
    }

    private void Confirm()
    {
        if (!_busy && BulkReviewing) { StartBulk(); return; }
        if (_busy || _quote == null || HasBeenDiscarded) return;
        Quote quote = _quote;
        _busy = true;
        _confirm.SetActive(false);
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyDictionary<string, int> recovered = NativeInventory.Execute(_catalog, quote);
            double transactionMs = timer.Elapsed.TotalMilliseconds;
            string result = "Recovered: " + string.Join(", ", recovered.Select(entry => $"{entry.Value} {_catalog.Material(entry.Key).ItemName}"));
            _lastResult = result;
            _quote = null;
            _selected = null;
            _selectedDescriptor = null;
            _view.ClearIngredients();
            _tooltip.SetDescriptor(null);
            _view.SetDetails("Dismantling complete", "Select another item to continue.");
            _view.SetStatus(result);
            double presentationMs = timer.Elapsed.TotalMilliseconds - transactionMs;
            timer.Restart();
            _busy = false;
            RebuildSnapshot();
            Plugin.Log.LogInfo($"Dismantling timings: transaction={transactionMs:F1} ms; result={presentationMs:F1} ms; refresh={timer.Elapsed.TotalMilliseconds:F1} ms.");
        }
        catch (Exception error)
        {
            _quote = null;
            _lastResult = "";
            Plugin.Log.LogError("Dismantling did not complete: " + error);
            _view.SetStatus("Dismantling stopped: " + error.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    public void Close()
    {
        if (BulkProcessing) { _cancelRequested = true; return; }
        if (BulkReviewing)
        {
            BulkReviewing = false;
            _review = Array.Empty<BulkEntry>();
            _view.HideBulkReview();
            _view.SetStatus("Bulk review cancelled. No items dismantled.");
            UpdateBulkControls();
            RebuildSnapshot();
            return;
        }
        if (!_busy) Discard();
    }

    internal void PreserveSelectedTooltip()
    {
        if (_selected != null && !_selected.HasBeenDiscarded && _tooltip != null &&
            _tooltip.Descriptor != _selectedDescriptor)
            _tooltip.SetDescriptor(_selectedDescriptor);
    }
}

internal sealed class DismantleItemList : ItemChooseUI<DismantleUI>
{
    private HashSet<Item> _listedItems = new HashSet<Item>();
    internal DismantleItemList() : base(new[] { ItemsTabType.All }) { }
    protected override bool ItemFilter(Item item) => true;
    protected override void Choose(Item item) => ParentModel.Select(item);
    protected override void HoveredItemsChanged(Item item)
    {
        if (item != null && RewiredHelper.IsGamepad) ParentModel.Select(item);
    }
    protected override void SelectCurrent() => ParentModel.Select(_itemsUI.ClickedItem ?? _itemsUI.HoveredItem);
    protected override void AddPrompts()
    {
        _promptSelect = Prompts.AddPrompt(Prompt.VisualOnlyTap(KeyBindings.UI.Items.SelectItem, "Select item",
            Prompt.Position.First, ControlSchemeFlag.Gamepad), this);
    }
    internal void FocusFirst()
    {
        _listedItems = new HashSet<Item>(Items);
        ParentModel.View<VDismantleUI>().BindInventoryTabs(_itemsUI);
        _itemsUI.FocusFirstItem().Forget();
    }
    internal void Refresh()
    {
        var current = new HashSet<Item>(Items);
        foreach (ItemsListUI list in _itemsUI.Elements<ItemsListUI>().GetManagedEnumerator())
        foreach (ItemsListElementUI row in list.Elements<ItemsListElementUI>().GetManagedEnumerator().ToArray())
            if (!current.Contains(row.Item)) row.Discard();
        // Native rows remove discarded items and observe quantity changes. Rebuild only for new equipment.
        if (current.Any(item => !_listedItems.Contains(item))) _itemsUI.FullRefresh();
        else _itemsUI.SoftRefresh();
        _listedItems = current;
    }
}
