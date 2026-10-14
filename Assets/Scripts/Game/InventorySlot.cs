using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public sealed class InventorySlot : MonoBehaviour, IPointerClickHandler, IBeginDragHandler, IEndDragHandler, IDropHandler, IDragHandler
{
    private InventoryEntry _rendered; private bool _selected, _busy, _ready;
    private InventoryWindow _owner; private int _index; private Text _quantity; private Image _background; private InventoryItemIcon _icon;
    public void Bind(InventoryWindow owner, int index)
    {
        _owner = owner; _index = index; _quantity = transform.Find("Quantity").GetComponent<Text>();
        _background = GetComponent<Image>(); _icon = transform.Find("Icon").GetComponent<InventoryItemIcon>();
    }
    public void Render(InventoryEntry entry, bool selected, bool busy)
    {
        // 슬롯 내용과 표시 상태가 같으면 Text·Graphic을 다시 갱신하지 않는다.
        if (_ready && entry.ItemId == _rendered.ItemId && entry.Quantity == _rendered.Quantity && selected == _selected && busy == _busy) return;
        _ready = true; _rendered = entry; _selected = selected; _busy = busy;
        _icon.ItemId = entry.ItemId; _quantity.text = entry.Quantity > 1 ? entry.Quantity.ToString() : "";
        _background.color = selected ? new Color(1, 0.81f, 0.43f) : new Color(0.87f, 0.94f, 0.93f);
        _icon.color = busy ? new Color(1, 1, 1, 0.5f) : Color.white;
    }
    // 단일 클릭은 선택하고 더블 클릭은 선택 슬롯의 사용 요청을 전달한다.
    public void OnPointerClick(PointerEventData eventData) { if (eventData.button == PointerEventData.InputButton.Left && !_owner.HandledMouseDrag) _owner.Select(_index, eventData.clickCount >= 2); }
    public void OnBeginDrag(PointerEventData eventData) { if (eventData.button == PointerEventData.InputButton.Left) _owner.BeginDrag(_index); }
    // 슬롯 자체는 움직이지 않고 드래그 출발·도착 번호만 InventoryWindow에서 관리한다.
    public void OnDrag(PointerEventData eventData) { }
    public void OnEndDrag(PointerEventData eventData) => _owner.EndDrag();
    public void OnDrop(PointerEventData eventData)
    {
        if (_owner.HandledMouseDrag) return;
        // 다른 Canvas나 다른 인벤토리 창의 드래그를 현재 캐릭터의 요청으로 사용하지 않는다.
        if (eventData.pointerDrag!=null && eventData.pointerDrag.TryGetComponent<InventorySlot>(out var source) && source._owner==_owner && source._index!=_index)
            _owner.Drop(_index);
    }
}
