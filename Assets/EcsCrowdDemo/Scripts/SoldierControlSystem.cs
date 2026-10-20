using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace EcsCrowdDemo
{
    /// <summary>
    /// 玩家输入 → 士兵移动：摇杆/键盘方向移动（优先，会打断点击目标），
    /// 或朝点击设定的地面目标移动（到达即停）；目标点显示蓝色标记。
    /// 同时发布士兵位置供相机跟随。
    /// </summary>
    public partial class SoldierControlSystem : SystemBase
    {
        private EntityQuery _settingsQuery;
        private GameObject _marker;
        private Material _markerMaterial;

        protected override void OnCreate()
        {
            _settingsQuery = GetEntityQuery(typeof(CrowdSpawnSettings));

            // 点击目标标记：压扁的小方块平放在地面，点击时显示、到达后隐藏
            _marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _marker.name = "MoveTargetMarker";
            Object.Destroy(_marker.GetComponent<Collider>());
            _marker.transform.localScale = new Vector3(1.4f, 0.06f, 1.4f);
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            _markerMaterial = new Material(unlit);
            _markerMaterial.SetColor("_BaseColor", new Color(0.2f, 0.7f, 1f));
            _marker.GetComponent<MeshRenderer>().sharedMaterial = _markerMaterial;
            _marker.SetActive(false);
        }

        protected override void OnDestroy()
        {
            if (_marker != null) Object.Destroy(_marker);
            if (_markerMaterial != null) Object.Destroy(_markerMaterial);
        }

        protected override void OnUpdate()
        {
            CrowdInput.Sample();
            float dt = SystemAPI.Time.DeltaTime;

            float arenaRadius = 60f;
            if (_settingsQuery.TryGetSingleton<CrowdSpawnSettings>(out var settings))
                arenaRadius = settings.ArenaRadius;

            foreach (var (transform, state) in
                SystemAPI.Query<RefRW<LocalTransform>, RefRW<SoldierState>>().WithAll<SoldierTag>())
            {
                // 消费点击目标（clamp 到场地内）
                if (CrowdInput.ClickMoveTarget.HasValue)
                {
                    var target = CrowdInput.ClickMoveTarget.Value;
                    float r = math.length(target.xz);
                    if (r > arenaRadius && r > 0.001f)
                        target = new float3(target.x / r, 0f, target.z / r) * arenaRadius;
                    state.ValueRW.MoveTarget = target;
                    state.ValueRW.HasMoveTarget = true;
                    CrowdInput.ClickMoveTarget = null;
                }

                float2 move = CrowdInput.Move;
                if (math.lengthsq(move) > 0.0001f)
                {
                    // 摇杆/键盘输入优先，打断点击移动
                    var p = transform.ValueRO.Position;
                    p += new float3(move.x, 0f, move.y) * state.ValueRO.MoveSpeed * dt;
                    float r = math.length(p.xz);
                    if (r > arenaRadius && r > 0.001f)
                        p = new float3(p.x / r, 0f, p.z / r) * arenaRadius;
                    transform.ValueRW.Position = p;
                    state.ValueRW.HasMoveTarget = false;
                }
                else if (state.ValueRO.HasMoveTarget)
                {
                    // 朝点击目标移动，到达即停
                    var p = transform.ValueRO.Position;
                    float3 to = state.ValueRO.MoveTarget - p;
                    to.y = 0f;
                    float d = math.length(to);
                    if (d < 0.6f)
                    {
                        state.ValueRW.HasMoveTarget = false;
                    }
                    else
                    {
                        p += to / d * state.ValueRO.MoveSpeed * dt;
                        transform.ValueRW.Position = p;
                    }
                }

                // 相机跟随用（WebGLGraphicsSystem / CrowdCamera 共享）
                CrowdRenderSettings.SoldierPosition = transform.ValueRO.Position;

                // 目标标记跟随
                bool hasTarget = state.ValueRO.HasMoveTarget;
                _marker.SetActive(hasTarget);
                if (hasTarget)
                    _marker.transform.position = state.ValueRO.MoveTarget + new float3(0f, 0.03f, 0f);
            }
        }
    }
}
