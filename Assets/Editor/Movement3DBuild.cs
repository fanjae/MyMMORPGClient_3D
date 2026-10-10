using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;

public static class Movement3DBuild
{
    [MenuItem("Tools/MyMMORPG/Build 3D Windows Client")]
    public static void Build()
    {
        const string materialPath = "Assets/Resources/Movement3DMaterial.mat";
        if (AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(materialPath) == null)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
            // Player 빌드에서 Lit 셰이더가 제거되지 않도록 Resources 자산으로 참조한다.
            var material = new UnityEngine.Material(UnityEngine.Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(material, materialPath);
        }
        Directory.CreateDirectory("Builds/Movement3D");
        PlayerSettings.runInBackground = true;
        PlayerSettings.defaultScreenWidth = 1100; PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.fullScreenMode = UnityEngine.FullScreenMode.Windowed;
        BuildReport report = BuildPipeline.BuildPlayer(new[] { "Assets/Scenes/SampleScene.unity" },
            "Builds/Movement3D/MyMMORPGClient_3D.exe", BuildTarget.StandaloneWindows64, BuildOptions.None);
        if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("3D Windows build failed: " + report.summary.result);
        UnityEngine.Debug.Log("3D_BUILD_SUCCESS: " + report.summary.outputPath);
    }
}
