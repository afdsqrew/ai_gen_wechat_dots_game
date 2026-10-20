using Unity.Entities;
using UnityEngine;

namespace EcsCrowdDemo
{
    /// <summary>人群生成器：放在 SubScene 中，烘焙出生成配置、初始生成请求与 MoveActive。</summary>
    public class CrowdSpawnerAuthoring : MonoBehaviour
    {
        public GameObject UnitPrefab;

        [Min(1)] public int Count = 10000;
        public float ArenaRadius = 60f;
        public float MaxSpeed = 10f;
        public float SeparationRadius = 1f;
    }

    public class CrowdSpawnerBaker : Baker<CrowdSpawnerAuthoring>
    {
        public override void Bake(CrowdSpawnerAuthoring authoring)
        {
            if (authoring.UnitPrefab == null)
                return;

            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new CrowdSpawnSettings
            {
                UnitPrefab = GetEntity(authoring.UnitPrefab, TransformUsageFlags.Dynamic),
                Count = authoring.Count,
                ArenaRadius = authoring.ArenaRadius,
                MaxSpeed = authoring.MaxSpeed,
                SeparationRadius = authoring.SeparationRadius,
            });
            AddComponent(entity, new CrowdSpawnRequest { Count = authoring.Count });
            AddComponent<MoveActive>(entity);
        }
    }
}
