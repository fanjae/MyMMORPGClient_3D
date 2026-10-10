using System;
using System.IO;
using UnityEngine;

// 오프라인 이동 계산은 씬에 배치한 BoxCollider와 발 기준 시작 위치를 읽는다.
// 온라인에서는 이 배치를 숨기고 서버가 보낸 지형을 사용한다.
public sealed class SceneWorld3D : MonoBehaviour
{
    public Transform TerrainRoot;
    public Transform SpawnPoint;

    public void SetVisible(bool visible)
    {
        if (TerrainRoot != null) TerrainRoot.gameObject.SetActive(visible);
        if (SpawnPoint != null) SpawnPoint.gameObject.SetActive(visible && !Application.isPlaying);
    }
    public World3DData ReadWorld()
    {
        if (TerrainRoot == null || SpawnPoint == null) throw new InvalidDataException("Assign TerrainRoot and SpawnPoint in SceneWorld3D.");
        World3DData world = World3DData.Preview(); world.Boxes.Clear();
        world.SpawnX = Unit(SpawnPoint.position.x); world.SpawnY = Unit(SpawnPoint.position.y); world.SpawnZ = Unit(SpawnPoint.position.z);
        foreach (var collider in TerrainRoot.GetComponentsInChildren<BoxCollider>(true))
        {
            if (!collider.enabled || !collider.gameObject.activeSelf) continue;
            Transform box = collider.transform;
            if (Quaternion.Angle(box.rotation, Quaternion.identity) > 0.001f ||
                box.lossyScale.x <= 0 || box.lossyScale.y <= 0 || box.lossyScale.z <= 0)
                throw new InvalidDataException($"{box.name}: use zero rotation and positive scale for the shared box motor.");
            Vector3 center = box.TransformPoint(collider.center);
            Vector3 half = Vector3.Scale(collider.size, box.lossyScale) * 0.5f;
            Box3DData data = new() { Id = (uint)world.Boxes.Count + 1,
                MinX = Unit(center.x - half.x), MinY = Unit(center.y - half.y), MinZ = Unit(center.z - half.z),
                MaxX = Unit(center.x + half.x), MaxY = Unit(center.y + half.y), MaxZ = Unit(center.z + half.z) };
            if (data.MinX >= data.MaxX || data.MinY >= data.MaxY || data.MinZ >= data.MaxZ ||
                data.MinX < world.MinX || data.MaxX > world.MaxX || data.MinY < world.MinY || data.MaxY > world.MaxY ||
                data.MinZ < world.MinZ || data.MaxZ > world.MaxZ)
                throw new InvalidDataException($"{box.name}: box is empty or outside the test world bounds.");
            world.Boxes.Add(data);
        }
        if (world.Boxes.Count == 0 || world.Boxes.Count > 4096) throw new InvalidDataException("Place between 1 and 4096 terrain boxes.");
        world.BoxCount = (ushort)world.Boxes.Count;
        if (world.SpawnSupport() == 0 || world.SpawnX - world.Radius < world.MinX || world.SpawnX + world.Radius > world.MaxX ||
            world.SpawnZ - world.Radius < world.MinZ || world.SpawnZ + world.Radius > world.MaxZ ||
            world.SpawnY < world.MinY || world.SpawnY + world.Height > world.MaxY)
            throw new InvalidDataException("Place Player Spawn on a box top inside the test world bounds.");
        foreach (var box in world.Boxes)
            if (world.SpawnX + world.Radius > box.MinX && world.SpawnX - world.Radius < box.MaxX &&
                world.SpawnZ + world.Radius > box.MinZ && world.SpawnZ - world.Radius < box.MaxZ &&
                world.SpawnY < box.MaxY && world.SpawnY + world.Height > box.MinY)
                throw new InvalidDataException("Player Spawn overlaps terrain. Move the feet marker to a clear box top.");
        return world;
    }
    private static int Unit(float metres)
    {
        float units = metres * 20;
        if (float.IsNaN(units) || float.IsInfinity(units) || Math.Abs(units - Math.Round(units)) > 0.001)
            throw new InvalidDataException("Align box edges and Player Spawn to the 0.05 m grid.");
        return checked((int)Math.Round(units));
    }
}
