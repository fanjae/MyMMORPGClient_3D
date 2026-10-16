using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

// 제안은 서버 상태로 표시하고 내용이 바뀌면 양쪽 최종 확인을 다시 받는다.
public sealed class TradeWindow3D : MonoBehaviour
{
    private NetworkManager3D _network; private InventoryWindow _inventory;
    private GameObject _panel, _launcher; private Text _status, _mine, _theirs, _networkText;
    private Dropdown _peers, _offered; private InputField _quantity, _mesos;
    private readonly Dictionary<uint,string> _names=new(); private readonly List<uint> _ids=new();
    private readonly List<TradeSlotData> _draft=new();
    private TradeStateData _state; private uint _local; private bool _waiting; private double _deadline;
    private string _recoveryKey; private bool _restore;
    private double _nextNetworkAt;
    private Func<byte[],Task> _sendTrade;
    public bool IsOpen => _panel!=null && _panel.activeSelf;
    public bool Active => _state!=null && _state.Stage<TradeStage.Completed;
    public void Initialize(Transform canvas,NetworkManager3D network,InventoryWindow inventory,Func<byte[],Task> sendTrade=null)
    {
        _network=network; _inventory=inventory;
        _sendTrade=sendTrade??(request=>network.SendTradeAsync(request));
        _launcher=Button(canvas,"TRADE",738,187,155,30,()=>SetVisible(!IsOpen)).gameObject;
        _networkText=Label(canvas,738,222,350,24,"");
        _panel=Node(canvas,"Trade Panel",18,250,552,455,new Color(0.08f,0.13f,0.2f,0.97f));
        Label(_panel.transform,14,10,520,25,"PLAYER TRADE");
        _status=Label(_panel.transform,14,38,520,52,"Choose a player in this field.");
        _peers=DropdownAt(_panel.transform,14,93,258,30);
        Button(_panel.transform,"REQUEST",284,93,116,30,Invite);
        Button(_panel.transform,"ACCEPT",412,93,124,30,()=>Send(TradeOperation.Accept));
        _mine=Label(_panel.transform,14,133,252,142,""); _theirs=Label(_panel.transform,284,133,252,142,"");
        Label(_panel.transform,14,278,75,24,"Quantity"); _quantity=Input(_panel.transform,88,276,68,"1");
        Button(_panel.transform,"ADD SELECTED",166,276,155,28,AddSelected);
        Button(_panel.transform,"INVENTORY",331,276,205,28,()=>_inventory?.SetVisible(true));
        _offered=DropdownAt(_panel.transform,14,316,307,28); Button(_panel.transform,"REMOVE",331,316,205,28,Remove);
        Label(_panel.transform,14,356,65,24,"Mesos"); _mesos=Input(_panel.transform,82,354,170,"0");
        Button(_panel.transform,"SET MESOS",262,354,140,28,SetMesos);
        Button(_panel.transform,"QUERY",412,354,124,28,()=>Send(TradeOperation.Query));
        Button(_panel.transform,"FINAL CONFIRM",14,398,194,34,()=>Send(TradeOperation.Confirm));
        Button(_panel.transform,"CANCEL",220,398,152,34,()=>Send(TradeOperation.Cancel));
        Button(_panel.transform,"CLOSE",384,398,152,34,()=>SetVisible(false));
        _network.TradeReceived+=Apply; _panel.SetActive(false); _launcher.SetActive(false); _networkText.gameObject.SetActive(false);
    }
    public void SetLocal(uint id,string recoveryKey=null)
    {
        _local=id; _launcher?.SetActive(id!=0); _networkText?.gameObject.SetActive(id!=0);
        if(id==0)
        {
            _inventory?.SetTradeLocked(false); _state=null; _waiting=false; _restore=false; _draft.Clear();
            if(_mine!=null) _mine.text=""; if(_theirs!=null) _theirs.text="";
            if(_quantity!=null) _quantity.text="1"; if(_mesos!=null) _mesos.text="0";
            if(_offered!=null) { _offered.ClearOptions(); _offered.AddOptions(new List<string>{"No offered items"}); }
            SetVisible(false); ClearPeers(); return;
        }
        _recoveryKey=recoveryKey;
        if(!string.IsNullOrEmpty(_recoveryKey) && ulong.TryParse(PlayerPrefs.GetString(_recoveryKey,""),out ulong pending) && pending!=0)
        { _state=new() { Id=pending,Stage=TradeStage.Committing,Characters=new[]{id,0u} }; _inventory?.SetTradeLocked(true); _restore=true; }
    }
    public void SetPeer(uint id,string name) { if(id==_local) return; _names[id]=name; RefreshPeers(); }
    public void RemovePeer(uint id) { _names.Remove(id); RefreshPeers(); }
    public void ClearPeers() { _names.Clear(); RefreshPeers(); }
    private void RefreshPeers()
    {
        if(_peers==null) return; _ids.Clear(); _peers.ClearOptions(); var names=new List<string>();
        foreach(var pair in _names) { _ids.Add(pair.Key); names.Add(pair.Value); }
        if(names.Count==0) names.Add("No other players"); _peers.AddOptions(names);
    }
    private string Name(uint id) => id==_local ? "You" : _names.TryGetValue(id,out var name) ? name : "Other player";
    public void SetVisible(bool visible) { if(_panel==null) return; _panel.SetActive(visible); if(visible) _panel.transform.SetAsLastSibling(); }
    private void Invite() { if(_ids.Count==0 || Active) return; Send(TradeOperation.Invite,_ids[_peers.value]); }
    private bool CanEdit => _state?.Stage==TradeStage.Editing && !_waiting && _inventory?.Data!=null;
    private void AddSelected()
    {
        if(!CanEdit) return;
        int selected=_inventory.SelectedSlot; if(selected<0 || !ushort.TryParse(_quantity.text,out var quantity) || quantity==0) { _status.text="Select an inventory item and quantity."; return; }
        byte category=(byte)_inventory.Category; var entry=_inventory.Data.Entries[category*24+selected];
        if(entry.ItemId==0 || quantity>entry.Quantity) { _status.text="Not enough items in the selected slot."; return; }
        var proposed=new List<TradeSlotData>(_draft); int index=proposed.FindIndex(s=>s.Category==category && s.Slot==selected);
        var slot=new TradeSlotData { Category=category,Slot=(byte)selected,Quantity=quantity };
        if(index>=0) proposed[index]=slot; else if(proposed.Count<6) proposed.Add(slot); else { _status.text="You can offer up to six inventory slots."; return; }
        Send(TradeOperation.Offer,slots:proposed);
    }
    private void Remove() { if(!CanEdit || _draft.Count==0) return; var proposed=new List<TradeSlotData>(_draft); proposed.RemoveAt(_offered.value); Send(TradeOperation.Offer,slots:proposed); }
    private void SetMesos()
    {
        if(!CanEdit) return;
        if(!ulong.TryParse(_mesos.text,out var mesos) || mesos>_inventory.Data.Mesos) { _status.text="Enter a valid amount within your balance."; return; }
        Send(TradeOperation.Offer,mesos:mesos);
    }
    private async void Send(TradeOperation operation,uint target=0,ulong? mesos=null,IReadOnlyList<TradeSlotData> slots=null)
    {
        if(!Client3DController.UiInputAllowed || _local==0 || _waiting) return;
        int side=_state?.Side(_local)??-1;
        if(operation!=TradeOperation.Invite && operation!=TradeOperation.Query && _state==null) return;
        if(_state?.Stage==TradeStage.Committing && operation!=TradeOperation.Query) return;
        _waiting=true; _deadline=Time.unscaledTimeAsDouble+5;
        _status.text="Waiting for the server...";
        try
        {
            await _sendTrade(TradeProtocol.Request(operation,_state?.Id??0,_state?.Revision??0,target,_inventory?.Data?.Revision??0,mesos??(side>=0?_state.Offers[side].Mesos:0),slots??_draft));
        }
        catch(Exception) { if(this==null || _status==null) return; _waiting=false; _status.text="Trade response unavailable. Query before trying again."; }
    }
    private void Apply(TradeStateData state)
    {
        if(!TradeStateGate3D.CanApply(_state,state,_local)) return;
        _waiting=false;
        if(state.Id==0) { _status.text=ResultMessage(state.Result); SetVisible(true); return; }
        _state=state; int side=state.Side(_local); _draft.Clear(); foreach(var item in state.Offers[side].Items) _draft.Add(item.Source);
        _inventory?.SetTradeLocked(state.Stage==TradeStage.Editing || state.Stage==TradeStage.Committing);
        if(!string.IsNullOrEmpty(_recoveryKey))
        {
            if(state.Stage<TradeStage.Completed) PlayerPrefs.SetString(_recoveryKey,state.Id.ToString()); else PlayerPrefs.DeleteKey(_recoveryKey);
            PlayerPrefs.Save();
        }
        _mine.text=Describe(state,side); _theirs.text=Describe(state,1-side);
        _mesos.text=state.Offers[side].Mesos.ToString(); _offered.ClearOptions(); var names=new List<string>();
        foreach(var item in state.Offers[side].Items) names.Add(ItemCatalog.Name(item.ItemId)+" x"+item.Source.Quantity);
        if(names.Count==0) names.Add("No offered items"); _offered.AddOptions(names);
        _status.text=state.Result!=TradeResult.Success ? ResultMessage(state.Result) : state.Stage switch
        {
            TradeStage.Invited=>Name(state.Characters[0])+" requested a trade. The recipient can accept.",
            TradeStage.Editing=>"Review both offers. Changing either offer clears both confirmations.",
            TradeStage.Committing=>"Confirming trade. Use QUERY if the result is delayed.",
            TradeStage.Completed=>"Trade completed.",
            _=>"Trade cancelled."
        };
        SetVisible(true);
    }
    private static string ResultMessage(TradeResult result)=>result switch
    {
        TradeResult.Full=>"Trade cancelled: not enough free inventory slots.",
        TradeResult.Stale=>"The offer changed. Review both offers before confirming again.",
        TradeResult.StorageError=>"Trade result pending. QUERY checks without repeating the exchange.",
        TradeResult.Expired=>"Trade expired. Request a new trade.",
        TradeResult.Unavailable=>"Trade unavailable: a player left or combat interrupted it.",
        TradeResult.Busy=>"Trade busy. QUERY checks the current result.",
        _=>"Trade request rejected. Check the items, quantity and mesos."
    };
    private string Describe(TradeStateData state,int side)
    {
        string text=Name(state.Characters[side])+(state.Confirmed[side]?" [CONFIRMED]":"")+"\nMesos: "+state.Offers[side].Mesos;
        foreach(var item in state.Offers[side].Items) text+="\n"+ItemCatalog.Name(item.ItemId)+" x"+item.Source.Quantity;
        return text;
    }
    private void Update()
    {
        if(_restore && Client3DController.UiInputAllowed) { _restore=false; Send(TradeOperation.Query); }
        if(_waiting && Time.unscaledTimeAsDouble>=_deadline) { _waiting=false; _status.text="Result pending. QUERY checks without repeating the exchange."; }
        if(_networkText!=null && _local!=0 && Time.unscaledTimeAsDouble>=_nextNetworkAt)
        { _nextNetworkAt=Time.unscaledTimeAsDouble+0.2; _networkText.text=$"RTT {_network.Timing.RttSeconds*1000:F0}ms / buffer {_network.PlaybackBufferSeconds*1000:F0}ms"; }
    }
    private void OnDestroy() { if(_network!=null) _network.TradeReceived-=Apply; }
    private static GameObject Node(Transform parent,string name,float x,float y,float w,float h,Color color,bool image=true)
    {
        var node=new GameObject(name,typeof(RectTransform)); node.transform.SetParent(parent,false);
        var rect=(RectTransform)node.transform; rect.anchorMin=rect.anchorMax=new(0,1); rect.pivot=new(0,1); rect.anchoredPosition=new(x,-y); rect.sizeDelta=new(w,h); if(image) node.AddComponent<Image>().color=color; return node;
    }
    private static Text Label(Transform parent,float x,float y,float w,float h,string value)
    {
        var node=Node(parent,"Label",x,y,w,h,Color.clear,false); var text=node.AddComponent<Text>(); text.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); text.fontSize=13; text.color=Color.white; text.text=value; text.raycastTarget=false; return text;
    }
    private static Button Button(Transform parent,string name,float x,float y,float w,float h,Action action)
    {
        var node=Node(parent,name,x,y,w,h,new Color(0.2f,0.36f,0.54f)); var button=node.AddComponent<Button>(); var label=Label(node.transform,0,0,w,h,name); label.alignment=TextAnchor.MiddleCenter;
        button.onClick.AddListener(()=> { if(Client3DController.UiInputAllowed) action(); }); return button;
    }
    private static InputField Input(Transform parent,float x,float y,float width,string value)
    {
        var node=Node(parent,"Input",x,y,width,28,new Color(0.16f,0.23f,0.3f)); var input=node.AddComponent<InputField>(); input.textComponent=Label(node.transform,4,3,width-8,22,""); input.contentType=InputField.ContentType.IntegerNumber; input.text=value; return input;
    }
    private static Dropdown DropdownAt(Transform parent,float x,float y,float width,float height)
    {
        var node=Node(parent,"Choices",x,y,width,height,new Color(0.16f,0.23f,0.3f)); var dropdown=node.AddComponent<Dropdown>(); dropdown.captionText=Label(node.transform,5,4,width-10,height-6,"");
        var template=Node(node.transform,"Template",0,height,width,180,new Color(0.12f,0.18f,0.25f)); var scroll=template.AddComponent<ScrollRect>();
        var viewport=Node(template.transform,"Viewport",0,0,width,180,Color.clear); viewport.AddComponent<Mask>().showMaskGraphic=false;
        var content=Node(viewport.transform,"Content",0,0,width,30,Color.clear); var item=Node(content.transform,"Item",0,0,width,30,new Color(0.2f,0.3f,0.4f));
        var toggle=item.AddComponent<Toggle>(); toggle.targetGraphic=item.GetComponent<Image>();
        var check=Node(item.transform,"Check",3,8,12,12,new Color(1,0.7f,0.2f)); toggle.graphic=check.GetComponent<Image>(); dropdown.itemText=Label(item.transform,21,4,width-26,24,"");
        scroll.viewport=(RectTransform)viewport.transform; scroll.content=(RectTransform)content.transform; scroll.horizontal=false; dropdown.template=(RectTransform)template.transform; template.SetActive(false); return dropdown;
    }
}
