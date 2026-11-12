using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Awaken.TG.MVC;
using Awaken.TG.MVC.Domains;
using Awaken.TG.MVC.UI.Handlers.States;
using Awaken.TG.MVC.UI.Universal;
using Awaken.TG.MVC.UI.Handlers.Focuses;
using Awaken.TG.MVC.UI.Events;
using Awaken.TG.Main.Heroes;
using Awaken.TG.Main.Heroes.CharacterSheet.Items.Choose;
using Awaken.TG.Main.Heroes.CharacterSheet.Items.Panel;
using Awaken.TG.Main.Heroes.CharacterSheet.Items.Panel.Slot;
using Awaken.TG.Main.Heroes.CharacterSheet.Items.Panel.Tabs;
using Awaken.TG.Main.Heroes.Items;
using Awaken.TG.Main.Heroes.Items.Tooltips;
using Awaken.TG.Main.Heroes.Items.Tooltips.Descriptors;
using Awaken.TG.Main.Heroes.Items.Tooltips.Views;
using Awaken.TG.Main.Locations.Gems;
using Awaken.TG.Main.UI.ButtonSystem;
using Awaken.TG.Main.UI.Components;
using Awaken.TG.Main.UI.Components.PadShortcuts;
using Awaken.TG.Main.UI.HUD;
using Awaken.TG.Main.Utility;
using Awaken.TG.Main.Utility.UI;
using Awaken.TG.Main.Utility.UI.Keys;
using Awaken.TG.Utility;
using Awaken.Utility.Animations;
using Awaken.Utility;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AvalonDismantling.Plugin;

