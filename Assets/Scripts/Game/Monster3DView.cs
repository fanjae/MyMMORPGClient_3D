using UnityEngine;
using System.Collections.Generic;

public sealed class Monster3DView : MonoBehaviour
{
    public uint Id { get; private set; }
    public FieldMonsterData State { get; private set; }
    private Vector3 _target; private Transform _body; private bool _ready;
    private readonly List<Material> _materials = new();
    public void Initialize(uint id, byte kind)
    {
        Id = id;
        var body = GameObject.CreatePrimitive(PrimitiveType.Sphere); body.name = "Slime body"; body.transform.SetParent(transform, false);
        _body = body.transform; _body.localScale = new(0.85f, 0.65f, 0.8f); _body.localPosition = new(0, 0.33f, 0);
        // 표시용 몬스터는 클라이언트 충돌 계산에 참여하지 않고 서버 위치를 따른다.
        Destroy(body.GetComponent<Collider>());
        Color[] colors = { new(0.41f, 0.83f, 0.28f), new(0.37f, 0.72f, 0.93f), new(0.98f, 0.64f, 0.35f) };
        var material = Player3DView.CreateMaterial(colors[kind % 3]); _materials.Add(material); body.GetComponent<Renderer>().sharedMaterial = material;
        for (int i = 0; i < 2; ++i)
        {
            var eye = GameObject.CreatePrimitive(PrimitiveType.Sphere); eye.name = "Eye"; eye.transform.SetParent(transform, false);
            eye.transform.localPosition = new(i == 0 ? -0.17f : 0.17f, 0.45f, 0.35f); eye.transform.localScale = new(0.08f, 0.11f, 0.06f);
            Destroy(eye.GetComponent<Collider>()); var dark = Player3DView.CreateMaterial(new Color(0.08f, 0.15f, 0.13f)); _materials.Add(dark); eye.GetComponent<Renderer>().sharedMaterial = dark;
        }
        var label = new GameObject("Name").AddComponent<TextMesh>(); label.transform.SetParent(transform, false); label.transform.localPosition = new(0, 1.0f, 0);
        label.text = "Slime " + id; label.characterSize = 0.08f; label.fontSize = 36; label.anchor = TextAnchor.MiddleCenter; label.color = Color.white;
    }
    public void Apply(FieldMonsterData state)
    {
        // 패킷의 서버 좌표를 Unity 거리로 변환하고 첫 상태나 큰 차이는 즉시 반영한다.
        State = state; _target = new((float)state.X / 20, (float)state.Y / 20, (float)state.Z / 20);
        if (!_ready || Vector3.Distance(transform.position, _target) > 2) transform.position = _target;
        _ready = true;
    }
    private void Update()
    {
        if (!_ready) return;
        // 수신 주기 사이의 위치·방향만 부드럽게 표시하고 순찰 경로를 별도로 계산하지 않는다.
        transform.position = Vector3.Lerp(transform.position, _target, 1 - Mathf.Exp(-15 * Time.unscaledDeltaTime));
        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0, State.Yaw / 100f, 0), 1 - Mathf.Exp(-12 * Time.unscaledDeltaTime));
        _body.localScale = new(0.85f, 0.65f + Mathf.Sin(Time.unscaledTime * 4 + Id) * 0.03f, 0.8f);
        var label = transform.Find("Name"); if (Camera.main != null) label.rotation = Camera.main.transform.rotation;
    }
    private void OnDestroy() { foreach (var material in _materials) if (material != null) Destroy(material); }
}
