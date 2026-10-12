using System;
using UnityEngine;
using UnityEngine.UI;

// 하단 게이지는 인벤토리 창과 같은 서버 상태를 사용한다.
public sealed class FieldVitalsHUD : MonoBehaviour
{
    public event Action AttackRequested;
    private RectTransform _hp, _mp;
    private Text _hpText, _mpText, _hint;
    private ushort _lastHp, _lastMaxHp, _lastMp, _lastMaxMp;
    private bool _hasData;
    private void Awake()
    {
        _hp = (RectTransform)transform.Find("HP/Fill"); _mp = (RectTransform)transform.Find("MP/Fill");
        _hpText = transform.Find("HP/Value").GetComponent<Text>(); _mpText = transform.Find("MP/Value").GetComponent<Text>();
        _hint = transform.Find("Hint").GetComponent<Text>();
        transform.Find("Attack").GetComponent<Button>().onClick.AddListener(() => AttackRequested?.Invoke());
        Apply(null); SetVisible(false);
    }
    public void SetVisible(bool visible) => gameObject.SetActive(visible);
    public void Apply(InventoryData data)
    {
        if (_hp == null) return;
        if (data == null) { _hasData = false; _hp.sizeDelta = new(0, 20); _mp.sizeDelta = new(0, 20); _hpText.text = "HP  -- / --"; _mpText.text = "MP  -- / --"; return; }
        // 수치가 그대로면 Text와 게이지를 다시 갱신하지 않는다.
        if (_hasData && data.Hp == _lastHp && data.MaxHp == _lastMaxHp && data.Mp == _lastMp && data.MaxMp == _lastMaxMp) return;
        _hasData = true; _lastHp = data.Hp; _lastMaxHp = data.MaxHp; _lastMp = data.Mp; _lastMaxMp = data.MaxMp;
        _hp.sizeDelta = new(210 * VitalsFill.Ratio(data.Hp, data.MaxHp), 20); _mp.sizeDelta = new(210 * VitalsFill.Ratio(data.Mp, data.MaxMp), 20);
        _hpText.text = $"HP  {data.Hp} / {data.MaxHp}"; _mpText.text = $"MP  {data.Mp} / {data.MaxMp}";
    }
    public void Report(BasicAttackResult result) => _hint.text = result switch
    {
        BasicAttackResult.Hit => "Hit!  J / Left Ctrl: basic attack",
        BasicAttackResult.Killed => "Monster defeated.  J / Left Ctrl: basic attack",
        BasicAttackResult.Miss => "No target in front. Move closer.",
        BasicAttackResult.Cooldown => "Attack is cooling down.",
        BasicAttackResult.Unavailable => "Load character HP before attacking.",
        _ => "Attack state refreshed. Try again."
    };
    public static void EnsureLayout(GameObject canvas)
    {
        if (canvas.transform.Find("Player Vitals HUD") != null) return;
        var panel = Node(canvas.transform, "Player Vitals HUD", new(0, 0, 580, 88), new(0.09f, 0.14f, 0.21f, 0.95f));
        panel.anchorMin = panel.anchorMax = new(0.5f, 0); panel.pivot = new(0.5f, 0); panel.anchoredPosition = new(0, 14);
        var outline = panel.gameObject.AddComponent<Outline>(); outline.effectColor = new(0.33f, 0.47f, 0.61f); outline.effectDistance = new(1, -1);
        Bar(panel, "HP", new(15, 18, 210, 20), new(0.92f, 0.28f, 0.33f));
        Bar(panel, "MP", new(240, 18, 210, 20), new(0.2f, 0.55f, 0.97f));
        var attack = Node(panel, "Attack", new(467, 13, 98, 34), new(1, 0.57f, 0.19f));
        attack.gameObject.AddComponent<Button>(); Label(attack, "Label", new(0, 0, 98, 34), "ATTACK", 13);
        Label(panel, "Hint", new(15, 51, 550, 26), "J / Left Ctrl: basic attack  |  MP cost: 0", 13);
        panel.gameObject.AddComponent<FieldVitalsHUD>();
    }
    private static void Bar(Transform parent, string name, Rect rect, Color color)
    {
        var bar = Node(parent, name, rect, new(0.15f, 0.21f, 0.28f));
        Node(bar, "Fill", new(0, 0, rect.width, rect.height), color);
        Label(bar, "Value", new(0, 0, rect.width, rect.height), name + "  -- / --", 13);
    }
    private static RectTransform Node(Transform parent, string name, Rect rect, Color color)
    {
        var obj = new GameObject(name, typeof(RectTransform), typeof(Image)); obj.transform.SetParent(parent, false);
        var node = obj.GetComponent<RectTransform>(); node.anchorMin = node.anchorMax = new(0, 1); node.pivot = new(0, 1);
        node.anchoredPosition = new(rect.x, -rect.y); node.sizeDelta = rect.size; obj.GetComponent<Image>().color = color; return node;
    }
    private static void Label(Transform parent, string name, Rect rect, string content, int size)
    {
        var node = Node(parent, name, rect, Color.clear); UnityEngine.Object.DestroyImmediate(node.GetComponent<Image>());
        var text = node.gameObject.AddComponent<Text>(); text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); text.text = content;
        text.color = Color.white; text.fontSize = size; text.fontStyle = FontStyle.Bold; text.alignment = TextAnchor.MiddleCenter; text.raycastTarget = false;
    }
}
