using UnityEngine;
using UnityEngine.EventSystems;
public sealed class InventoryDragHandle : MonoBehaviour, IDragHandler
{
    public void OnDrag(PointerEventData eventData)
    {
        var panel = (RectTransform)transform.parent; var canvas = GetComponentInParent<Canvas>();
        // 화면 픽셀 이동량을 Canvas 좌표로 변환해 해상도에 따라 드래그 속도가 달라지지 않도록 한다.
        panel.anchoredPosition += eventData.delta / canvas.scaleFactor;
        var area = ((RectTransform)panel.parent).rect;
        // 창 전체가 Canvas 영역 안에 남도록 위치를 제한한다.
        panel.anchoredPosition = new(Mathf.Clamp(panel.anchoredPosition.x, 0, Mathf.Max(0, area.width - panel.rect.width)),
            Mathf.Clamp(panel.anchoredPosition.y, -Mathf.Max(0, area.height - panel.rect.height), 0));
    }
}
