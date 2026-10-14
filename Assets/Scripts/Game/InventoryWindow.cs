using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using System.Collections.Generic;

// 편집기에서 SampleScene에 저장한 Canvas·탭·슬롯을 연결해 인벤토리를 표시한다.
public sealed class InventoryWindow : MonoBehaviour
{
    public event Action<InventoryOperation, byte, byte, byte, ulong> Requested;
    private ulong _nextRequestId, _pendingRequestId;
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
    private GameObject _equipment;
    private Button _equip;
    private readonly Text[] _equippedLabels=new Text[2];
    private readonly Button[] _unequip=new Button[2];
    private int _selected = -1;
    private bool _mouseHeld;
    private int _mouseSource = -1, _mouseCategory, _rawDragFrame = -1;
    private Vector2 _mousePosition, _mouseStart;
    private InventoryEntry _mouseEntry;
    private readonly List<RaycastResult> _mouseHits = new();
    public bool HandledMouseDrag => _rawDragFrame == Time.frameCount;
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
        _equip=_panel.Find("Equip").GetComponent<Button>(); _equip.onClick.AddListener(()=>Request(InventoryOperation.Equip,_selected,0));
        _equipment=transform.Find("Equipment Panel").gameObject;
        for (int i=0;i<2;++i) { int slot=i; _equippedLabels[i]=_equipment.transform.Find("Item "+i).GetComponent<Text>(); _unequip[i]=_equipment.transform.Find("Remove "+i).GetComponent<Button>(); _unequip[i].onClick.AddListener(()=>Unequip(slot)); }
        for (int i = 0; i < 5; ++i) { int category = i; _tabs[i] = _panel.Find("Tab " + i).GetComponent<Button>(); _tabs[i].onClick.AddListener(() => SelectCategory(category)); }
        for (int i = 0; i < 24; ++i) { _slots[i] = _panel.Find("Slot " + i).GetComponent<InventorySlot>(); _slots[i].Bind(this, i); }
        SetVisible(false);
        InputSystem.onEvent += ReadMouseEvent;
    }
    private void OnDestroy() { InputSystem.onEvent -= ReadMouseEvent; }
    private void OnApplicationFocus(bool focused)
    {
        // 다른 창으로 전환한 드래그는 복귀 후 해제 입력으로 적용하지 않는다.
        if (!focused) { _mouseHeld=false; _mouseSource=-1; DragSource=-1; }
    }
    private int MouseSlot(Vector2 position)
    {
        if (EventSystem.current == null) return -1;
        var pointer = new PointerEventData(EventSystem.current) { position = position };
        _mouseHits.Clear(); EventSystem.current.RaycastAll(pointer, _mouseHits);
        if (_mouseHits.Count == 0) return -1;
        var slot = _mouseHits[0].gameObject.GetComponentInParent<InventorySlot>();
        for (int i=0;i<_slots.Length;++i) if (_slots[i]==slot) return i;
        return -1;
    }
    private void ReadMouseEvent(InputEventPtr input, InputDevice device)
    {
        if (device is not Mouse mouse || (!input.IsA<StateEvent>() && !input.IsA<DeltaStateEvent>())) return;
        if (!IsOpen || !Client3DController.UiInputAllowed || EventSystem.current?.currentInputModule?.isActiveAndEnabled!=true) { _mouseHeld=false; _mouseSource=-1; return; }
        if (mouse.position.ReadValueFromEvent(input,out Vector2 position)) _mousePosition=position;
        if (!mouse.leftButton.ReadValueFromEvent(input,out float button)) return;
        bool held=button>0.5f;
        if (held && !_mouseHeld)
        {
            _mouseStart=_mousePosition; _mouseCategory=Category;
            _mouseSource=!Busy && !_recovery.RequiresRefresh && Data!=null ? MouseSlot(_mousePosition) : -1;
            if (_mouseSource>=0) _mouseEntry=Data.Entries[Category*24+_mouseSource];
        }
        else if (!held && _mouseHeld && _mouseSource>=0)
        {
            int source=_mouseSource, target=MouseSlot(_mousePosition);
            int threshold=EventSystem.current?.pixelDragThreshold ?? 5;
            // 같은 프레임에 끝난 드래그도 실제 누름·해제 위치로 복원해 UI의 마지막 위치에 합쳐지지 않도록 한다.
            if (target>=0 && target!=source && (_mousePosition-_mouseStart).sqrMagnitude>=threshold*threshold)
            {
                _rawDragFrame=Time.frameCount;
                if(Category==_mouseCategory && Data!=null)
                {
                    var entry=Data.Entries[Category*24+source];
                    if (entry.ItemId==_mouseEntry.ItemId && entry.Quantity==_mouseEntry.Quantity && entry.InstanceId==_mouseEntry.InstanceId)
                    { BeginDrag(source); Drop(target); }
                }
            }
            _mouseSource=-1;
        }
        _mouseHeld=held;
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
        _mouseSource=-1; _mouseHeld=open && Mouse.current?.leftButton.isPressed==true;
        if (_equipment!=null) _equipment.SetActive(open);
        if (open) { _panel.SetAsLastSibling(); _equipment?.transform.SetAsLastSibling(); }
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
        // 드롭·부활 snapshot은 슬롯을 갱신하되 진행 중인 다른 요청의 완료로 취급하지 않는다.
        bool completes=data.RequestId!=0 && data.RequestId==_pendingRequestId;
        if (completes || !Busy) _recovery.Accept(data.Result, Time.unscaledTimeAsDouble);
        DragSource=-1;
        if (data.Result!=InventoryResult.StorageError && data.Result!=InventoryResult.RateLimited) Data=data;
        _status.text = data.Result switch
        {
            InventoryResult.Success => "Drag items to move / merge. Double-click a potion to use.",
            InventoryResult.StaleRevision => "Inventory refreshed. Try again.",
            InventoryResult.CannotUse => "This item cannot be used, or HP / MP is already full.",
            InventoryResult.StorageError => "Inventory storage unavailable. Please try again.",
            InventoryResult.RateLimited => "Please wait a moment before the next action.",
            InventoryResult.Full => "No free equipment inventory slot.", _ => "Choose a valid item and slot."
        };
        Refresh();
    }
    public void Fail(string message) { _recovery.Fail(Time.unscaledTimeAsDouble); DragSource = -1; _status.text = message; Refresh(); }
    public void RefreshFromServer() => Request(InventoryOperation.Refresh, 0, 0);
    private void SelectCategory(int category) { Category = category; _selected = -1; DragSource = -1; Refresh(); }
    public void Select(int index, bool use)
    {
        if (Busy || Data == null) return;
        _selected = index; Refresh(); if (use) Request(Category==0 ? InventoryOperation.Equip : InventoryOperation.Use, index, 0);
    }
    public void BeginDrag(int index) { if (!Busy && !_recovery.RequiresRefresh && Data != null && Data.Entries[Category * 24 + index].ItemId != 0) { DragSource = index; _selected = index; Refresh(); } }
    public void EndDrag() { DragSource = -1; }
    private void Unequip(int slot)
    {
        if (Data==null) return;
        for (int target=0;target<24;++target) if (Data.Entries[target].ItemId==0) { Request(InventoryOperation.Unequip,slot,target,0); return; }
        _status.text="No free equipment inventory slot.";
    }
    public void Drop(int target) { if (DragSource >= 0 && target != DragSource) { int source = DragSource; DragSource = -1; Request(InventoryOperation.Move, source, target); } }
    private void Request(InventoryOperation operation, int source, int target, int category=-1)
    {
        if(operation!=InventoryOperation.Refresh && !Client3DController.UiInputAllowed) return;
        if (source < 0 || source >= 24 || target < 0 || target >= 24 || !_recovery.Begin(operation, Time.unscaledTimeAsDouble)) return;
        // 요청 중에는 추가 조작을 막고 서버 응답에서만 표시 상태를 갱신한다.
        _status.text = "Updating inventory..."; Refresh();
        _pendingRequestId=++_nextRequestId;
        if (Requested != null) Requested(operation, (byte)(category<0 ? Category : category), (byte)source, (byte)target,_pendingRequestId);
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
        if (Category==0 && item!=0) _description.text += "\nInstance: "+Data.Entries[_selected].InstanceId;
        bool canEquip=!Busy && !_recovery.RequiresRefresh && Data!=null && Data.Hp>0;
        _equip.interactable=canEquip && Category==0 && (item==1 || item==2);
        for (int i=0;i<2;++i)
        {
            var entry=Data==null ? default : Data.Equipped[i];
            _equippedLabels[i].text=(i==0 ? "WEAPON" : "HAT")+"\n"+(entry.ItemId==0 ? "Not equipped" : ItemCatalog.Name(entry.ItemId));
            _unequip[i].interactable=canEquip && entry.ItemId!=0;
        }
        _use.interactable = !Busy && !_recovery.RequiresRefresh && Data != null && Data.Hp > 0 && Category == 1 && (item == 101 || item == 102);
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
        if (panel!=null && panel.Find("Equip")==null) ButtonAt(panel,"Equip",new(122,405,105,28),"EQUIP",Accent);
        if (root.transform.Find("Equipment Panel")==null)
        {
            var equipment=Node(root.transform,"Equipment Panel",new(445,105,175,230),Paper);
            Label(equipment,"Title",new(12,6,150,25),"EQUIPMENT",15,FontStyle.Bold);
            for (int i=0;i<2;++i)
            {
                Label(equipment,"Item "+i,new(12,37+i*94,150,46),"Not equipped",12);
                ButtonAt(equipment,"Remove "+i,new(12,84+i*94,150,28),"UNEQUIP",Accent);
            }
        }
    }
    public bool ApplyVitals(VitalsData vitals)
    {
        // 생명력만 받은 경우 슬롯이 중간에 바뀌지 않았음을 변경 번호로 확인한다.
        if (Data==null || _recovery.RequiresRefresh) { RefreshFromServer(); return false; }
        if (vitals.Revision<Data.Revision) return false;
        if (vitals.Revision>Data.Revision+1) { RefreshFromServer(); return false; }
        Data.Hp=vitals.Hp; Data.MaxHp=vitals.MaxHp; Data.Mp=vitals.Mp; Data.MaxMp=vitals.MaxMp; Data.Revision=vitals.Revision; Refresh(); return true;
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
