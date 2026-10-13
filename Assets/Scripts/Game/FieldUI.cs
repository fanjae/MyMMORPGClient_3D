using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

// 로그인·채팅·필드 조작·ESC 메뉴를 씬에 저장한 UGUI에서 처리한다.
public sealed class FieldUI : MonoBehaviour
{
    public event Action<string,string,string,string> LoginRequested;
    public event Action<uint> CharacterRequested, MapRequested;
    public event Action<ushort> ChannelRequested;
    public event Action<string> ChatRequested;
    public event Action PreviewRequested, InventoryRequested, DisconnectRequested, ChannelsRequested, ReviveRequested, PickupRequested, QuitRequested;
    private GameObject _login, _controls, _chat, _menu;
    private InputField _host, _port, _account, _password, _chatInput;
    private Text _status, _field, _channelStatus, _chatHistory, _life;
    private Button _loginButton, _revive;
    private readonly Button[] _channels = new Button[3];
    private readonly Button[] _characters = new Button[6];
    private uint[] _characterIds = new uint[6];
    private CharacterInfo[] _lastCharacters;
    public bool MenuOpen => _menu != null && _menu.activeSelf;
    public bool TextFocused => EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null &&
        EventSystem.current.currentSelectedGameObject.TryGetComponent<InputField>(out var input) && input.isFocused;
    private void Awake()
    {
        _login=transform.Find("Login").gameObject; _controls=transform.Find("Controls").gameObject;
        _chat=transform.Find("Chat").gameObject; _menu=transform.Find("Menu Overlay").gameObject;
        _status=transform.Find("Status").GetComponent<Text>(); _field=transform.Find("Controls/Field").GetComponent<Text>();
        _host=_login.transform.Find("Host").GetComponent<InputField>(); _port=_login.transform.Find("Port").GetComponent<InputField>();
        _account=_login.transform.Find("Account").GetComponent<InputField>(); _password=_login.transform.Find("Password").GetComponent<InputField>();
        _chatInput=_chat.transform.Find("Input").GetComponent<InputField>(); _chatHistory=_chat.transform.Find("History").GetComponent<Text>();
        _loginButton=Bind(_login.transform,"Connect",()=>LoginRequested?.Invoke(_host.text,_port.text,_account.text,_password.text));
        Bind(_login.transform,"Preview",()=>PreviewRequested?.Invoke());
        for (int i=0;i<6;++i) { int index=i; _characters[i]=Bind(_login.transform,"Character "+i,()=>CharacterRequested?.Invoke(_characterIds[index])); _characters[i].gameObject.SetActive(false); }
        Bind(_controls.transform,"Inventory",()=>InventoryRequested?.Invoke()); Bind(_controls.transform,"Menu",ToggleMenu);
        Bind(_controls.transform,"Map A",()=>MapRequested?.Invoke(100000002)); Bind(_controls.transform,"Map B",()=>MapRequested?.Invoke(100000003));
        Bind(_controls.transform,"Pickup",()=>PickupRequested?.Invoke());
        Bind(_chat.transform,"Send",SendChat);
        _chatInput.onEndEdit.AddListener(_=> { if (UnityEngine.InputSystem.Keyboard.current?.enterKey.wasPressedThisFrame==true) SendChat(); });
        var panel=_menu.transform.Find("Panel"); _channelStatus=panel.Find("Channel Status").GetComponent<Text>(); _life=panel.Find("Life").GetComponent<Text>();
        for (int i=0;i<3;++i) { ushort channel=(ushort)(i+1); _channels[i]=Bind(panel,"Channel "+channel,()=>ChannelRequested?.Invoke(channel)); }
        Bind(panel,"Refresh",()=>ChannelsRequested?.Invoke()); _revive=Bind(panel,"Revive",()=>ReviveRequested?.Invoke());
        Bind(panel,"Disconnect",()=> { CloseMenu(); DisconnectRequested?.Invoke(); });
        Bind(panel,"Quit",()=>QuitRequested?.Invoke()); Bind(panel,"Close",CloseMenu);
        _menu.SetActive(false); _controls.SetActive(false); _chat.SetActive(false);
    }
    private static Button Bind(Transform parent,string name,Action action)
    { var button=parent.Find(name).GetComponent<Button>(); button.onClick.AddListener(()=>action()); return button; }
    public void ClearFocus() { if (EventSystem.current!=null) EventSystem.current.SetSelectedGameObject(null); }
    public void ToggleMenu()
    {
        ClearFocus(); _menu.SetActive(!MenuOpen);
        if (MenuOpen) { _menu.transform.SetAsLastSibling(); ChannelsRequested?.Invoke(); }
    }
    public void CloseMenu() { _menu.SetActive(false); ClearFocus(); }
    public void SetState(string status,uint localId,uint mapId,ushort channel,bool offline,bool busy,InventoryData inventory,CharacterInfo[] characters)
    {
        SetText(_status,status); bool inField=localId!=0;
        _login.SetActive(!inField); _controls.SetActive(inField); _chat.SetActive(inField && !offline); _loginButton.interactable=!busy;
        SetText(_field,$"{(offline ? "Preview" : "Channel "+channel)}  |  Field {mapId}\nWASD: move  Space: jump  J/Ctrl: attack  Z: loot");
        bool dead=inventory!=null && inventory.Hp==0; _revive.interactable=inField && dead && !busy && !offline;
        SetText(_life,dead ? "You are defeated. Revive after 3 seconds." : "ESC: close menu   |   Channel switch: grounded only");
        foreach (var button in _channels) button.interactable=inField && !offline && !busy && !dead;
        if (ReferenceEquals(characters,_lastCharacters)) return;
        _lastCharacters=characters;
        for (int i=0;i<6;++i)
        {
            bool present=i<characters.Length; _characters[i].gameObject.SetActive(present);
            if (present) { _characterIds[i]=characters[i].CharacterId; SetText(_characters[i].GetComponentInChildren<Text>(),$"Enter {characters[i].Name} (Lv {characters[i].Level})"); }
        }
    }
    public void ShowChannels(ChannelData data)
    {
        SetText(_channelStatus,$"Current: {data.Current}   |   {data.Result}");
        foreach (var channel in data.Channels) SetText(_channels[channel.Id-1].GetComponentInChildren<Text>(),$"CH {channel.Id}\n{channel.Players} players");
    }
    public void ShowChat(string text) { SetText(_chatHistory,text); }
    private void SendChat() { string text=_chatInput.text; if (string.IsNullOrWhiteSpace(text)) return; ChatRequested?.Invoke(text); _chatInput.text=""; ClearFocus(); }
    private static void SetText(Text text,string value) { if (text.text!=value) text.text=value; }
    public static void EnsureLayout(GameObject canvas)
    {
        if (canvas.transform.Find("Field UI")!=null) return;
        var root=new GameObject("Field UI",typeof(RectTransform)); root.transform.SetParent(canvas.transform,false);
        var rect=root.GetComponent<RectTransform>(); rect.anchorMin=Vector2.zero; rect.anchorMax=Vector2.one; rect.offsetMin=rect.offsetMax=Vector2.zero;
        Color paper=new(0.08f,0.13f,0.2f,0.94f), blue=new(0.19f,0.36f,0.55f), gold=new(0.93f,0.51f,0.18f);
        Label(root.transform,"Status",new(18,14,660,50),"Connect or choose preview",15);
        var login=Node(root.transform,"Login",new(18,80,365,580),paper);
        Label(login,"Title",new(18,14,320,30),"MY MMORPG",21);
        Label(login,"Host label",new(18,52,200,20),"Server / Login port",13);
        Input(login,"Host",new(18,78,225,32),"127.0.0.1"); Input(login,"Port",new(250,78,96,32),"7776");
        Label(login,"Account label",new(18,119,320,20),"Account",13); Input(login,"Account",new(18,145,328,32),"test");
        Label(login,"Password label",new(18,186,320,20),"Password",13); Input(login,"Password",new(18,212,328,32),"",true);
        ButtonAt(login,"Connect",new(18,258,328,34),"CONNECT AND LOGIN",gold);
        ButtonAt(login,"Preview",new(18,300,328,30),"OFFLINE PREVIEW",blue);
        for (int i=0;i<6;++i) ButtonAt(login,"Character "+i,new(18,340+i*36,328,31),"Character",blue);
        var controls=Node(root.transform,"Controls",new(738,14,344,166),paper);
        Label(controls,"Field",new(12,7,320,44),"Field",12);
        ButtonAt(controls,"Inventory",new(12,57,152,30),"INVENTORY [I]",blue); ButtonAt(controls,"Menu",new(178,57,152,30),"MENU [ESC]",gold);
        ButtonAt(controls,"Map A",new(12,96,99,28),"FIELD A",blue); ButtonAt(controls,"Map B",new(121,96,99,28),"FIELD B",blue);
        ButtonAt(controls,"Pickup",new(230,96,100,28),"LOOT [Z]",blue);
        Label(controls,"Note",new(12,132,320,25),"Move close to attack or pick up items",11);
        var chat=Node(root.transform,"Chat",new(0,0,420,160),paper); chat.anchorMin=chat.anchorMax=new(0,0); chat.pivot=new(0,0); chat.anchoredPosition=new(16,116);
        Label(chat,"Title",new(12,8,390,20),"FIELD CHAT   /m characterId message",12);
        Label(chat,"History",new(12,31,394,80),"",12); Input(chat,"Input",new(12,118,319,30),""); ButtonAt(chat,"Send",new(339,118,67,30),"SEND",blue);
        var overlay=Node(root.transform,"Menu Overlay",new(0,0,1100,720),new(0,0,0,0.5f)); overlay.anchorMin=Vector2.zero; overlay.anchorMax=Vector2.one; overlay.offsetMin=overlay.offsetMax=Vector2.zero;
        var menu=Node(overlay,"Panel",new(0,0,450,420),paper); menu.anchorMin=menu.anchorMax=new(0.5f,0.5f); menu.pivot=new(0.5f,0.5f); menu.anchoredPosition=Vector2.zero;
        Label(menu,"Title",new(20,14,410,32),"CHANNEL & GAME MENU",20);
        for (int i=0;i<3;++i) ButtonAt(menu,"Channel "+(i+1),new(20+i*140,64,130,52),"CH "+(i+1),blue);
        Label(menu,"Channel Status",new(20,125,410,28),"Connect to view channels",13);
        ButtonAt(menu,"Refresh",new(20,163,410,30),"REFRESH CHANNELS",blue);
        Label(menu,"Life",new(20,205,410,42),"",12);
        ButtonAt(menu,"Revive",new(20,255,410,32),"REVIVE AT FIELD SPAWN",blue);
        ButtonAt(menu,"Disconnect",new(20,297,198,34),"RETURN TO LOGIN",blue); ButtonAt(menu,"Quit",new(232,297,198,34),"EXIT GAME",new(0.65f,0.2f,0.22f));
        ButtonAt(menu,"Close",new(20,351,410,34),"CLOSE [ESC]",gold);
        root.AddComponent<FieldUI>();
    }
    private static RectTransform Node(Transform parent,string name,Rect bounds,Color color)
    {
        var obj=new GameObject(name,typeof(RectTransform),typeof(Image)); obj.transform.SetParent(parent,false);
        var rect=obj.GetComponent<RectTransform>(); rect.anchorMin=rect.anchorMax=new(0,1); rect.pivot=new(0,1); rect.anchoredPosition=new(bounds.x,-bounds.y); rect.sizeDelta=bounds.size; obj.GetComponent<Image>().color=color; return rect;
    }
    private static Text Label(Transform parent,string name,Rect bounds,string value,int size)
    {
        var rect=Node(parent,name,bounds,Color.clear); UnityEngine.Object.DestroyImmediate(rect.GetComponent<Image>());
        var text=rect.gameObject.AddComponent<Text>(); text.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); text.text=value; text.fontSize=size;
        text.color=Color.white; text.raycastTarget=false; text.alignment=TextAnchor.MiddleLeft; return text;
    }
    private static Button ButtonAt(Transform parent,string name,Rect bounds,string value,Color color)
    {
        var rect=Node(parent,name,bounds,color); var button=rect.gameObject.AddComponent<Button>();
        Label(rect,"Label",new(4,0,bounds.width-8,bounds.height),value,13).alignment=TextAnchor.MiddleCenter; return button;
    }
    private static InputField Input(Transform parent,string name,Rect bounds,string initial,bool password=false)
    {
        var rect=Node(parent,name,bounds,new(0.94f,0.97f,1)); var input=rect.gameObject.AddComponent<InputField>();
        var text=Label(rect,"Text",new(8,0,bounds.width-16,bounds.height),initial,14); text.color=new(0.1f,0.15f,0.22f); input.textComponent=text;
        input.contentType=password ? InputField.ContentType.Password : InputField.ContentType.Standard; input.text=initial; return input;
    }
}
