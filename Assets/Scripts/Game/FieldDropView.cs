using UnityEngine;

// 드롭은 표시만 담당하고 획득 여부와 아이템 지급은 서버에서 확정한다.
public sealed class FieldDropView : MonoBehaviour
{
    public DropData State { get; private set; }
    private TextMesh _label;
    private Camera _camera;
    private static Material _material;
    private static int _instances;
    public void Initialize(DropData state)
    {
        ++_instances;
        if (_material==null) _material=Player3DView.CreateMaterial(new(1,0.72f,0.18f));
        var body=GameObject.CreatePrimitive(PrimitiveType.Sphere); body.transform.SetParent(transform,false);
        body.transform.localScale=new(0.25f,0.25f,0.25f); body.transform.localPosition=new(0,0.18f,0); Destroy(body.GetComponent<Collider>()); body.GetComponent<Renderer>().sharedMaterial=_material;
        _label=new GameObject("Item label").AddComponent<TextMesh>(); _label.transform.SetParent(transform,false); _label.transform.localPosition=new(0,0.65f,0);
        _label.characterSize=0.06f; _label.fontSize=32; _label.anchor=TextAnchor.MiddleCenter; _label.color=new(1,0.85f,0.35f);
        _camera=Camera.main; Apply(state);
    }
    public void Apply(DropData state)
    {
        if (State.Id!=state.Id || State.Quantity!=state.Quantity) _label.text=$"{ItemCatalog.Name(state.ItemId)} x{state.Quantity}\n[Z] Pick up";
        State=state; transform.position=new((float)state.X/20,(float)state.Y/20,(float)state.Z/20);
    }
    private void LateUpdate() { if (_camera!=null) _label.transform.rotation=_camera.transform.rotation; }
    private void OnDestroy() { if (--_instances==0 && _material!=null) { Destroy(_material); _material=null; } }
}
