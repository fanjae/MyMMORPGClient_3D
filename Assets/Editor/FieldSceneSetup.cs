using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class FieldSceneSetup
{
    [MenuItem("Tools/MyMMORPG/Place Inventory and Input Probe")]
    public static void Ensure()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != "Assets/Scenes/SampleScene.unity") scene = EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        var controller = Object.FindFirstObjectByType<Client3DController>();
        if (controller == null) throw new System.InvalidOperationException("Place the 3D client before adding field UI.");
        // 씬에서 직접 확인할 수 있도록 입력 기록기와 인벤토리를 배치하고 중복 생성은 피한다.
        if (controller.GetComponent<InputProbe3D>() == null) controller.gameObject.AddComponent<InputProbe3D>();
        var inventory = Object.FindFirstObjectByType<InventoryWindow>();
        if (inventory == null)
        {
            var canvas = new GameObject("Field Inventory Canvas", typeof(RectTransform));
            InventoryWindow.BuildLayout(canvas);
        }
        if (inventory != null) InventoryWindow.EnsureControls(inventory.gameObject);
        inventory = Object.FindFirstObjectByType<InventoryWindow>();
        if (inventory != null) FieldVitalsHUD.EnsureLayout(inventory.gameObject);
        // 실행 파일에서도 같은 배치를 사용하도록 씬과 자산을 저장한다.
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
        Debug.Log("FIELD_SCENE_SETUP: inventory, HP/MP HUD and keyboard probe saved in SampleScene");
    }
}
