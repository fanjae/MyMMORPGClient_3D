using System;
using System.IO;
using UnityEditor;

// 열린 Editor의 프로젝트를 다시 실행하지 않고 명시한 로컬 빌드 요청만 처리한다.
[InitializeOnLoad]
public static class Movement3DBuildRequest
{
    private static bool _running;
    static Movement3DBuildRequest() { EditorApplication.update += Poll; }
    private static void Poll()
    {
        if (_running || AssetDatabase.IsAssetImportWorkerProcess() || EditorApplication.isPlaying || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        const string request = "Temp/Movement3DBuild.request";
        if (!File.Exists(request)) return;
        File.Delete(request); _running = true;
        try
        {
            Movement3DBuild.Build();
            File.WriteAllText("Temp/Movement3DBuild.result", "SUCCESS");
        }
        catch (Exception e)
        {
            File.WriteAllText("Temp/Movement3DBuild.result", "FAILED\n" + e);
            UnityEngine.Debug.LogException(e);
        }
        finally { _running = false; }
    }
}
