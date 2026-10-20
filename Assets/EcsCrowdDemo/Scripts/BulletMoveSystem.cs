using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace EcsCrowdDemo
{
    /// <summary>
    /// 子弹飞行、命中与回收系统：
    /// - 追踪锁定目标的当前位置，接近即命中——目标掉 1 血，3 血打空则隐藏并计击杀；
    /// - 完全离开相机视锥立即回收（视野外不留子弹），寿命仅作兜底；
    /// - 回收 = 归还对象池（禁用 BulletTag + 入空闲列表），全程零实体生灭。
    ///
    /// 结构变化说明：命中伤害（SetComponentData）与隐藏（enableable 位 + RenderBounds
    /// 置空）均为纯数据写入；子弹回收的启用位切换经 ECB 延迟批量执行，
    /// 避免在 foreach 迭代中直接修改 enableable 位。
    /// </summary>
    public partial class BulletMoveSystem : SystemBase
    {
        private Camera _camera;
        private EntityQuery _gameplayQuery;

        protected override void OnCreate()
        {
            _gameplayQuery = GetEntityQuery(typeof(GameplayState));
        }

        protected override void OnUpdate()
        {
            float dt = SystemAPI.Time.DeltaTime;
            float respawnTime = 5f;
            if (_gameplayQuery.TryGetSingleton<GameplayState>(out var gameplay))
                respawnTime = gameplay.RespawnTime;

            // 共享视锥缓存（与渲染/血条系统共用本帧的计算结果）
            if (_camera == null)
                _camera = Camera.main;
            bool hasFrustum = _camera != null;
            var planes = hasFrustum ? FrustumUtility.GetPlanes(_camera) : default;

            // Temp ECB 是 struct + Temp 分配器（非 GC），1.0 的 ECB 不可复用；
            // 本帧的启用位切换命令积攒于此，仅在有待处理回收时 Playback（避免空跑）
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            var bulletExtents = new float3(0.2f);
            int pendingRecycles = 0;

            foreach (var (transform, bullet, entity) in
                SystemAPI.Query<RefRW<LocalTransform>, RefRW<Bullet>>().WithAll<BulletTag>().WithEntityAccess())
            {
                var b = bullet.ValueRO;
                b.Life -= dt;

                // 回收：寿命耗尽，或完全离开相机视野——归还对象池（禁用 + 入空闲列表）
                if (b.Life <= 0f ||
                    (hasFrustum && FrustumUtility.IsOutside(planes, transform.ValueRO.Position, bulletExtents)))
                {
                    ecb.SetComponentEnabled<BulletTag>(entity, false);
                    BulletPool.Free.Add(entity);
                    pendingRecycles++;
                    bullet.ValueRW = b;
                    continue;
                }

                // 追踪目标当前位置（目标被销毁则飞向最后已知位置）
                float3 targetPos = b.LastTargetPos;
                if (EntityManager.Exists(b.Target))
                    b.LastTargetPos = targetPos =
                        EntityManager.GetComponentData<LocalTransform>(b.Target).Position +
                        new float3(0f, 0.6f, 0f);

                float3 pos = transform.ValueRO.Position;
                float3 toTarget = targetPos - pos;
                float dist = math.length(toTarget);
                float step = b.Speed * dt;

                if (dist <= step + 0.5f)
                {
                    // 命中：目标仍存活且未隐藏才结算伤害（Hidden 为 enableable，看启用状态而非存在性）
                    if (EntityManager.Exists(b.Target) && !EntityManager.IsComponentEnabled<Hidden>(b.Target))
                    {
                        var hp = EntityManager.GetComponentData<EnemyHealth>(b.Target);
                        if (hp.Value > 0)
                        {
                            hp.Value--;
                            if (hp.Value == 0)
                            {
                                // 零结构变化的隐藏：启用 Hidden（查询自动视为"存在"）+
                                // BRG 路径置空渲染包围盒（Entities Graphics 剔除空包围盒）
                                EntityManager.SetComponentData(b.Target, new Hidden { RespawnTimer = respawnTime });
                                EntityManager.SetComponentEnabled<Hidden>(b.Target, true);
                                if (EntityManager.HasComponent<Unity.Rendering.RenderBounds>(b.Target))
                                {
                                    EntityManager.SetComponentData(b.Target, new Unity.Rendering.RenderBounds
                                    {
                                        Value = new Unity.Mathematics.AABB
                                        {
                                            Center = Unity.Mathematics.float3.zero,
                                            Extents = Unity.Mathematics.float3.zero,
                                        },
                                    });
                                }
                                AddKill();
                            }
                            else
                            {
                                EntityManager.SetComponentData(b.Target, hp);
                            }
                        }
                    }
                // 命中后回收：归还对象池（禁用 + 入空闲列表）
                    ecb.SetComponentEnabled<BulletTag>(entity, false);
                    BulletPool.Free.Add(entity);
                    pendingRecycles++;
                    bullet.ValueRW = b;
                    continue;
                }

                pos += toTarget / dist * step;
                transform.ValueRW.Position = pos;
                bullet.ValueRW = b;
            }

            // 仅在有待处理回收时执行批量启用位切换（避免空 Playback 的 native 调用）
            if (pendingRecycles > 0)
            {
                ecb.Playback(EntityManager);
            }
            ecb.Dispose();
        }

        /// <summary>击杀计数（GameplayState 单例，UI 显示）。</summary>
        private void AddKill()
        {
            if (_gameplayQuery.TryGetSingletonEntity<GameplayState>(out var entity))
            {
                var state = EntityManager.GetComponentData<GameplayState>(entity);
                state.Kills++;
                EntityManager.SetComponentData(entity, state);
            }
        }
    }
}
