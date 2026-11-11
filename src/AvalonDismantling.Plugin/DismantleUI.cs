using System;
using System.Collections.Generic;
using System.Linq;
using Awaken.TG.MVC;
using Awaken.TG.MVC.Domains;
using Awaken.TG.MVC.UI.Handlers.States;
using Awaken.TG.MVC.UI.Universal;
using Awaken.TG.Main.Heroes;
using Awaken.TG.Main.Heroes.CharacterSheet.Items.Choose;
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
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AvalonDismantling.Plugin;

internal sealed class DismantleUI : Model, IUIStateSource, IItemChooseParent, IPromptHost, IClosable
{
    private readonly GameCatalog _catalog;
    private VDismantleUI _view = null!;
    private DismantleItemList _list = null!;
    private ItemTooltipUI _tooltip = null!;
    private Prompt _confirm = null!;
    private Item? _selected;
    private ExistingItemDescriptor? _selectedDescriptor;
    private Quote? _quote;
    private bool _busy;
    private string _lastResult = "";

    public override Domain DefaultDomain => Domain.Gameplay;
    public override bool IsNotSaved => true;
    public UIState UIState => UIState.ModalState(HUDState.EverythingHidden).WithPauseTime();
    public IEnumerable<Item> PossibleItems => Hero.Current.HeroItems.Items.Where(item => !item.HiddenOnUI && GameCatalog.IsSupported(item.Template));
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
        Prompts.AddPrompt(Prompt.Tap(KeyBindings.UI.Generic.Cancel, "Back", Close, Prompt.Position.Last), this);
        _confirm = Prompts.BindPrompt(Prompt.Hold(KeyBindings.UI.Crafting.CraftOne, "Dismantle", Confirm,
            holdTime: 0.65f), this, _view.ConfirmView, active: false);
        _tooltip = AddElement(new ItemTooltipUI(typeof(VStaticSingleItemTooltipSystemUI), _view.TooltipHost,
            isStatic: true, comparerActive: false, prompts: Prompts));
        _list = AddElement(new DismantleItemList());
        _view.SetStatus(_selected != null ? "" : PossibleItems.Any()
            ? "Select an item to see material recovery." : "No equipment or ammunition in your inventory.");
        _list.FocusFirst();
    }

    internal void Select(Item? item)
    {
        if (_busy || HasBeenDiscarded) return;
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
            _list.Refresh();
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
        _itemsUI.FocusFirstItem().Forget();
    }
    internal void Refresh()
    {
        var current = new HashSet<Item>(Items);
        // Native rows remove discarded items and observe quantity changes. Rebuild only for new equipment.
        if (current.Any(item => !_listedItems.Contains(item))) _itemsUI.FullRefresh();
        else _itemsUI.SoftRefresh();
        _listedItems = current;
    }
}

internal sealed class VDismantleUI : View<DismantleUI>
{
    private readonly List<TempItemDescriptor> _previews = new List<TempItemDescriptor>();
    private readonly List<GameObject> _slots = new List<GameObject>();
    private Transform _grid = null!;
    private ItemSlotUI _slotTemplate = null!;
    private TMP_Text _heading = null!;
    private TMP_Text _details = null!;
    private TMP_Text _status = null!;
    private TMP_Text _fontTemplate = null!;
    private LayoutElement _gridSize = null!;

    internal Transform InventoryHost { get; private set; } = null!;
    internal Transform TooltipHost { get; private set; } = null!;
    internal Transform FooterHost { get; private set; } = null!;
    internal VGenericPromptUI ConfirmView { get; private set; } = null!;

    private void Update()
    {
        if (Target != null && !Target.HasBeenDiscarded) Target.PreserveSelectedTooltip();
    }

