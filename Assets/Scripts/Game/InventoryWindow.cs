using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

// 편집기에서 SampleScene에 저장한 Canvas·탭·슬롯을 연결해 인벤토리를 표시한다.
public sealed class InventoryWindow : MonoBehaviour
{
    public event Action<InventoryOperation, byte, byte, byte> Requested;
    public InventoryData Data { get; private set; }
    public bool IsOpen => _panel != null && _panel.gameObject.activeSelf;
    public bool Busy => _recovery.Busy;
    private readonly InventoryRecovery3D _recovery = new();
    public int Category { get; private set; }
    public int DragSource { get; private set; } = -1;
    private RectTransform _panel;
    private Text _description, _balance, _status, _vitals;
    private readonly InventorySlot[] _slots = new InventorySlot[24];
    private readonly Button[] _tabs = new Button[5];
    private Button _use, _sort, _retry;
    private int _selected = -1;
    private static readonly Color Ink = new(0.18f, 0.25f, 0.34f), Paper = new(1f, 0.98f, 0.91f), Accent = new(1f, 0.57f, 0.19f);

    private void Awake()
    {
        _panel = (RectTransform)transform.Find("Inventory Panel");
        _description = _panel.Find("Description").GetComponent<Text>(); _balance = _panel.Find("Balance").GetComponent<Text>();
        _status = _panel.Find("Status").GetComponent<Text>(); _vitals = _panel.Find("Vitals").GetComponent<Text>();
        _use = _panel.Find("Use").GetComponent<Button>(); _sort = _panel.Find("Sort").GetComponent<Button>();
        _panel.Find("Close").GetComponent<Button>().onClick.AddListener(() => SetVisible(false));
        var retry = _panel.Find("Retry");
        if (retry != null) { _retry = retry.GetComponent<Button>(); _retry.onClick.AddListener(RefreshFromServer); }
        _use.onClick.AddListener(() => Request(InventoryOperation.Use, _selected, 0));
        _sort.onClick.AddListener(() => Request(InventoryOperation.Sort, 0, 0));
        for (int i = 0; i < 5; ++i) { int category = i; _tabs[i] = _panel.Find("Tab " + i).GetComponent<Button>(); _tabs[i].onClick.AddListener(() => SelectCategory(category)); }
        for (int i = 0; i < 24; ++i) { _slots[i] = _panel.Find("Slot " + i).GetComponent<InventorySlot>(); _slots[i].Bind(this, i); }
        SetVisible(false);
    }
    private void Update()
    {
        // 응답 대기가 끝나지 않아도 조작 잠금이 무한히 유지되지 않도록 제한 시간을 둔다.
        double now = Time.unscaledTimeAsDouble;
        if (_recovery.Expire(now)) { _status.text = "Response timed out. Refreshing before the next action."; Refresh(); }
        if (IsOpen && _recovery.TryRetry(now)) Request(InventoryOperation.Refresh, 0, 0);
    }
    public void SetVisible(bool open)
    {
        if (_panel == null) return;
        _panel.gameObject.SetActive(open); DragSource = -1;
        if (open && !Busy) RefreshFromServer();
        if (!open && EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
    }
    public void Clear()
    {
        Data = null; _recovery.Reset(); _selected = -1; DragSource = -1; SetVisible(false);
    }
    public void Apply(InventoryData data)
    {
        // 늦게 도착한 오류 응답 등이 더 최신의 인벤토리를 되돌리지 않도록 한다.
        // 저장 오류의 기본 응답은 기존 슬롯을 덮어쓰지 않고 재조회 전까지 조작을 막는다.
        if (data.Result != InventoryResult.StorageError && data.Result != InventoryResult.RateLimited &&
            Data != null && data.Revision < Data.Revision) return;
        _recovery.Accept(data.Result, Time.unscaledTimeAsDouble); DragSource = -1;
        if (!_recovery.RequiresRefresh) Data = data;
        _status.text = data.Result switch
        {
            InventoryResult.Success => "Drag items to move / merge. Double-click a potion to use.",
            InventoryResult.StaleRevision => "Inventory refreshed. Try again.",
            InventoryResult.CannotUse => "This item cannot be used, or HP / MP is already full.",
            InventoryResult.StorageError => "Inventory storage unavailable. Please try again.",
            InventoryResult.RateLimited => "Please wait a moment before the next action.", _ => "Choose a valid item and slot."
        };
        Refresh();
    }
    public void Fail(string message) { _recovery.Fail(Time.unscaledTimeAsDouble); DragSource = -1; _status.text = message; Refresh(); }
    public void RefreshFromServer() => Request(InventoryOperation.Refresh, 0, 0);
    private void SelectCategory(int category) { Category = category; _selected = -1; DragSource = -1; Refresh(); }
    public void Select(int index, bool use)
    {
        if (Busy || Data == null) return;
        _selected = index; Refresh(); if (use) Request(InventoryOperation.Use, index, 0);
    }
    public void BeginDrag(int index) { if (!Busy && !_recovery.RequiresRefresh && Data != null && Data.Entries[Category * 24 + index].ItemId != 0) { DragSource = index; _selected = index; Refresh(); } }
    public void EndDrag() { DragSource = -1; }
    public void Drop(int target) { if (DragSource >= 0 && target != DragSource) { int source = DragSource; DragSource = -1; Request(InventoryOperation.Move, source, target); } }
    private void Request(InventoryOperation operation, int source, int target)
    {
        if (source < 0 || source >= 24 || target < 0 || target >= 24 || !_recovery.Begin(operation, Time.unscaledTimeAsDouble)) return;
        // 요청 중에는 추가 조작을 막고 서버 응답에서만 표시 상태를 갱신한다.
        _status.text = "Updating inventory..."; Refresh();
        if (Requested != null) Requested(operation, (byte)Category, (byte)source, (byte)target);
        else Fail("Connect or start Offline preview first.");
    }
    private void Refresh()
    {
        if (_balance == null) return;
        for (int i = 0; i < 5; ++i) _tabs[i].GetComponent<Image>().color = i == Category ? Accent : new Color(0.79f, 0.87f, 0.93f);
        int used = 0;
        for (int i = 0; i < 24; ++i)
        {
            InventoryEntry entry = Data == null ? default : Data.Entries[Category * 24 + i];
            if (entry.ItemId != 0) ++used; _slots[i].Render(entry, i == _selected, Busy);
        }
        _balance.text = $"MESOS  {(Data?.Mesos ?? 0):N0}       SLOTS  {used} / 24";
        _vitals.text = Data == null ? "Loading character inventory..." : $"HP  {Data.Hp} / {Data.MaxHp}       MP  {Data.Mp} / {Data.MaxMp}";
        uint item = Data != null && _selected >= 0 ? Data.Entries[Category * 24 + _selected].ItemId : 0;
        _description.text = ItemCatalog.Name(item) + "\n" + ItemCatalog.Description(item);
        _use.interactable = !Busy && !_recovery.RequiresRefresh && Category == 1 && (item == 101 || item == 102);
        _sort.interactable = !Busy && !_recovery.RequiresRefresh && Data != null;
        if (_retry != null) _retry.interactable = !Busy;
    }
    // 실행 중 자동 생성 대신 편집기에서 배치하고 저장할 기본 UGUI 구성을 만든다.
    public static void BuildLayout(GameObject root)
    {
        if (root.transform.Find("Inventory Panel") != null) return;
        var canvas = root.GetComponent<Canvas>(); if (canvas == null) canvas = root.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 20;
        var scaler = root.GetComponent<CanvasScaler>(); if (scaler == null) scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new(1100, 720); scaler.matchWidthOrHeight = 0.5f;
        root.AddComponent<GraphicRaycaster>();
        if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() == null)
        { var events = new GameObject("Field UI EventSystem"); events.AddComponent<EventSystem>(); events.AddComponent<InputSystemUIInputModule>(); }
        var panel = Node(root.transform, "Inventory Panel", new(630, 105, 430, 530), Paper);
        var drag = Node(panel, "Title", new(0, 0, 430, 38), Accent); Label(drag, "Label", new(14, 0, 365, 38), "ITEM INVENTORY", 18, FontStyle.Bold);
        drag.gameObject.AddComponent<InventoryDragHandle>();
        ButtonAt(panel, "Close", new(390, 7, 28, 24), "X", new Color(0.97f, 0.42f, 0.2f));
        string[] names = { "EQUIP", "USE", "SETUP", "ETC", "CASH" };
        for (int i = 0; i < 5; ++i) ButtonAt(panel, "Tab " + i, new(14 + i * 80, 50, 76, 30), names[i], new Color(0.79f, 0.87f, 0.93f));
        Label(panel, "Vitals", new(16, 86, 395, 24), "HP / MP", 13);
        for (int i = 0; i < 24; ++i)
        {
            var slot = Node(panel, "Slot " + i, new(16 + i % 6 * 67, 120 + i / 6 * 57, 62, 52), new Color(0.87f, 0.94f, 0.93f));
            var outline = slot.gameObject.AddComponent<Outline>(); outline.effectColor = new(0.59f, 0.71f, 0.72f); outline.effectDistance = new(1, -1);
            var icon = Node(slot, "Icon", new(10, 4, 38, 38), Color.white); UnityEngine.Object.DestroyImmediate(icon.GetComponent<Image>());
            icon.gameObject.AddComponent<InventoryItemIcon>();
            var quantity = Label(slot, "Quantity", new(2, 31, 57, 19), "", 12, FontStyle.Bold); quantity.alignment = TextAnchor.MiddleRight;
            slot.gameObject.AddComponent<InventorySlot>();
        }
        Label(panel, "Description", new(18, 357, 394, 75), "Select an item to inspect it.", 13);
        Label(panel, "Balance", new(18, 439, 395, 22), "MESOS", 13, FontStyle.Bold);
        Label(panel, "Status", new(18, 467, 394, 42), "I: open / close inventory", 11);
        ButtonAt(panel, "Sort", new(240, 405, 80, 28), "SORT", new Color(0.78f, 0.86f, 0.96f));
        ButtonAt(panel, "Use", new(332, 405, 80, 28), "USE", Accent);
        EnsureControls(root);
        root.AddComponent<InventoryWindow>();
    }
    // 기존 씬에도 재조회 버튼만 추가해 배치한 슬롯과 창 위치는 유지한다.
    public static void EnsureControls(GameObject root)
    {
        var panel = root.transform.Find("Inventory Panel");
        if (panel != null && panel.Find("Retry") == null)
            ButtonAt(panel, "Retry", new(16, 405, 95, 28), "REFRESH", new Color(0.78f, 0.86f, 0.96f));
    }
    private static RectTransform Node(Transform parent, string name, Rect rect, Color color)
    {
        var obj = new GameObject(name, typeof(RectTransform), typeof(Image)); obj.transform.SetParent(parent, false);
        var transform = obj.GetComponent<RectTransform>(); transform.anchorMin = transform.anchorMax = new(0, 1); transform.pivot = new(0, 1);
        transform.anchoredPosition = new(rect.x, -rect.y); transform.sizeDelta = rect.size; obj.GetComponent<Image>().color = color; return transform;
    }
    private static Text Label(Transform parent, string name, Rect rect, string value, int size, FontStyle style = FontStyle.Normal)
    {
        var node = Node(parent, name, rect, Color.clear); UnityEngine.Object.DestroyImmediate(node.GetComponent<Image>());
        var text = node.gameObject.AddComponent<Text>(); text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); text.text = value;
        text.fontSize = size; text.fontStyle = style; text.color = Ink; text.raycastTarget = false; text.alignment = TextAnchor.MiddleLeft; return text;
    }
    private static Button ButtonAt(Transform parent, string name, Rect rect, string value, Color color)
    {
        var node = Node(parent, name, rect, color); var button = node.gameObject.AddComponent<Button>();
        Label(node, "Label", new(0, 0, rect.width, rect.height), value, 12, FontStyle.Bold).alignment = TextAnchor.MiddleCenter; return button;
    }
}