internal sealed class VDismantleUI : View<DismantleUI>, IAutoFocusBase
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
    private readonly Dictionary<string, ARButton> _typeButtons = new Dictionary<string, ARButton>();
    private readonly Dictionary<string, VCItemTabButton> _nativeTypeTabs = new Dictionary<string, VCItemTabButton>();
    private static readonly MethodInfo NativeTabSelection = AccessTools.Method(typeof(VCItemTabButton), "Refresh", new[] { typeof(bool) })
        ?? throw new MissingMethodException(typeof(VCItemTabButton).FullName, "Refresh(bool)");
    private ItemsUI? _pendingInventory;
    private VItemsTabs? _nativeTabs;
    private string[] _availableTypes = { "All" };
    private string _selectedType = "All";
    private ARButton _rarityButton = null!;
    private TMP_Text _rarityLabel = null!;
    private Transform _right = null!;
    private CanvasGroup _inventoryInteraction = null!;
    private RectTransform _reviewRoot = null!;
    private TMP_Text _reviewText = null!;
    private ScrollRect _reviewScroll = null!;
    private ScrollRect _statusScroll = null!;
    private ScrollWithPad _reviewPadScroll = null!;
    private ScrollWithPadDPad _reviewDPadScroll = null!;
    private ScrollWithPad _statusPadScroll = null!;
    private ScrollWithPadDPad _statusDPadScroll = null!;
    private bool _reviewing;
    private bool _processing;
    private bool _filtersEnabled;

    internal Transform InventoryHost { get; private set; } = null!;
    internal Transform TooltipHost { get; private set; } = null!;
    internal Transform FooterHost { get; private set; } = null!;
    internal VGenericPromptUI ConfirmView { get; private set; } = null!;

    private void Update()
    {
        if (Target == null || Target.HasBeenDiscarded) return;
        if (_pendingInventory != null) BindInventoryTabs(_pendingInventory);
        Target.Tick();
        if (!_reviewing) Target.PreserveSelectedTooltip();
        bool statusFocused = World.Any<Focus>()?.Focused == _statusScroll.verticalScrollbar;
        _reviewPadScroll.enabled = _reviewing && !statusFocused;
        _reviewDPadScroll.enabled = _reviewing && !statusFocused;
        _statusPadScroll.enabled = statusFocused;
        _statusDPadScroll.enabled = statusFocused;
        HandleNativeTabShortcuts();
        HandleKeyboard();
    }

    protected override void OnInitialize()
    {
        InventoryHost = Required("LeftContent");
        Transform right = _right = Required("RightContent");
        _grid = Required("RightContent/DismantleIngredients/DismantleIngredients");
        _slotTemplate = Required("RightContent/DismantleIngredients/DismantleIngredients/ItemSlot").GetComponent<ItemSlotUI>()
            ?? throw new InvalidOperationException("The dismantling ingredient slot is missing.");
        _fontTemplate = Required("RightContent/DismantleIngredients/Title").GetComponent<TMP_Text>()
            ?? throw new InvalidOperationException("The dismantling font binding is missing.");
        ConfirmView = Required("RightContent/VGenericPromptUI").GetComponent<VGenericPromptUI>()
            ?? throw new InvalidOperationException("The dismantling confirmation prompt is missing.");

        Stretch((RectTransform)transform, 0, 0, 1, 1);
        Stretch((RectTransform)InventoryHost, 0.025f, 0.16f, 0.36f, 0.88f);
        _inventoryInteraction = InventoryHost.GetComponent<CanvasGroup>() ?? InventoryHost.gameObject.AddComponent<CanvasGroup>();
        Stretch((RectTransform)right, 0.71f, 0.19f, 0.975f, 0.80f);
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
        Stretch((RectTransform)TooltipHost, 0.38f, 0.18f, 0.69f, 0.80f);
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
        CreateFilters();
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
        RectTransform statusRoot = NewRect("ResultScroll", transform);
        Stretch(statusRoot, 0.025f, 0.085f, 0.975f, 0.125f);
        _statusScroll = NewScroll(statusRoot, "Result", 20, out _status);
        _statusPadScroll = statusRoot.gameObject.AddComponent<ScrollWithPad>();
        _statusDPadScroll = statusRoot.gameObject.AddComponent<ScrollWithPadDPad>();
        _statusPadScroll.enabled = false;
        _statusDPadScroll.enabled = false;
        _status.color = new Color(0.8f, 0.9f, 0.9f);
        CreateReview();
        SetDetails("Select an item", "Recover 50–100% of recipe materials.\nSingle-unit ingredients always return one.");
    }

    private void CreateFilters()
    {
        _rarityButton = NewButton("RarityFilter", transform, () => Target.CycleRarity(), out _rarityLabel);
        Stretch((RectTransform)_rarityButton.transform, 0.71f, 0.825f, 0.975f, 0.88f);
        _rarityLabel.text = "Rarity: All  ›";
    }

    internal void BindInventoryTabs(ItemsUI items)
    {
        if (items.HasBeenDiscarded) { _pendingInventory = null; return; }
        _pendingInventory = items;
        Transform host = items.TabButtonsHost;
        if (host == null) return;
        VItemsTabs? tabs = host.GetComponentInChildren<VItemsTabs>(true);
        if (tabs == null || tabs.Target == null) return;
        _pendingInventory = null;
        if (_nativeTabs == tabs) return;
        _nativeTabs = tabs;
        _typeButtons.Clear();
        _nativeTypeTabs.Clear();
        tabs.Target.BlockNavigation = true;

        // Reuse the inventory's complete tab row; the dismantling snapshot owns its filtering.
        foreach (VCItemTabButton tab in tabs.GetComponentsInChildren<VCItemTabButton>(true))
        {
            World.EventSystem.RemoveAllListenersOwnedBy(tab, removeModals: false);
            tab.button.ClearAllOnClickEvents();
            string? type = tab.Type == ItemsTabType.All || tab.Type == ItemsTabType.AllWithRecent ? "All"
                : tab.Type == ItemsTabType.Weapons ? "Weapons"
                : tab.Type == ItemsTabType.Armor ? "Armor"
                : tab.Type == ItemsTabType.Jewelry ? "Other" : null;
            if (type == null || _typeButtons.ContainsKey(type))
            {
                tab.gameObject.SetActive(false);
                continue;
            }
            string category = type;
            tab.button.OnClick += () => Target.ChangeType(category);
            tab.button.navigation = new Navigation { mode = Navigation.Mode.Automatic };
            var tooltip = tab.gameObject.GetComponent<VCTooltipProvider>() ?? tab.gameObject.AddComponent<VCTooltipProvider>();
            tooltip.tooltipHost = (RectTransform)tab.transform;
            tooltip.Assign(type == "Other" ? "Other / " + ItemsTabType.Jewelry.Title : tab.Type.Title);
            _typeButtons.Add(type, tab.button);
            _nativeTypeTabs.Add(type, tab);
        }
        if (_typeButtons.Count != 4)
            throw new InvalidOperationException("The native inventory tab row is missing a dismantling category.");
        host.gameObject.SetActive(true);
        Stretch((RectTransform)host, 0, 0.90f, 1, 1);
        Stretch((RectTransform)items.ContentHost, 0, 0, 1, 0.87f);
        ApplyNativeTypeFilters();
    }

    internal void SetFilters(IReadOnlyList<string> types, string selectedType,
        IReadOnlyList<ItemQuality> rarities, ItemQuality? selectedRarity, bool enabled)
    {
        _filtersEnabled = enabled;
        _availableTypes = types.ToArray();
        _selectedType = selectedType;
        ApplyNativeTypeFilters();
        _rarityLabel.text = "Rarity: " + (selectedRarity?.DisplayName.ToString() ?? "All") + "  ›";
        _rarityLabel.color = selectedRarity != null ? (Color)selectedRarity.NameColor : _fontTemplate.color;
        _rarityButton.gameObject.SetActive(rarities.Count > 0);
        RefreshInteraction();
    }

    private void ApplyNativeTypeFilters()
    {
        foreach (var entry in _nativeTypeTabs)
        {
            if (entry.Value == null) continue;
            bool visible = entry.Key == "All" || _availableTypes.Contains(entry.Key);
            entry.Value.gameObject.SetActive(visible);
            if (visible) NativeTabSelection.Invoke(entry.Value, new object[] { entry.Key == _selectedType });
        }
        RefreshInteraction();
    }

    private void HandleNativeTabShortcuts()
    {
        if (!RewiredHelper.IsGamepad || !_filtersEnabled || _reviewing || _processing) return;
        int direction = RewiredHelper.Player.GetButtonDown(KeyBindings.UI.Generic.PreviousAlt) ? -1
            : RewiredHelper.Player.GetButtonDown(KeyBindings.UI.Generic.NextAlt) ? 1 : 0;
        if (direction == 0 || _availableTypes.Length == 0) return;
        int current = Array.IndexOf(_availableTypes, _selectedType);
        Target.ChangeType(_availableTypes[(current + direction + _availableTypes.Length) % _availableTypes.Length]);
    }

    internal void ShowBulkReview(string reviewText)
    {
        _reviewing = true;
        InventoryHost.gameObject.SetActive(false);
        TooltipHost.gameObject.SetActive(false);
        ConfirmView.transform.SetParent(transform, false);
        ConfirmView.GetComponent<LayoutElement>().ignoreLayout = true;
        Stretch((RectTransform)ConfirmView.transform, 0.71f, 0.16f, 0.975f, 0.215f);
        _right.gameObject.SetActive(false);
        _reviewRoot.gameObject.SetActive(true);
        _reviewText.text = reviewText;
        RefreshScroll(_reviewScroll, reset: true);
        RefreshInteraction();
        World.Any<Focus>()?.Select(_reviewScroll.verticalScrollbar);
    }

    internal void HideBulkReview()
    {
        _reviewing = false;
        _reviewRoot.gameObject.SetActive(false);
        _right.gameObject.SetActive(true);
        ConfirmView.transform.SetParent(_right, false);
        ConfirmView.GetComponent<LayoutElement>().ignoreLayout = false;
        ConfirmView.transform.SetAsLastSibling();
        InventoryHost.gameObject.SetActive(true);
        TooltipHost.gameObject.SetActive(true);
        RefreshInteraction();
        World.Any<Focus>()?.Select(_rarityButton.gameObject.activeInHierarchy ? _rarityButton
            : _typeButtons.TryGetValue("All", out ARButton all) ? all : null);
    }

    internal void SetProcessing(bool busy)
    {
        _processing = busy;
        RefreshInteraction();
    }

    internal void SetBulkProgress(string text) => SetStatus(text);

    private void RefreshInteraction()
    {
        bool enabled = _filtersEnabled && !_reviewing && !_processing;
        foreach (ARButton button in _typeButtons.Values) if (button != null) button.Interactable = enabled;
        _rarityButton.Interactable = enabled;
        _inventoryInteraction.interactable = !_reviewing && !_processing;
        _inventoryInteraction.blocksRaycasts = !_reviewing && !_processing;
    }

    private void CreateReview()
    {
        _reviewRoot = NewRect("BulkReview", transform);
        Stretch(_reviewRoot, 0.025f, 0.23f, 0.975f, 0.80f);
        _reviewScroll = NewScroll(_reviewRoot, "BulkReviewText", 22, out _reviewText);
        _reviewPadScroll = _reviewRoot.gameObject.AddComponent<ScrollWithPad>();
        _reviewDPadScroll = _reviewRoot.gameObject.AddComponent<ScrollWithPadDPad>();
        _reviewRoot.gameObject.SetActive(false);
    }

    private ScrollRect NewScroll(RectTransform parent, string textName, float fontSize, out TMP_Text text)
    {
        var scroll = parent.gameObject.AddComponent<NoDragScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = false;
        scroll.scrollSensitivity = 50;
        RectTransform viewport = NewRect("Viewport", parent);
        Stretch(viewport, 0, 0, 1, 1);
        viewport.offsetMax = new Vector2(-24, 0);
        viewport.gameObject.AddComponent<RectMask2D>();
        var hitArea = viewport.gameObject.AddComponent<Image>();
        hitArea.color = Color.clear;
        hitArea.raycastTarget = true;
        text = NewText(textName, viewport, fontSize);
        text.rectTransform.anchorMin = new Vector2(0, 1);
        text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.pivot = new Vector2(0.5f, 1);
        text.rectTransform.sizeDelta = Vector2.zero;
        text.margin = new Vector4(8, 4, 8, 8);
        text.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.viewport = viewport;
        scroll.content = text.rectTransform;
        RectTransform bar = NewRect("Scrollbar", parent);
        Stretch(bar, 1, 0, 1, 1);
        bar.offsetMin = new Vector2(-12, 0);
        bar.gameObject.AddComponent<Image>().color = new Color(1, 1, 1, 0.08f);
        RectTransform handle = NewRect("Handle", bar);
        Stretch(handle, 0, 0, 1, 1);
        var handleImage = handle.gameObject.AddComponent<Image>();
        handleImage.color = _fontTemplate.color;
        var scrollbar = bar.gameObject.AddComponent<Scrollbar>();
        scrollbar.direction = Scrollbar.Direction.BottomToTop;
        scrollbar.handleRect = handle;
        scrollbar.targetGraphic = handleImage;
        scrollbar.navigation = new Navigation { mode = Navigation.Mode.Automatic };
        scroll.verticalScrollbar = scrollbar;
        return scroll;
    }

    private ARButton NewButton(string name, Transform parent, Action click, out TMP_Text label)
    {
        RectTransform rect = NewRect(name, parent);
        rect.gameObject.SetActive(false);
        var image = rect.gameObject.AddComponent<Image>();
        var button = rect.gameObject.AddComponent<DismantleFilterButton>();
        button.transition = Selectable.Transition.None;
        button.TargetGraphic = image;
        button.NormalColor = new Color(0.065f, 0.07f, 0.075f, 1);
        button.HoverColor = new Color(0.10f, 0.105f, 0.11f, 1);
        button.SelectedColor = new Color(0.12f, 0.125f, 0.13f, 1);
        button.PressColor = new Color(0.14f, 0.145f, 0.15f, 1);
        button.DisableColor = new Color(0.08f, 0.085f, 0.09f, 1);
        button.transitionTime = 0;
        image.color = button.NormalColor;
        button.navigation = new Navigation { mode = Navigation.Mode.Automatic };
        button.OnClick += click;
        label = NewText("Label", rect, 22);
        Stretch(label.rectTransform, 0, 0, 1, 1);
        label.alignment = TextAlignmentOptions.Center;
        rect.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
        rect.gameObject.SetActive(true);
        return button;
    }

    private void HandleKeyboard()
    {
        if (RewiredHelper.IsGamepad) return;
        ScrollRect scroll = _reviewing ? _reviewScroll : _statusScroll;
        float axis = _reviewing ? RewiredHelper.Player.GetAxis(KeyBindings.UI.Generic.ScrollVerticalTooltip) : 0;
        if (Input.GetKey(KeyCode.PageUp)) axis += 2;
        if (Input.GetKey(KeyCode.PageDown)) axis -= 2;
        if (_reviewing)
        {
            if (Input.GetKey(KeyCode.UpArrow)) axis += 1;
            if (Input.GetKey(KeyCode.DownArrow)) axis -= 1;
        }
        float overflow = scroll.content.rect.height - scroll.viewport.rect.height;
        if (overflow > 0 && axis != 0)
            scroll.verticalNormalizedPosition = Mathf.Clamp01(scroll.verticalNormalizedPosition + axis * 800 * Time.unscaledDeltaTime / overflow);

        if (_reviewing || _processing || !_filtersEnabled || EventSystem.current == null) return;
        bool previous = Input.GetKeyDown(KeyCode.LeftArrow) ||
            (Input.GetKeyDown(KeyCode.Tab) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)));
        bool next = Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.RightArrow);
        bool submit = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space);
        if (!previous && !next && !submit) return;
        var buttons = _typeButtons.Values.Where(button => button != null && button.gameObject.activeInHierarchy).ToList();
        if (_rarityButton.gameObject.activeInHierarchy) buttons.Add(_rarityButton);
        if (buttons.Count == 0) return;
        GameObject current = EventSystem.current.currentSelectedGameObject;
        int index = buttons.FindIndex(button => button.gameObject == current);
        if (previous || next)
        {
            index = index < 0 ? (previous ? buttons.Count - 1 : 0)
                : (index + (previous ? -1 : 1) + buttons.Count) % buttons.Count;
            EventSystem.current.SetSelectedGameObject(buttons[index].gameObject);
        }
        else if (index >= 0 && submit)
        {
            buttons[index].Handle(new UISubmitAction());
        }
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

    internal void SetStatus(string text)
    {
        if (_status.text == text) return;
        _status.text = text;
        RefreshScroll(_statusScroll, reset: true);
    }

    private static void RefreshScroll(ScrollRect scroll, bool reset)
    {
        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
        if (reset) scroll.verticalNormalizedPosition = 1;
    }

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

internal sealed class DismantleFilterButton : ARButton
{
    protected override void Awake()
    {
        // ARButton caches graphic availability in Awake, before callers can assign TargetGraphic.
        TargetGraphic = GetComponent<Image>();
        base.Awake();
    }
}