    protected override void OnInitialize()
    {
        InventoryHost = Required("LeftContent");
        Transform right = Required("RightContent");
        _grid = Required("RightContent/DismantleIngredients/DismantleIngredients");
        _slotTemplate = Required("RightContent/DismantleIngredients/DismantleIngredients/ItemSlot").GetComponent<ItemSlotUI>()
            ?? throw new InvalidOperationException("The dismantling ingredient slot is missing.");
        _fontTemplate = Required("RightContent/DismantleIngredients/Title").GetComponent<TMP_Text>()
            ?? throw new InvalidOperationException("The dismantling font binding is missing.");
        ConfirmView = Required("RightContent/VGenericPromptUI").GetComponent<VGenericPromptUI>()
            ?? throw new InvalidOperationException("The dismantling confirmation prompt is missing.");

        Stretch((RectTransform)transform, 0, 0, 1, 1);
        Stretch((RectTransform)InventoryHost, 0.025f, 0.16f, 0.36f, 0.85f);
        Stretch((RectTransform)right, 0.71f, 0.19f, 0.975f, 0.85f);
        right.GetComponent<CanvasGroup>().alpha = 1f;
        var rightLayout = right.GetComponent<VerticalLayoutGroup>();
        rightLayout.childControlWidth = true;
        rightLayout.childControlHeight = true;
        rightLayout.childForceExpandHeight = false;
        rightLayout.childAlignment = TextAnchor.UpperLeft;
        rightLayout.spacing = 12;
        // The retained ingredient wrapper uses fixed anchors. Put its native grid in the flow layout.
        Transform ingredientWrapper = _grid.parent;
        _grid.SetParent(right, false);
        ingredientWrapper.gameObject.SetActive(false);
        _grid.GetComponent<ContentSizeFitter>().enabled = false;
        _gridSize = _grid.gameObject.AddComponent<LayoutElement>();
        _gridSize.preferredHeight = 0;
        var gridLayout = _grid.GetComponent<GridLayoutGroup>();
        gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        gridLayout.constraintCount = 3;
        gridLayout.cellSize = new Vector2(100, 150);
        gridLayout.spacing = new Vector2(12, 10);
        _slotTemplate.gameObject.SetActive(false);
        Required("RightContent/Cost").gameObject.SetActive(false);
        _fontTemplate.gameObject.SetActive(false);

        var background = (RectTransform)Required("BackgroundSlot");
        Stretch(background, 0, 0, 1, 1);
        background.SetAsFirstSibling();
        // Keep an opaque local surface; the native map background intentionally shows the world.
        var backdrop = background.gameObject.AddComponent<Image>();
        backdrop.color = new Color(0.11f, 0.115f, 0.12f, 1f);
        backdrop.raycastTarget = true;
        backdrop.canvasRenderer.SetAlpha(1f);

        TooltipHost = NewRect("SelectedItemTooltip", transform);
        Stretch((RectTransform)TooltipHost, 0.38f, 0.18f, 0.69f, 0.85f);
        FooterHost = NewRect("Prompts", transform);
        Stretch((RectTransform)FooterHost, 0.025f, 0.035f, 0.975f, 0.085f);
        var footerLayout = FooterHost.gameObject.AddComponent<HorizontalLayoutGroup>();
        footerLayout.childForceExpandWidth = false;
        footerLayout.childForceExpandHeight = false;
        footerLayout.childAlignment = TextAnchor.MiddleLeft;
        footerLayout.spacing = 32;

        var title = NewText("Title", transform, 36);
        Stretch(title.rectTransform, 0.025f, 0.89f, 0.975f, 0.96f);
        title.text = "DISMANTLE";
        _heading = NewText("SelectedItem", right, 26);
        _heading.transform.SetAsFirstSibling();
        _details = NewText("RecoveryDetails", right, 18);
        _details.transform.SetSiblingIndex(1);
        _grid.SetSiblingIndex(2);
        var cost = NewText("Cost", right, 22);
        cost.text = "Gold cost: 0";
        cost.transform.SetSiblingIndex(3);
        ConfirmView.transform.SetAsLastSibling();
        var confirmSize = ConfirmView.GetComponent<LayoutElement>() ?? ConfirmView.gameObject.AddComponent<LayoutElement>();
        confirmSize.preferredHeight = 55;
        _status = NewText("Result", transform, 22);
        Stretch(_status.rectTransform, 0.025f, 0.09f, 0.975f, 0.15f);
        _status.color = new Color(0.8f, 0.9f, 0.9f);
        SetDetails("Select an item", "Recover 50–100% of recipe materials.\nSingle-unit ingredients always return one.");
    }

