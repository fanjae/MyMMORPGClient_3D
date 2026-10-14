using UnityEngine;
using System;

public sealed class Monster3DView : MonoBehaviour
{
    public uint Id { get; private set; }
    public FieldMonsterData State { get; private set; }
    private Transform _body, _label;
    private TextMesh _name, _damage;
    private readonly GameObject[] _eyeObjects = new GameObject[2];
    private double _damageUntil, _deadUntil;
    public bool PreviewRespawnReady => Time.unscaledTimeAsDouble >= _deadUntil;
    private Camera _camera;
    private readonly MonsterMotion3D _motion = new();
    private bool _ownsMaterials;
    private static int _instances;
    public static int LiveCount => _instances;
    public static uint CreatedCount { get; private set; }
    public static uint DestroyedCount { get; private set; }
    private static readonly Material[] BodyMaterials = new Material[3];
    private static Material _eyes;
    public static int SharedMaterialCount => (BodyMaterials[0]!=null ? 1 : 0)+(BodyMaterials[1]!=null ? 1 : 0)+(BodyMaterials[2]!=null ? 1 : 0)+(_eyes!=null ? 1 : 0);
    private static readonly Color[] Colors = { new(0.41f, 0.83f, 0.28f), new(0.37f, 0.72f, 0.93f), new(0.98f, 0.64f, 0.35f) };

    public void Initialize(uint id, byte kind)
    {
        Id = id; ++_instances; ++CreatedCount; _ownsMaterials = true;
        // 같은 종류의 몸체와 눈 재질을 공유해 몬스터 수에 비례한 재질 생성을 줄인다.
        int color = kind % 3;
        if (BodyMaterials[color] == null) BodyMaterials[color] = Player3DView.CreateMaterial(Colors[color]);
        if (_eyes == null) _eyes = Player3DView.CreateMaterial(new Color(0.08f, 0.15f, 0.13f));
        var body = GameObject.CreatePrimitive(PrimitiveType.Sphere); body.name = "Slime body"; body.transform.SetParent(transform, false);
        _body = body.transform; _body.localScale = new(0.85f, 0.65f, 0.8f); _body.localPosition = new(0, 0.33f, 0);
        // 표시용 몬스터는 클라이언트 충돌 계산에 참여하지 않고 서버 위치를 따른다.
        Destroy(body.GetComponent<Collider>()); body.GetComponent<Renderer>().sharedMaterial = BodyMaterials[color];
        for (int i = 0; i < 2; ++i)
        {
            var eye = GameObject.CreatePrimitive(PrimitiveType.Sphere); eye.name = "Eye"; eye.transform.SetParent(transform, false);
            _eyeObjects[i] = eye;
            eye.transform.localPosition = new(i == 0 ? -0.17f : 0.17f, 0.45f, 0.35f); eye.transform.localScale = new(0.08f, 0.11f, 0.06f);
            Destroy(eye.GetComponent<Collider>()); eye.GetComponent<Renderer>().sharedMaterial = _eyes;
        }
        var label = new GameObject("Name").AddComponent<TextMesh>(); _label = label.transform; _label.SetParent(transform, false); _label.localPosition = new(0, 1.0f, 0);
        label.text = "Slime " + id; label.characterSize = 0.08f; label.fontSize = 36; label.anchor = TextAnchor.MiddleCenter; label.color = Color.white;
        _name = label;
        _damage = new GameObject("Damage").AddComponent<TextMesh>(); _damage.transform.SetParent(transform, false);
        _damage.characterSize = 0.12f; _damage.fontSize = 36; _damage.anchor = TextAnchor.MiddleCenter; _damage.color = new(1, 0.77f, 0.25f); _damage.text = "";
        _camera = Camera.main;
    }
    public void Apply(FieldMonsterData state, ulong tick = 0)
    {
        // 공격 결과보다 늦게 온 목록은 몬스터 HP를 되돌리지 않는다.
        if (State.Revision > state.Revision) { state.Hp = State.Hp; state.MaxHp = State.MaxHp; state.Revision = State.Revision; }
        if (state.Hp == 0 && State.Hp != 0) _deadUntil = Time.unscaledTimeAsDouble + 5;
        // 순찰 위치만 바뀌면 이름과 표시 오브젝트를 다시 갱신하지 않는다.
        if (State.Revision == 0 || State.Hp != state.Hp || State.MaxHp != state.MaxHp)
        {
            bool alive = state.Hp > 0;
            _body.gameObject.SetActive(alive); foreach (var eye in _eyeObjects) eye.SetActive(alive);
            _name.text = alive ? $"Slime {Id}\nHP {state.Hp} / {state.MaxHp}" : $"Slime {Id}\nRespawning...";
        }
        State = state; _motion.Apply(state, tick, Time.unscaledTimeAsDouble);
        RenderPose();
    }
    public void ShowDamage(ushort damage) { _damage.text = damage.ToString(); _damageUntil = Time.unscaledTimeAsDouble + 0.7; }
    // 명시한 표시 부하 검사에서만 이름·피해 숫자의 렌더링 조건을 나누어 비교한다.
    public void SetLoadPresentation(bool names,bool damage) { _label.gameObject.SetActive(names); _damage.gameObject.SetActive(damage); }
    private void RenderPose()
    {
        var pose = _motion.Sample(Time.unscaledTimeAsDouble);
        transform.position = new((float)pose.X / 20, (float)pose.Y / 20, (float)pose.Z / 20);
        transform.rotation = Quaternion.Euler(0, (float)pose.Yaw, 0);
    }
    private void Update()
    {
        if (!_motion.Ready) return;
        RenderPose();
        _body.localScale = new(0.85f, 0.65f + Mathf.Sin(Time.unscaledTime * 4 + Id) * 0.03f, 0.8f);
        // 이름 Transform과 카메라를 매 프레임 검색하지 않고 생성 시 연결한 값을 사용한다.
        if (_camera == null) _camera = Camera.main;
        _damage.transform.localPosition = new(0, 1.4f + (float)Math.Max(0, 0.7 - (_damageUntil - Time.unscaledTimeAsDouble)), 0);
        if (Time.unscaledTimeAsDouble >= _damageUntil && _damage.text.Length > 0) _damage.text = "";
        if (_camera != null) { _label.rotation = _camera.transform.rotation; _damage.transform.rotation = _camera.transform.rotation; }
    }
    private void OnDestroy()
    {
        if (!_ownsMaterials) return;
        ++DestroyedCount;
        if (--_instances != 0) return;
        foreach (var material in BodyMaterials) if (material != null) Destroy(material);
        if (_eyes != null) Destroy(_eyes);
        for (int i = 0; i < BodyMaterials.Length; ++i) BodyMaterials[i] = null;
        _eyes = null;
    }
}
