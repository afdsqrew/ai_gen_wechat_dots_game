using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace EcsCrowdDemo
{
    /// <summary>
    /// 处理人群生成请求：批量实例化单位，并以黄金角"向日葵"排布铺满场地，
    /// 同时写入每单位随机种子、公转轨道与实例颜色。
    ///
    /// 触发方式：Baker/Bootstrap 写入 CrowdSpawnRequest 实体（数量），或 UI 在运行时
    /// 追加请求（+100 按钮）；处理完成后移除请求实体。
    /// </summary>
    [UpdateBefore(typeof(CrowdMoveSystem))] // 新单位先于移动系统就绪
    public partial struct UnitSpawnSystem : ISystem
    {
        /// <summary>黄金角 ≈ 137.5°——向日葵种子的排布角度，相邻序号的单位错开角度最大化。</summary>
        private const float GoldenAngle = 2.39996323f;
        /// <summary>黄金比例的小数部分，作为低频扰动源（分布均匀、无周期感）。</summary>
        private const float GoldenRatio = 0.6180339887f;

        private EntityQuery _requestQuery;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CrowdSpawnSettings>();
            _requestQuery = state.GetEntityQuery(typeof(CrowdSpawnRequest));
        }

        public void OnUpdate(ref SystemState state)
        {
            // 无生成请求的帧直接返回，避免每帧分配请求列表（GC 清理）
            if (_requestQuery.IsEmpty)
                return;

            var em = state.EntityManager;
            var settingsEntity = SystemAPI.GetSingletonEntity<CrowdSpawnSettings>();

            // 先收集请求再处理：Spawn 内有结构变化（Instantiate/RemoveComponent），
            // 不能在 Query 迭代中直接做
            var requests = new List<(Entity entity, int count)>();
            foreach (var (request, entity) in SystemAPI.Query<RefRW<CrowdSpawnRequest>>().WithEntityAccess())
                requests.Add((entity, request.ValueRO.Count));

            if (requests.Count == 0)
                return;

            var settings = em.GetComponentData<CrowdSpawnSettings>(settingsEntity);
            foreach (var (entity, count) in requests)
            {
                Spawn(em, settingsEntity, ref settings, count);
                em.RemoveComponent<CrowdSpawnRequest>(entity);
            }
        }

        private static void Spawn(EntityManager em, Entity settingsEntity, ref CrowdSpawnSettings settings, int count)
        {
            if (settings.UnitPrefab == Entity.Null || count <= 0)
                return;

            // 批量实例化：一次结构操作生成 count 个模板副本（组件值/enableable 位一并复制）
            var entities = new NativeArray<Entity>(count, Allocator.Temp);
            em.Instantiate(settings.UnitPrefab, entities);

            int startIndex = settings.SpawnedTotal;
            for (int i = 0; i < count; i++)
            {
                var e = entities[i];
                int index = startIndex + i;

                // Hidden 的禁用位随 Instantiate 从模板复制（模板创建时即禁用）；
                // 不在此逐实体 SetComponentEnabled：万级逐次调用会反复触发查询增量同步，代价极高

                // 向日葵排布：半径按 sqrt 增长、角度按黄金角递进，初始即均匀铺满场地
                //（sqrt 保证单位圆盘面积密度均匀——相邻序号的角度差最大，视觉上是螺旋放射）
                float angle = index * GoldenAngle;
                float radius = settings.ArenaRadius * 0.9f * math.sqrt((index + 0.5f) / (startIndex + count));

                // 覆写位置（从模板拷贝来的 LocalTransform 只保留 Scale）
                var transform = em.GetComponentData<LocalTransform>(e);
                transform.Position = new float3(radius * math.cos(angle), 0f, radius * math.sin(angle));
                em.SetComponentData(e, transform);

                // 每单位独立 PRNG 种子（整数散列，保证唯一且可复现）
                uint seed = (uint)index * 747796405u + 2891336453u;
                em.SetComponentData(e, new UnitMotion
                {
                    Velocity = float3.zero,
                    // 速度带 ±20% 个体差异（黄金比例扰动，自然且无聚集）
                    MaxSpeed = settings.MaxSpeed * (0.8f + 0.4f * math.frac(index * GoldenRatio)),
                    Rng = seed,
                });

                // 公转轨道参数：半径覆盖内圈到 40% 场地、速度 ±(0.06~0.45) rad/s、
                // 相位用出生角——初始状态即为有序漩涡
                em.SetComponentData(e, new UnitGoalOffset
                {
                    OrbitRadius = math.lerp(1.5f, settings.ArenaRadius * 0.4f, Hash01(seed)),
                    OrbitSpeed = math.lerp(0.06f, 0.45f, Hash01(seed + 1u)) * ((seed & 1u) == 0u ? 1f : -1f),
                    Phase = angle,
                });

                // 每实例颜色：色相按黄金比例铺满色环（相邻单位色差最大，整体呈彩虹）
                float3 rgb = HsvToRgb(math.frac(index * GoldenRatio + 0.03f), 0.8f, 1f);
                em.SetComponentData(e, new Unity.Rendering.URPMaterialPropertyBaseColor
                {
                    Value = new float4(rgb, 1f),
                });
            }

            // 记录累计生成数（决定后续批次的排布/颜色序号，保证 ±100 追加时连续）
            settings.SpawnedTotal = startIndex + count;
            em.SetComponentData(settingsEntity, settings);
            entities.Dispose();
        }

        /// <summary>低偏差哈希，把整数映射到 [0,1)。</summary>
        private static float Hash01(uint x)
        {
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (x & 0x00FFFFFF) / 16777215f;
        }

        /// <summary>标准 HSV → RGB。</summary>
        private static float3 HsvToRgb(float h, float s, float v)
        {
            float3 k = new float3(1f, 2f / 3f, 1f / 3f);
            float3 p = math.abs(math.frac(new float3(h, h, h) + k) * 6f - 3f);
            return v * math.lerp(new float3(1f), math.clamp(p - 1f, 0f, 1f), s);
        }
    }
}
