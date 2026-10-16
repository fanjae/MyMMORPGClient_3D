using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

// 실제 UGUI 콜백과 상태 반영만 자동 검사한다. 창 조작·실제 서버 거래 검사는 별도로 기록한다.
public static class HeadlessTradeUiValidation
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,Private).GetValue(target);
    private static void Call(object target,string name,params object[] arguments)=>target.GetType().GetMethod(name,Private).Invoke(target,arguments);
    private static void Check(bool valid,string message) { if(!valid) throw new InvalidOperationException(message); }
    public static void Run()
    {
        string report=null;
        foreach(var argument in Environment.GetCommandLineArgs()) if(argument.StartsWith("--trade-ui-report=")) report=argument.Substring("--trade-ui-report=".Length);
        var inputField=typeof(Client3DController).GetField("<UiInputAllowed>k__BackingField",BindingFlags.Static|BindingFlags.NonPublic);
        bool previous=(bool)inputField.GetValue(null); string key="headless-trade-ui-"+Guid.NewGuid().ToString("N");
        GameObject root=null,host=null;
        var passes=new List<string>();
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            inputField.SetValue(null,true);
            root=new GameObject("Headless inventory",typeof(RectTransform)); InventoryWindow.BuildLayout(root);
            var inventory=root.GetComponent<InventoryWindow>();
            if(Field<RectTransform>(inventory,"_panel")==null) Call(inventory,"Awake");
            var data=new InventoryData { Revision=7,Mesos=100,Hp=50,MaxHp=100,Mp=25,MaxMp=60 };
            data.Entries[0]=new() { ItemId=1,Quantity=1,InstanceId=11 };
            for(int i=0;i<7;++i) data.Entries[24+i]=new() { ItemId=101,Quantity=10 };
            inventory.Apply(data);
            host=new GameObject("Headless trade"); var network=host.AddComponent<NetworkManager3D>(); var trade=host.AddComponent<TradeWindow3D>();
            var requests=new List<byte[]>(); trade.Initialize(root.transform,network,inventory,request=> { requests.Add(request); return Task.CompletedTask; });
            var panel=Field<GameObject>(trade,"_panel");
            void Click(string name)=>panel.transform.Find(name).GetComponent<Button>().onClick.Invoke();
            string Status()=>Field<Text>(trade,"_status").text;
            TradeStateData State(ulong revision,TradeStage stage,TradeResult result=TradeResult.Success,ulong id=10)=>new() { Id=id,Revision=revision,Stage=stage,Result=result,Characters=new[]{1002u,2001u} };
            void Apply(TradeStateData state)=>Call(trade,"Apply",state);
            trade.SetLocal(1002,key); trade.SetPeer(2001,"Other"); Click("REQUEST"); Click("REQUEST");
            Check(requests.Count==1 && requests[0][0]==(byte)TradeOperation.Invite,"Duplicate request escaped response wait");
            Apply(State(1,TradeStage.Invited)); Apply(State(2,TradeStage.Editing));
            Check(Field<bool>(inventory,"_tradeLocked"),"Editing did not lock inventory changes");
            root.transform.Find("Inventory Panel/Tab 1").GetComponent<Button>().onClick.Invoke(); inventory.Select(0,false);
            var quantity=Field<InputField>(trade,"_quantity"); quantity.text="0"; Click("ADD SELECTED"); quantity.text="11"; Click("ADD SELECTED");
            Check(requests.Count==1,"Zero/excess quantity was sent");
            quantity.text="2"; Click("ADD SELECTED");
            Check(requests.Count==2 && requests[1][37]==1 && BitConverter.ToUInt16(requests[1],40)==2,"Valid selected quantity was not serialized");
            var offered=State(3,TradeStage.Editing); offered.Offers[0].Items.Add(new() { ItemId=101,Source=new() { Category=1,Slot=0,Quantity=2 } }); Apply(offered);
            Click("REMOVE"); Check(requests.Count==3 && requests[2][37]==0,"Remove preserved the offered slot");
            Apply(State(4,TradeStage.Editing)); var mesos=Field<InputField>(trade,"_mesos"); mesos.text="101"; Click("SET MESOS");
            Check(requests.Count==3,"Mesos above balance was sent"); mesos.text="100"; Click("SET MESOS");
            Check(requests.Count==4 && BitConverter.ToUInt64(requests[3],29)==100,"Mesos limit was not serialized");
            passes.Add("PASS UGUI duplicate requests, quantity validation, offer removal and mesos limits");
            var six=State(5,TradeStage.Editing);
            for(byte i=0;i<6;++i) six.Offers[0].Items.Add(new() { ItemId=101,Source=new() { Category=1,Slot=i,Quantity=1 } });
            Apply(six); inventory.Select(6,false); quantity.text="1"; Click("ADD SELECTED");
            Check(requests.Count==4 && Status().Contains("six"),"Seventh offered slot was accepted");
            Apply(State(6,TradeStage.Committing)); Click("CANCEL"); Click("ADD SELECTED"); Check(requests.Count==4,"Committing allowed a mutation");
            Click("QUERY"); Check(requests.Count==5 && requests[4][0]==(byte)TradeOperation.Query,"Committing query blocked");
            typeof(TradeWindow3D).GetField("_deadline",Private).SetValue(trade,Time.unscaledTimeAsDouble-1); Call(trade,"Update");
            Check(Status().Contains("QUERY") && !Field<bool>(trade,"_waiting"),"Timeout omitted manual query guidance");
            Apply(State(7,TradeStage.Cancelled,TradeResult.Full)); Check(Status().Contains("free inventory slots") && !Field<bool>(inventory,"_tradeLocked"),"Full destination omitted guidance or kept inventory locked");
            passes.Add("PASS UGUI six-slot limit, commit restrictions, timeout QUERY guidance and full-slot notice");
            Apply(State(8,TradeStage.Editing,id:11)); Apply(State(1,TradeStage.Completed,id:10));
            Check(Field<TradeStateData>(trade,"_state").Id==11 && Field<bool>(inventory,"_tradeLocked"),"Late old receipt closed current UI");
            trade.SetLocal(0); Check(Field<List<TradeSlotData>>(trade,"_draft").Count==0,"Disconnect retained old offer draft");
            PlayerPrefs.SetString(key,"20"); PlayerPrefs.Save(); trade.SetLocal(1002,key); Call(trade,"Update");
            Check(requests.Count==6 && requests[5][0]==(byte)TradeOperation.Query && BitConverter.ToUInt64(requests[5],1)==20 && requests[5][37]==0,"Restored pending ID did not query a clean state");
            Apply(State(1,TradeStage.Completed,id:20));
            Check(!PlayerPrefs.HasKey(key) && !Field<bool>(inventory,"_tradeLocked") && Status().Contains("completed"),"Completed recovery retained local pending ID or lock");
            passes.Add("PASS UGUI late receipt protection, clean disconnect and stored pending-ID automatic query");
            data.Revision=8; for(int i=0;i<24;++i) data.Entries[i]=new() { ItemId=1,Quantity=1,InstanceId=(ulong)(100+i) };
            data.Equipped[0]=new() { ItemId=1,Quantity=1,InstanceId=200 }; inventory.Apply(data);
            int mutations=0; inventory.Requested+=(op,category,source,target,id)=>++mutations;
            root.transform.Find("Equipment Panel/Remove 0").GetComponent<Button>().onClick.Invoke();
            Check(mutations==0 && Field<Text>(inventory,"_status").text.Contains("No free"),"Full equipment inventory did not reject unequip with guidance");
            passes.Add("PASS UGUI equipment unequip rejection when all 24 slots are occupied");
            if(report!=null) { Directory.CreateDirectory(Path.GetDirectoryName(report)); File.WriteAllLines(report,passes); }
            foreach(var line in passes) Debug.Log(line);
            Debug.Log("HEADLESS_TRADE_UI_SUCCESS");
        }
        catch(Exception error)
        {
            if(report!=null) File.WriteAllText(report,"FAIL "+error);
            Debug.LogException(error); EditorApplication.Exit(1);
        }
        finally
        {
            PlayerPrefs.DeleteKey(key); PlayerPrefs.Save(); inputField.SetValue(null,previous);
            if(host!=null) UnityEngine.Object.DestroyImmediate(host); if(root!=null) UnityEngine.Object.DestroyImmediate(root);
        }
    }
}
