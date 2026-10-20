using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace EcsCrowdDemo
{
    /// <summary>
    /// 士兵扇形喷射系统：锁定最近敌人确定中心方向，单遍 Job 把扇形内的敌人按角度
    /// 分到 100 个桶（每桶保留该方向最近的敌人），随后一次发射 100 粒追踪子弹
    /// （速度带随机扰动）。空桶方向的子弹直线飞行，离开视野后由 BulletMoveSystem 回收。
    ///
    /// 性能设计：索敌与分桶只在喷射时执行（FireInterval 间隔，默认 1 秒/次）；
    /// 子弹从对象池取用（零 Create/Destroy 结构变化）；临时数组用 try/finally
    /// 管理保证异常时也能释放（using var 声明的 NativeArray 不能修改成员）。
    /// </summary>
    public partial class SoldierFireSystem : SystemBase
    {
        /// <summary>每次喷射的子弹数。</summary>
        private const int kPellets = 100;

        /// <summary>可被射击的敌人（已隐藏的排除——enableable 禁用满足 None）。</summary>
        private EntityQuery _enemyQuery;
        private EntityQuery _gameplayQuery;

        /// <summary>子弹实体原型（批量创建池实体时复用，一次结构操作生成 N 个）。</summary>
        private EntityArchetype _bulletArchetype;

        /// <summary>子弹的渲染资源（BRG 路径 Batch 引用模式的注册 ID，懒注册一次）。</summary>
        private BatchMeshID _bulletMeshId;
        private BatchMaterialID _bulletMatId;
        private bool _bulletIdsRegistered;

        /// <summary>池是否已初始化（懒建：等 Bootstrap 注入渲染资源）。</summary>
        private bool _poolInitialized;

        /// <summary>迭代中发现池不足的待扩标记（扩池的结构变化在迭代外执行）。</summary>
        private bool _needExpand;

        protected override void OnCreate()
        {
            _enemyQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<UnitTag>(),
                    ComponentType.ReadOnly<LocalTransform>(),
                },
                None = new[] { ComponentType.ReadOnly<Hidden>() },
            });
            _gameplayQuery = GetEntityQuery(typeof(GameplayState));
            _bulletArchetype = EntityManager.CreateArchetype(
                typeof(LocalTransform), typeof(BulletTag), typeof(Bullet));
            // 注意：不能在 OnCreate 建池——系统创建早于场景加载，
            // 此时 Bootstrap 尚未注入 Mesh/Material，BRG 分支会 NRE 且导致整个系统注册失败
        }

        protected override void OnUpdate()
        {
            // 懒建池：Simulation 组先于 MonoBehaviour Start 执行，首次更新时
            // Bootstrap.Start 可能尚未注入 Mesh/Material——未就绪则跳过本帧（下一帧再试）
            if (!_poolInitialized)
            {
                if (CrowdRenderSettings.Mesh == null || CrowdRenderSettings.BulletMaterial == null)
                    return;
                _poolInitialized = true;
                BulletPool.Free.Clear();
                ExpandPool(1024);
            }

            float dt = SystemAPI.Time.DeltaTime;

            // 从 GameplayState 单例读玩法参数（Bootstrap 启动时写入，可运行时调整）
            float bulletSpeed = 40f;
            float bulletLife = 3f;
            float spreadAngle = 80f;
            if (_gameplayQuery.TryGetSingleton<GameplayState>(out var gameplay))
            {
                bulletSpeed = gameplay.BulletSpeed;
                bulletLife = gameplay.BulletLife;
                spreadAngle = gameplay.SpreadAngle;
            }

            foreach (var (transform, state) in
                SystemAPI.Query<RefRW<LocalTransform>, RefRW<SoldierState>>().WithAll<SoldierTag>())
            {
                // 开火冷却倒计时
                state.ValueRW.FireCooldown -= dt;
                if (state.ValueRO.FireCooldown > 0f)
                    continue;

                // 池不足：本轮跳过喷射（1.4 禁止在 query 迭代中做结构变化，
                // 扩池必须放到迭代结束后），短冷却待扩池完成后立即重试
                if (BulletPool.Free.Count < kPellets)
                {
                    _needExpand = true;
                    state.ValueRW.FireCooldown = 0.1f;
                    continue;
                }

                // 中心方向：最近的未隐藏敌人（扇形以此方向为中心张开）
                var center = FindNearestEnemy(transform.ValueRO.Position);
                if (center == Entity.Null)
                {
                    // 场上没有可打的目标，稍后再试（持续索敌但降频到 0.5s/次）
                    state.ValueRW.CurrentTarget = Entity.Null;
                    state.ValueRW.FireCooldown = 0.5f;
                    continue;
                }
                state.ValueRW.CurrentTarget = center;

                float3 soldierPos = transform.ValueRO.Position;
                float3 toCenter = EntityManager.GetComponentData<LocalTransform>(center).Position - soldierPos;
                toCenter.y = 0f;
                float centerAngle = math.atan2(toCenter.z, toCenter.x);
                float spread = math.radians(math.max(spreadAngle, 1f));

                // 扇形目标分配：单遍遍历敌人，按角度入桶、距离最近者占据
                var targets = new NativeArray<Entity>(kPellets, Allocator.TempJob);
                var bestD2 = new NativeArray<float>(kPellets, Allocator.TempJob);
                var origin = new NativeReference<float3>(soldierPos, Allocator.TempJob);
                try
                {
                    // 桶的最小距离² 初始化为无穷大
                    for (int i = 0; i < kPellets; i++)
                        bestD2[i] = float.MaxValue;

                    var assignJob = new AssignSectorTargetsJob
                    {
                        Origin = origin,
                        CenterAngle = centerAngle,
                        SpreadHalf = spread * 0.5f,
                        PelletCount = kPellets,
                        Targets = targets,
                        BestD2 = bestD2,
                    };
                    // 单线程调度：Job 内对桶数组的"最近者占据"是非原子的读改写，
                    // 必须单线程（频率低，成本可忽略）
                    assignJob.Schedule(_enemyQuery, Dependency).Complete();

                    // 从对象池取 100 粒（不足自动扩池），写入数据后启用——零结构变化发射
                    while (BulletPool.Free.Count < kPellets)
                        ExpandPool(256);

                    var rng = new Unity.Mathematics.Random(
                        (uint)(SystemAPI.Time.ElapsedTime * 1000.0) * 747796405u + 2891336453u);
                    for (int i = 0; i < kPellets; i++)
                    {
                        // 每粒沿自己的角度方向飞行；扇形等分 100 份
                        float angle = centerAngle + spread * (i / (float)(kPellets - 1) - 0.5f);
                        float3 dir = new float3(math.cos(angle), 0f, math.sin(angle));
                        var target = targets[i];

                        // 尾部取用（RemoveAt 尾部 O(1)）
                        var bulletEntity = BulletPool.Free[BulletPool.Free.Count - 1];
                        BulletPool.Free.RemoveAt(BulletPool.Free.Count - 1);

                        var bulletTransform = LocalTransform.FromScale(0.15f);
                        bulletTransform.Position = soldierPos + new float3(0f, 0.6f, 0f);
                        EntityManager.SetComponentData(bulletEntity, bulletTransform);

                        // 有目标则追踪；空桶方向沿直线飞出视野
                        float3 lastTarget = target != Entity.Null
                            ? EntityManager.GetComponentData<LocalTransform>(target).Position + new float3(0f, 0.6f, 0f)
                            : soldierPos + dir * 70f + new float3(0f, 0.6f, 0f);

                        EntityManager.SetComponentData(bulletEntity, new Bullet
                        {
                            Target = target,
                            LastTargetPos = lastTarget,
                            Speed = bulletSpeed * (0.85f + 0.3f * rng.NextFloat()),
                            Life = bulletLife,
                        });

                        // 启用 = 进入飞行（渲染与子弹系统开始处理）
                        EntityManager.SetComponentEnabled<BulletTag>(bulletEntity, true);
                    }
                }
                finally
                {
                    targets.Dispose();
                    bestD2.Dispose();
                    origin.Dispose();
                }

                // 下次喷射间隔（从 SoldierState 读，UI 可调）
                state.ValueRW.FireCooldown = state.ValueRO.FireInterval > 0f
                    ? state.ValueRO.FireInterval
                    : 1f;
            }

            // 结构变化统一放在迭代结束后执行（1.4 禁止迭代中结构变化）
            if (_needExpand)
            {
                _needExpand = false;
                ExpandPool(512);
            }
        }

        /// <summary>扩池：批量创建子弹实体并整体禁用（一次结构操作 + 一次批量禁用）。</summary>
        private void ExpandPool(int count)
        {
            var created = new NativeArray<Entity>(count, Allocator.Temp);
            EntityManager.CreateEntity(_bulletArchetype, created);

            // BRG 路径（编辑器/PC）：子弹用 Batch 引用模式挂渲染（四参重载，无 RenderMeshArray）。
            // 不能用 RenderMeshArray：它是 managed shared component，池实体全部禁用期间
            // Entities Graphics 1.4 会回收该实例（实测 MaterialReferences 被置 null），
            // 复用缓存实例必 NRE；Batch 引用模式直接指向注册的 mesh/material，无此生命周期问题。
            // Instanced 路径由 WebGLGraphicsSystem 直接绘制，无需渲染组件。
            if (CrowdRenderSettings.UseBatchRendererGroup)
            {
                EnsureBulletIdsRegistered();
                foreach (var e in created)
                {
                    RenderMeshUtility.AddComponents(
                        e,
                        EntityManager,
                        new RenderMeshDescription(UnityEngine.Rendering.ShadowCastingMode.Off),
                        new MaterialMeshInfo(_bulletMatId, _bulletMeshId));
                }
            }

            for (int i = 0; i < count; i++)
                BulletPool.Free.Add(created[i]);
            created.Dispose();

            // 批量禁用（query 圈住全部子弹实体，池中本就全禁用，幂等）；
            // 逐实体 SetComponentEnabled 会反复触发查询增量同步，必须用批量 API
            EntityManager.SetComponentEnabled(
                GetEntityQuery(typeof(BulletTag)),
                ComponentType.ReadWrite<BulletTag>(),
                false);
        }

        /// <summary>
        /// 注册子弹的 mesh/material（仅 BRG 路径，懒执行一次）；
        /// BatchID 长期有效（RegisterMesh 的引用计数由系统管理），可跨多次扩池复用。
        /// </summary>
        private void EnsureBulletIdsRegistered()
        {
            if (_bulletIdsRegistered)
                return;
            var graphicsSystem = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<EntitiesGraphicsSystem>();
            _bulletMeshId = graphicsSystem.RegisterMesh(CrowdRenderSettings.Mesh);
            _bulletMatId = graphicsSystem.RegisterMaterial(CrowdRenderSettings.BulletMaterial);
            _bulletIdsRegistered = true;
        }

        /// <summary>找最近敌人：单遍 Job 求距离² 最小者，结果经 NativeReference 带回主线程。</summary>
        private Entity FindNearestEnemy(float3 origin)
        {
            if (_enemyQuery.CalculateEntityCount() == 0)
                return Entity.Null;

            using var nearest = new NativeReference<Entity>(Allocator.TempJob);
            using var bestD2 = new NativeReference<float>(float.MaxValue, Allocator.TempJob);
            var job = new FindNearestJob
            {
                Origin = origin,
                Nearest = nearest,
                BestD2 = bestD2,
            };
            job.Schedule(_enemyQuery, Dependency).Complete();
            return nearest.Value;
        }

        /// <summary>最近敌人搜索 Job（单线程，NativeReference 写结果）。</summary>
        [BurstCompile]
        private partial struct FindNearestJob : IJobEntity
        {
            public float3 Origin;
            public NativeReference<Entity> Nearest;
            public NativeReference<float> BestD2;

            private void Execute(in LocalTransform transform, Entity entity)
            {
                float d2 = math.lengthsq(transform.Position - Origin);
                if (d2 < BestD2.Value)
                {
                    BestD2.Value = d2;
                    Nearest.Value = entity;
                }
            }
        }

        /// <summary>
        /// 扇形目标分配 Job（单线程）：按"敌人相对喷射中心的角度"映射到桶索引，
        /// 每桶保留距离最近的敌人——这样 100 粒子弹各自有扇形内不同方向的目标，
        /// 命中面覆盖整个扇形。
        /// </summary>
        [BurstCompile]
        private partial struct AssignSectorTargetsJob : IJobEntity
        {
            [ReadOnly] public NativeReference<float3> Origin;
            public float CenterAngle;
            public float SpreadHalf;
            public int PelletCount;
            public NativeArray<Entity> Targets;
            public NativeArray<float> BestD2;

            private void Execute(in LocalTransform transform, Entity entity)
            {
                float3 toEnemy = transform.Position - Origin.Value;
                toEnemy.y = 0f;

                float rel = math.atan2(toEnemy.z, toEnemy.x) - CenterAngle;
                if (math.abs(rel) > math.PI)
                    rel -= math.sign(rel) * 2f * math.PI; // 环回到 -π..π
                if (math.abs(rel) > SpreadHalf)
                    return; // 扇形外的敌人不参与

                float d2 = math.lengthsq(toEnemy);
                // 相对角 [-半角, +半角] 线性映射到桶索引 [0, N-1]
                int idx = (int)math.round((rel + SpreadHalf) / (2f * SpreadHalf) * (PelletCount - 1));
                if (d2 < BestD2[idx])
                {
                    BestD2[idx] = d2;
                    Targets[idx] = entity;
                }
            }
        }
    }
}
