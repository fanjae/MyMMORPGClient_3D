using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

// 물리 클릭 대신 실제 UGUI 콜백·입력 필드·대기 중 버튼 차단을 검사한다.
public static class RegistrationUiValidation
{
    public static void Run()
    {
        string report=null;
        foreach(string argument in Environment.GetCommandLineArgs()) if(argument.StartsWith("--registration-ui-report=")) report=argument.Substring("--registration-ui-report=".Length);
        var allowed=typeof(Client3DController).GetField("<UiInputAllowed>k__BackingField",BindingFlags.Static|BindingFlags.NonPublic);
        bool previous=(bool)allowed.GetValue(null); GameObject root=null;
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            root=new GameObject("Registration UI test",typeof(RectTransform)); FieldUI.EnsureLayout(root);
            var ui=root.GetComponentInChildren<FieldUI>();
            if(typeof(FieldUI).GetField("_loginButton",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(ui)==null)
                typeof(FieldUI).GetMethod("Awake",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(ui,null);
            var login=ui.transform.Find("Login"); var account=login.Find("Account").GetComponent<InputField>(); var password=login.Find("Password").GetComponent<InputField>();
            if(password.contentType!=InputField.ContentType.Password) throw new InvalidOperationException("Password is not masked.");
            account.text="New_Player"; password.text=" test1234 "; int signup=0,signin=0;
            ui.RegistrationRequested+=(host,port,id,secret)=> { if(id!="New_Player" || secret!=" test1234 ") throw new InvalidOperationException("UI changed credentials."); ++signup; };
            ui.LoginRequested+=(host,port,id,secret)=>++signin;
            var create=login.Find("Register").GetComponent<Button>(); var connect=login.Find("Connect").GetComponent<Button>();
            allowed.SetValue(null,true); ui.SetState("",0,0,1,false,false,null,Array.Empty<CharacterInfo>());
            create.onClick.Invoke(); if(signup!=1 || signin!=0) throw new InvalidOperationException("Create button did not send registration intent.");
            ui.SetState("pending",0,0,1,false,true,null,Array.Empty<CharacterInfo>());
            create.onClick.Invoke(); connect.onClick.Invoke();
            if(signup!=1 || signin!=0 || create.interactable || connect.interactable) throw new InvalidOperationException("Busy UI accepted another credential operation.");
            ui.SetState("",0,0,1,false,false,null,Array.Empty<CharacterInfo>()); allowed.SetValue(null,false); create.onClick.Invoke();
            if(signup!=1) throw new InvalidOperationException("Inactive UI accepted registration.");
            ui.ClearPassword(); if(password.text!="") throw new InvalidOperationException("Password field did not clear.");
            FieldUI.EnsureLayout(root);
            if(login.GetComponentsInChildren<Button>(true).Length!=9) throw new InvalidOperationException("Ensuring layout duplicated signup controls.");
            if(report!=null) File.WriteAllText(report,"PASS signup button, masked/exact credentials, busy/focus gates, password clear and idempotent layout");
            Debug.Log("REGISTRATION_UI_SUCCESS");
        }
        catch(Exception error) { if(report!=null) File.WriteAllText(report,"FAIL "+error); Debug.LogException(error); EditorApplication.Exit(1); }
        finally { allowed.SetValue(null,previous); if(root!=null) UnityEngine.Object.DestroyImmediate(root); }
    }
}
