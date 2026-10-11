using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public sealed class InventorySlot : MonoBehaviour, IPointerClickHandler, IBeginDragHandler, IEndDragHandler, IDropHandler, IDragHandler
{
    private InventoryWindow _owner; private int _index; private Text _quantity; private Image _background; private InventoryItemIcon _icon;
    public void Bind(InventoryWindow owner, int index)
    {
        _owner = owner; _index = index; _quantity = transform.Find("Quantity").GetComponent<Text>();
        _background = GetComponent<Image>(); _icon = transform.Find("Icon").GetComponent<InventoryItemIcon>();
    }
    public void Render(InventoryEntry entry, bool selected, bool busy)
    {
        _icon.ItemId = entry.ItemId; _quantity.text = entry.Quantity > 1 ? entry.Quantity.ToString() : "";
        _background.color = selected ? new Color(1, 0.81f, 0.43f) : new Color(0.87f, 0.94f, 0.93f);
        _icon.color = busy ? new Color(1, 1, 1, 0.5f) : Color.white;
    }
    // 단일 클릭은 선택하고 더블 클릭은 선택 슬롯의 사용 요청을 전달한다.
    public void OnPointerClick(PointerEventData eventData) { if (eventData.button == PointerEventData.InputButton.Left) _owner.Select(_index, eventData.clickCount >= 2); }
    public void OnBeginDrag(PointerEventData eventData) { if (eventData.button == PointerEventData.InputButton.Left) _owner.BeginDrag(_index); }
    // 슬롯 자체는 움직이지 않고 드래그 출발·도착 번호만 InventoryWindow에서 관리한다.
    public void OnDrag(PointerEventData eventData) { }
    public void OnEndDrag(PointerEventData eventData) => _owner.EndDrag();
    public void OnDrop(PointerEventData eventData) { if (eventData.pointerDrag != null && eventData.pointerDrag.GetComponent<InventorySlot>() != null) _owner.Drop(_index); }
}
