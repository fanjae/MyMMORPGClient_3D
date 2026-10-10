using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class Movement3DScene
{
    [MenuItem("Tools/MyMMORPG/Create Placed 3D Test Scene")]
    public static void CreatePlacedScene()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        // 이미 배치한 씬은 다시 생성하지 않아 사용자가 옮긴 지형을 보존한다.
        if (UnityEngine.Object.FindFirstObjectByType<SceneWorld3D>() != null) return;
        World3DData world = World3DData.Preview();
        GameObject root = new("3D Test World");
        SceneWorld3D layout = root.AddComponent<SceneWorld3D>();
        GameObject terrain = new("Terrain"); terrain.transform.SetParent(root.transform, false); layout.TerrainRoot = terrain.transform;
        string[] names = { "Floor", "Far Floor", "Wall", "Low Step", "High Step" };
        Material floor = MaterialAsset("TestFloor", new Color(0.25f, 0.4f, 0.3f));
        Material obstacle = MaterialAsset("TestObstacle", new Color(0.4f, 0.5f, 0.65f));
        foreach (var box in world.Boxes)
        {
            GameObject placed = GameObject.CreatePrimitive(PrimitiveType.Cube); placed.name = names[box.Id - 1];
            placed.transform.SetParent(terrain.transform, false);
            placed.transform.position = new Vector3((box.MinX + box.MaxX) * 0.025f, (box.MinY + box.MaxY) * 0.025f, (box.MinZ + box.MaxZ) * 0.025f);
            placed.transform.localScale = new Vector3((box.MaxX - box.MinX) * 0.05f, (box.MaxY - box.MinY) * 0.05f, (box.MaxZ - box.MinZ) * 0.05f);
            placed.GetComponent<Renderer>().sharedMaterial = box.MaxY == 0 ? floor : obstacle;
        }
        GameObject spawn = new("Player Spawn"); spawn.transform.SetParent(root.transform, false); layout.SpawnPoint = spawn.transform;
        GameObject capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule); capsule.name = "Character Preview";
        capsule.transform.SetParent(spawn.transform, false); capsule.transform.localPosition = new Vector3(0, 0.9f, 0);
        capsule.transform.localScale = new Vector3(0.6f, 0.9f, 0.6f); UnityEngine.Object.DestroyImmediate(capsule.GetComponent<Collider>());
        capsule.GetComponent<Renderer>().sharedMaterial = MaterialAsset("TestCharacter", Color.cyan);
        GameObject client = new("MyMMORPG 3D Client"); client.AddComponent<NetworkManager3D>(); client.AddComponent<Client3DController>();
        Camera.main.transform.position = new Vector3(9, 9, -12); Camera.main.transform.LookAt(Vector3.up);
        layout.ReadWorld(); EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets();
        Selection.activeGameObject = root;
        Debug.Log("3D_PLACED_SCENE_SAVED: Assets/Scenes/SampleScene.unity");
    }
    private static Material MaterialAsset(string name, Color color)
    {
        if (!AssetDatabase.IsValidFolder("Assets/Materials")) AssetDatabase.CreateFolder("Assets", "Materials");
        string path = "Assets/Materials/" + name + ".mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null) return material;
        material = Player3DView.CreateMaterial(color); material.name = name; AssetDatabase.CreateAsset(material, path); return material;
    }
    public static void BuildAndValidate()
    {
        CreatePlacedScene();
        SceneWorld3D layout = UnityEngine.Object.FindFirstObjectByType<SceneWorld3D>();
        World3DData initial = layout.ReadWorld();
        if (initial.Boxes.Count != 5 || initial.SpawnSupport() != 1) throw new InvalidDataException("Default placed scene is invalid.");
        Transform wall = layout.TerrainRoot.Find("Wall"); Vector3 position = wall.position;
        try
        {
            // 벽 Transform을 옮기면 표시 좌표와 공통 이동 모터의 충돌 위치가 같이 바뀌는지 검사한다.
            wall.position += Vector3.right * 4;
            World3DData edited = layout.ReadWorld(); var motor = new Motion3DSimulation(edited); var state = motor.Spawn();
            for (int i = 0; i < 160; ++i) motor.Step(ref state, 1000, 0, false);
            if (Math.Abs(state.X - 154) > 0.00001) throw new InvalidDataException("Placed wall edit did not update collision.");
            wall.rotation = Quaternion.Euler(0, 15, 0);
            bool rejected = false; try { layout.ReadWorld(); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new InvalidDataException("Rotated terrain was accepted by the box motor.");
        }
        finally { wall.position = position; wall.rotation = Quaternion.identity; }
        Debug.Log("3D_PLACED_SCENE_CHECK: PASS saved geometry, edited-wall collision, unsupported rotation rejection");
        Movement3DBuild.Build();
    }
}
