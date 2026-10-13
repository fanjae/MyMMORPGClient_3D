using System;
using System.IO;
using UnityEngine;
using UnityEngine.Profiling;

// 명시한 --field-load 옵션에서만 실행하고 실제 조작 검증과 구분한다.
public sealed class FieldLoadProbe : MonoBehaviour
{
    private int _monsters, _frames;
    private double _start, _sampleStart, _sampleTime, _nextEffect;
    private readonly double[] _times=new double[30000];
    private long _managedStart, _nativeStart;
    private int _gcStart;
    private Monster3DView[] _views;
    private Player3DView _player;
    private bool _sampling, _finished;
    public void Initialize(int monsters)
    {
        _monsters=monsters; _start=Time.realtimeSinceStartupAsDouble;
        _views=FindObjectsByType<Monster3DView>(FindObjectsSortMode.None); _player=FindFirstObjectByType<Player3DView>();
        QualitySettings.vSyncCount=0; Application.targetFrameRate=-1;
    }
    private void Update()
    {
        if (_finished) return;
        double now=Time.realtimeSinceStartupAsDouble;
        if (now>=_nextEffect) { _nextEffect=now+0.8; foreach (var view in _views) view.ShowDamage(20); _player?.PlayAttack(); }
        if (!_sampling)
        {
            if (now-_start<3) return;
            _sampling=true; _sampleStart=now; _managedStart=GC.GetTotalMemory(false); _nativeStart=Profiler.GetTotalAllocatedMemoryLong(); _gcStart=GC.CollectionCount(0); return;
        }
        double elapsed=Time.unscaledDeltaTime; _sampleTime+=elapsed;
        if (_frames<_times.Length) _times[_frames++]=elapsed*1000;
        if (now-_sampleStart<12) return;
        _finished=true; Array.Sort(_times,0,_frames);
        Directory.CreateDirectory("Logs");
        string text=$"PASS automatic field render load\nrequested={_monsters} visibleObjects={_views.Length} frames={_frames} fps={_frames/_sampleTime:F2}"+
            $" p50Ms={_times[_frames/2]:F3} p95Ms={_times[(int)(_frames*0.95)]:F3}"+
            $"\nmanagedDelta={GC.GetTotalMemory(false)-_managedStart} nativeDelta={Profiler.GetTotalAllocatedMemoryLong()-_nativeStart} gc0={GC.CollectionCount(0)-_gcStart}"+
            $" sharedMonsterMaterials={Monster3DView.SharedMaterialCount}\nUncapped window frame time includes CPU/GPU/presentation; no isolated GPU timing.";
        File.WriteAllText($"Logs/field-load-{_monsters}.result",text);
        ScreenCapture.CaptureScreenshot(Path.GetFullPath($"Logs/field-load-{_monsters}.png"));
        Debug.Log(text); Invoke(nameof(Finish),0.5f);
    }
    private void Finish() { Application.Quit(0); }
}
