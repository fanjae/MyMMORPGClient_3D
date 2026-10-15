using UnityEngine;
using System.Collections.Generic;

public sealed class Player3DView : MonoBehaviour
{
    public uint CharacterId { get; private set; }
    public double LastGap { get; private set; }
    public int SnapCount { get; private set; }
    public bool Local { get; private set; }
    private readonly RemoteMotion3D _remote = new();
    private Vector3 _target;
    private float _yaw;
    private Transform _weapon;
    private double _attackUntil;
    private TextMesh _damage;
    private Material _bodyMaterial;
    private Color _bodyColor;
    private double _hurtUntil;
    private bool _dead;
    private bool _combatPaused;
    private Camera _camera;
    private readonly List<Material> _materials = new();
    public void Initialize(uint id, World3DData world, bool local)
    {
        CharacterId = id; Local = local;
        _camera=Camera.main;
        GameObject capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        capsule.name = "Body"; capsule.transform.SetParent(transform, false);
        capsule.transform.localPosition = new Vector3(0, world.Height * 0.025f, 0);
        capsule.transform.localScale = new Vector3(world.Radius * 0.1f, world.Height * 0.025f, world.Radius * 0.1f);
        Destroy(capsule.GetComponent<Collider>());
        var material = CreateMaterial(local ? Color.cyan : Color.HSVToRGB((id * 0.618034f) % 1, 0.7f, 0.95f));
        _materials.Add(material);
        _bodyMaterial=material; _bodyColor=material.color;
        capsule.GetComponent<Renderer>().material = material;
        GameObject face = GameObject.CreatePrimitive(PrimitiveType.Cube);
        face.name = "Facing"; face.transform.SetParent(transform, false);
        face.transform.localPosition = new Vector3(0, world.Height * 0.04f, world.Radius * 0.055f);
        face.transform.localScale = new Vector3(0.15f, 0.15f, 0.15f);
        Destroy(face.GetComponent<Collider>());
        var faceMaterial = CreateMaterial(Color.white);
        _materials.Add(faceMaterial);
        face.GetComponent<Renderer>().material = faceMaterial;
        // 기본 공격 표시는 추가 애니메이션 자산 없이 손 옆의 검을 짧게 휘두른다.
        var sword = GameObject.CreatePrimitive(PrimitiveType.Cube); sword.name = "Basic attack sword"; sword.transform.SetParent(transform, false);
        Destroy(sword.GetComponent<Collider>()); _weapon = sword.transform; _weapon.localPosition = new(0.65f, 0.85f, 0.5f); _weapon.localScale = new(0.12f, 0.85f, 0.12f);
        var swordMaterial = CreateMaterial(new(1, 0.8f, 0.3f)); _materials.Add(swordMaterial); sword.GetComponent<Renderer>().sharedMaterial = swordMaterial;
        sword.SetActive(false);
        _damage=new GameObject("Damage").AddComponent<TextMesh>(); _damage.transform.SetParent(transform,false); _damage.transform.localPosition=new(0,2.1f,0);
        _damage.characterSize=0.1f; _damage.fontSize=36; _damage.anchor=TextAnchor.MiddleCenter; _damage.color=new(1,0.3f,0.3f); _damage.text="";
        SetLocal(new Motion3DSimulation(world).Spawn());
    }
    public void ConfigureRemote(World3DData world, Relay3DAction snapshot, double now)
    {
        _remote.Configure(world, snapshot, now);
        transform.position = Position(snapshot.Action.State);
        transform.rotation = Quaternion.Euler(0, snapshot.Action.State.Yaw / 100f, 0);
    }
    public void Add(Relay3DAction action, double now) => _remote.Add(action, now);
    public void SetLocal(Motion3DState state)
    {
        transform.position = Position(state); transform.rotation = Quaternion.Euler(0, state.Yaw / 100f, 0);
    }
    public void PlayAttack() { _attackUntil = Time.unscaledTimeAsDouble + 0.24; _weapon.gameObject.SetActive(true); }
    public void StopAttack() { if (_weapon!=null) _weapon.gameObject.SetActive(false); }
    public void PauseCombat(bool paused) { _combatPaused = paused; if (paused) StopAttack(); }
    public void SetVitals(ushort hp,ushort damage)
    {
        _dead=hp==0; if (_dead) StopAttack();
        if (_dead && damage==0) _damage.text="Defeated";
        if (damage>0) { _damage.text="-"+damage; _hurtUntil=Time.unscaledTimeAsDouble+0.7; }
        _bodyMaterial.color=_dead ? Color.gray : damage>0 ? new Color(1,0.25f,0.25f) : _bodyColor;
    }
    public void SetNetworkTiming(NetworkTiming3D timing) => _remote.SetNetworkTiming(timing);
    public double PlaybackBufferSeconds=>_remote.PlaybackBufferSeconds;
    public double ArrivalJitterSeconds=>_remote.ArrivalJitterSeconds;
    private void Update()
    {
        if (_damage!=null)
        {
            if (_camera==null) _camera=Camera.main;
            if (_camera!=null) _damage.transform.rotation=_camera.transform.rotation;
            if (Time.unscaledTimeAsDouble>=_hurtUntil)
            {
                string text=_dead ? "Defeated" : ""; if (_damage.text!=text) _damage.text=text;
                Color color=_dead ? Color.gray : _bodyColor; if (_bodyMaterial.color!=color) _bodyMaterial.color=color;
            }
        }
        if (_weapon != null && _weapon.gameObject.activeSelf)
        {
            float remaining = (float)(_attackUntil - Time.unscaledTimeAsDouble);
            if (remaining <= 0) _weapon.gameObject.SetActive(false);
            else _weapon.localRotation = Quaternion.Euler(0, Mathf.Lerp(70, -70, remaining / 0.24f), 55);
        }
        if (Local || _dead || _combatPaused || !_remote.Sample(Time.unscaledTimeAsDouble, out var state)) return;
        _target = Position(state); _yaw = state.Yaw / 100f;
        LastGap = Vector3.Distance(transform.position, _target);
        // 계산 상태를 건드리지 않고 표시 위치만 보정한다. 큰 오차와 복귀는 즉시 맞춘다.
        if (_remote.Snap || LastGap > 2)
        { transform.position = _target; ++SnapCount; }
        else transform.position = Vector3.Lerp(transform.position, _target, 1 - Mathf.Exp(-12 * Time.unscaledDeltaTime));
        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0, _yaw, 0), 1 - Mathf.Exp(-12 * Time.unscaledDeltaTime));
    }
    public static Vector3 Position(Motion3DState state) => new((float)state.X * 0.05f, (float)state.Y * 0.05f, (float)state.Z * 0.05f);
    public static Material CreateMaterial(Color color)
    {
        var template = Resources.Load<Material>("Movement3DMaterial");
        var material = template != null ? new Material(template) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
        material.color = color; return material;
    }
    private void OnDestroy()
    {
        // Renderer.material로 생성한 인스턴스는 맵 퇴장 시 함께 정리한다.
        foreach (var material in _materials) if (material != null) Destroy(material);
        _materials.Clear();
    }
}
