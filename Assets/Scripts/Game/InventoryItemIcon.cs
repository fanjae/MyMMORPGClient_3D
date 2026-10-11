using UnityEngine;
using UnityEngine.UI;

// 별도 이미지 자산 없이 슬롯 안에 간단한 아이템 모양을 메시로 그린다.
public sealed class InventoryItemIcon : Graphic
{
    private uint _itemId;
    // 아이템이 바뀐 경우에만 메시를 다시 그리도록 표시 갱신을 요청한다.
    public uint ItemId { get => _itemId; set { if (_itemId != value) { _itemId = value; SetVerticesDirty(); } } }
    protected override void Awake() { base.Awake(); raycastTarget = false; }
    protected override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear(); if (_itemId == 0) return;
        void Box(float x, float y, float w, float h, Color fill)
        {
            int start = mesh.currentVertCount; var rect = rectTransform.rect;
            fill.a *= color.a;
            mesh.AddVert(new(rect.xMin + x, rect.yMin + y), fill, Vector2.zero); mesh.AddVert(new(rect.xMin + x + w, rect.yMin + y), fill, Vector2.zero);
            mesh.AddVert(new(rect.xMin + x + w, rect.yMin + y + h), fill, Vector2.zero); mesh.AddVert(new(rect.xMin + x, rect.yMin + y + h), fill, Vector2.zero);
            mesh.AddTriangle(start, start + 1, start + 2); mesh.AddTriangle(start, start + 2, start + 3);
        }
        Color brown = new(0.48f, 0.28f, 0.12f), silver = new(0.72f, 0.82f, 0.91f);
        switch (_itemId)
        {
            case 1: Box(16, 12, 7, 23, silver); Box(9, 10, 21, 5, brown); Box(16, 2, 7, 10, brown); break;
            case 2: Box(7, 10, 25, 14, new(0.32f, 0.53f, 0.8f)); Box(12, 23, 15, 7, silver); Box(3, 7, 32, 5, silver); break;
            case 101: case 102:
                Box(8, 4, 23, 23, silver); Box(11, 7, 17, 15, _itemId == 101 ? new Color(0.96f, 0.27f, 0.3f) : new Color(0.21f, 0.57f, 0.99f));
                Box(14, 26, 11, 6, silver); Box(12, 31, 15, 5, brown); Box(13, 14, 3, 7, Color.white); break;
            case 201: Box(7, 6, 5, 26, brown); Box(7, 14, 26, 5, brown); Box(28, 4, 5, 12, brown); Box(8, 25, 23, 5, new(0.82f, 0.58f, 0.3f)); break;
            case 301: Box(7, 8, 26, 19, brown); Box(12, 20, 15, 12, new(0.95f, 0.63f, 0.32f)); Box(14, 9, 8, 15, new(0.81f, 0.43f, 0.19f)); break;
            case 302: Box(16, 2, 3, 31, brown); Box(8, 15, 21, 9, silver); Box(12, 23, 15, 10, Color.white); break;
            default: Box(13, 4, 12, 29, new(1, 0.71f, 0.1f)); Box(5, 12, 28, 12, new(1, 0.71f, 0.1f)); Box(14, 14, 10, 10, Color.white); break;
        }
    }
}