    internal void ShowQuote(GameCatalog catalog, Quote quote)
    {
        string match = quote.IsExact ? "Exact crafting recipe" : "Materials based on: " + quote.MatchedName;
        string upgrade = quote.Plan.Upgrades.Count == 0 ? "" : "\nIncludes estimated upgrade materials: " +
            string.Join(", ", quote.Plan.Upgrades.Select(p => $"{p.Value} {catalog.Material(p.Key).ItemName}"));
        SetDetails(quote.Source.DisplayName, $"Consume: {quote.Plan.Consumed}\n{match}\nRecovery: 50–100%, rounded up.{upgrade}");
        foreach (var ingredient in quote.Plan.Maximum)
        {
            var descriptor = new TempItemDescriptor(catalog.Material(ingredient.Key), Target,
                ingredient.Value, 0, 0, 0);
            _previews.Add(descriptor);
            RectTransform cell = NewRect("RecoveredMaterial", _grid);
            _slots.Add(cell.gameObject);
            GameObject clone = Object.Instantiate(_slotTemplate.gameObject, cell, false);
            Stretch((RectTransform)clone.transform, 0, 0.27f, 1, 1);
            var slot = clone.GetComponent<ItemSlotUI>();
            slot.ResetHoveredState();
            slot.Setup(descriptor.ExistingItem, this);
            slot.SetVisibilityConfig(ItemSlotUI.VisibilityConfig.Crafting);
            int minimum = quote.Plan.Minimum[ingredient.Key];
            Transform quantity = clone.transform.Find("QuantitySlot");
            quantity.gameObject.SetActive(true);
            var label = quantity.GetComponentInChildren<TMP_Text>(true);
            label.text = minimum == ingredient.Value ? ingredient.Value.ToString() : $"{minimum}–{ingredient.Value}";
            label.gameObject.SetActive(true);
            clone.SetActive(true);
            TMP_Text name = NewText("MaterialName", cell, 16);
            Stretch(name.rectTransform, 0, 0, 1, 0.25f);
            name.alignment = TextAlignmentOptions.Top;
            name.text = catalog.Material(ingredient.Key).ItemName;
        }
        _gridSize.preferredHeight = ((_slots.Count + 2) / 3) * 160;
    }

    internal void SetDetails(string heading, string details)
    {
        _heading.text = heading;
        _details.text = details;
    }

    internal void SetStatus(string text) => _status.text = text;

    internal void ClearIngredients()
    {
        foreach (GameObject slot in _slots) if (slot != null) Object.Destroy(slot);
        _slots.Clear();
        if (_gridSize != null) _gridSize.preferredHeight = 0;
        foreach (TempItemDescriptor preview in _previews) preview.Dispose();
        _previews.Clear();
    }

    protected override IBackgroundTask OnDiscard()
    {
        ClearIngredients();
        return null!;
    }

    private Transform Required(string path) => transform.Find(path)
        ?? throw new InvalidOperationException("Dismantling layout binding missing: " + path);

    private TMP_Text NewText(string name, Transform parent, float size)
    {
        RectTransform rect = NewRect(name, parent);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        text.font = _fontTemplate.font;
        text.fontSharedMaterial = _fontTemplate.fontSharedMaterial;
        text.fontSize = size;
        text.color = _fontTemplate.color;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.raycastTarget = false;
        var layout = rect.gameObject.AddComponent<LayoutElement>();
        layout.flexibleWidth = 1;
        return text;
    }

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = parent.gameObject.layer;
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        return rect;
    }

    private static void Stretch(RectTransform rect, float left, float bottom, float right, float top)
    {
        rect.anchorMin = new Vector2(left, bottom);
        rect.anchorMax = new Vector2(right, top);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.localScale = Vector3.one;
    }
}
