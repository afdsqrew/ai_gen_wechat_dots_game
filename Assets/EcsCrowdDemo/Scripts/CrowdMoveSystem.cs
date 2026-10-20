using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace EcsCrowdDemo
{
    /// <summary>
    /// 群体移动系统（每帧模拟核心）：
    /// 1) ClearGridJob 清空空间哈希网格；
    /// 2) BuildGridJob 并行把所有单位位置写入网格（O(N)）；
    /// 3) MoveJob 并行做"寻的 + 分离"转向并积分位置（O(N × 邻域)）。
    /// 三个 Job 依赖链顺序调度，主线程零逐实体逻辑。
    ///
    /// 平台说明：编辑器下 Burst 编译并行执行；微信小游戏无 Burst（平台限制），
    /// 走 IL2CPP 托管单线程执行，通过 SimInterval（隔帧模拟）与 NeighborhoodMode
    /// （邻域降档）两个运行时档位控制成本。
    /// </summary>
    [BurstCompile]
    public partial struct CrowdMoveSystem : ISystem
    {
        /// <summary>
        /// 空间哈希网格：key = 网格坐标打包值，value = 单位位置。
        /// 容量初始 1024（随单位数按需扩容），Persistent 跨帧复用避免分配。
        /// 也被隐藏/重生的 enableable 语义间接关联——仅存活的未隐藏单位参与。
        /// </summary>
        private NativeParallelMultiHashMap<int, float3> _grid;

        /// <summary>隔帧模拟的帧计数器（配合 SimInterval 取模）。</summary>
        private int _frameCounter;

        /// <summary>SharedStatic 的类型标记（GetOrCreate 需要一个唯一子类型）。</summary>
        private class SimVersionTag { }

        /// <summary>
        /// 模拟版本号（Burst 可写的共享静态）：每次真正执行模拟递增，
        /// 供渲染系统（WebGLGraphicsSystem）判断实例矩阵是否需要重建。
        /// 用 SharedStatic 而非普通 static：Burst 编译的系统内不允许写普通静态字段（BC1040）。
        /// </summary>
        internal static readonly SharedStatic<int> SimVersion =
            SharedStatic<int>.GetOrCreate<CrowdMoveSystem, SimVersionTag>();

        public void OnCreate(ref SystemState state)
        {
            _grid = new NativeParallelMultiHashMap<int, float3>(1024, Allocator.Persistent);
            state.RequireForUpdate<UnitTag>();
            state.RequireForUpdate<CrowdSpawnSettings>();
            state.RequireForUpdate<MoveActive>(); // UI 的暂停按钮通过增删该单例控制模拟
        }

        public void OnDestroy(ref SystemState state)
        {
            // 结构变化/容器释放前必须等所有 Job 完成，否则安全系统报错
            state.CompleteDependency();
            if (_grid.IsCreated)
                _grid.Dispose();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var settings = SystemAPI.GetSingleton<CrowdSpawnSettings>();

            // 模拟降频：SimInterval > 1 时隔帧执行、dt 按倍数补偿（渲染仍每帧进行）；
            // 注意帧计数器只在非跳过帧递增，保证与渲染系统的版本号判断一致
            int simInterval = math.max(1, settings.SimInterval);
            if (simInterval > 1 && ++_frameCounter % simInterval != 0)
                return;

            // dt 补偿：隔帧执行时用 2 倍 dt 积分，保证运动速度与满频模拟一致
            float dt = SystemAPI.Time.DeltaTime * simInterval;
            float t = (float)SystemAPI.Time.ElapsedTime;

            // IJobEntity 的自定义查询必须包含 Execute 参数的全部组件（否则调度抛异常）；
            // 显式加 UnitTag 是为了排除其他带 LocalTransform 的实体
            var unitQuery = SystemAPI.QueryBuilder()
                .WithAll<UnitTag, LocalTransform, UnitMotion, UnitGoalOffset>()
                .Build();
            int unitCount = unitQuery.CalculateEntityCount();
            if (unitCount == 0)
                return;

            // 网格容量按单位数自适应扩容（哈希表桶数取 2 倍向上取 2 的幂）
            if (_grid.Capacity < unitCount)
            {
                state.CompleteDependency(); // 扩容前必须等在用的 Job 完成
                _grid.Dispose();
                _grid = new NativeParallelMultiHashMap<int, float3>(math.ceilpow2(unitCount * 2), Allocator.Persistent);
            }

            // 全局吸引点做 Lissajous 漫游，整群人随它迁移（产生大范围群体的"潮汐"感）
            float3 attractor = new float3(
                math.sin(t * 0.23f) * settings.ArenaRadius * 0.45f,
                0f,
                math.sin(t * 0.15f + 1.9f) * settings.ArenaRadius * 0.45f);

            float cellSize = settings.SeparationRadius;

            // 三段 Job 依赖链：Clear(单线程) → Build(并行写) → Move(并行读写)；
            // 邻域查询档位：Disabled（mode=2）完全跳过空间哈希——Clear/Build 均不调度，
            // MoveJob 的邻域循环在 NeighborhoodRange < 0 时天然不执行（-(-1)=1 > -1）
            int neighborhoodRange = settings.NeighborhoodMode == 0 ? 1 :
                                    settings.NeighborhoodMode == 1 ? 0 : -1;

            JobHandle moveDependency = state.Dependency;
            if (neighborhoodRange >= 0)
            {
                var clearHandle = new ClearGridJob { Grid = _grid }.Schedule(state.Dependency);
                moveDependency = new BuildGridJob
                {
                    Grid = _grid.AsParallelWriter(),
                    CellSize = cellSize,
                }.ScheduleParallel(unitQuery, clearHandle);
            }

            state.Dependency = new MoveJob
            {
                Grid = _grid,
                Attractor = attractor,
                CellSize = cellSize,
                DeltaTime = dt,
                Time = t,
                ArenaRadius = settings.ArenaRadius,
                SeparationRadius = settings.SeparationRadius,
                NeighborhoodRange = neighborhoodRange,
            }.ScheduleParallel(unitQuery, moveDependency);

            // Job 调度完成即推进版本号（Job 异步执行，但渲染系统的读取 Job 依赖本 Job，
            // 数据一致性由依赖链保证）
            SimVersion.Data++;
        }

        // 场地有限，网格坐标直接打包为唯一 key，避免哈希碰撞带来的额外邻居；
        // 偏移 1024 兼容负坐标，4096 容差覆盖 ±1023 格（60m 半径 / 1m 格 ≈ ±60 格，余量充足）
        private static int CellKey(int cx, int cz)
        {
            return (cx + 1024) * 4096 + (cz + 1024);
        }

        private static int CellKey(float3 pos, float cellSize)
        {
            return CellKey((int)math.floor(pos.x / cellSize), (int)math.floor(pos.z / cellSize));
        }

        /// <summary>每帧清空网格（单线程 IJob；并行写入前必须先清，链在依赖头部）。</summary>
        [BurstCompile]
        private struct ClearGridJob : IJob
        {
            public NativeParallelMultiHashMap<int, float3> Grid;

            public void Execute()
            {
                Grid.Clear();
            }
        }

        /// <summary>并行把每个单位的位置写入其所在网格 cell（供 MoveJob 做邻居查询）。</summary>
        [BurstCompile]
        [WithAll(typeof(UnitTag))]
        private partial struct BuildGridJob : IJobEntity
        {
            public NativeParallelMultiHashMap<int, float3>.ParallelWriter Grid;
            public float CellSize;

            private void Execute(in LocalTransform transform)
            {
                Grid.Add(CrowdMoveSystem.CellKey(transform.Position, CellSize), transform.Position);
            }
        }

        /// <summary>
        /// 群体转向与积分 Job（并行）：
        /// 目标 = 全局吸引点 + 单位自身公转偏移（形成漩涡流动）；
        /// 转向 = 寻的 + 邻域分离力 + 随机游走；越界向内推；速度平滑 + 限速 + 积分。
        /// </summary>
        [BurstCompile]
        [WithAll(typeof(UnitTag))]
        private partial struct MoveJob : IJobEntity
        {
            [ReadOnly] public NativeParallelMultiHashMap<int, float3> Grid;

            public float3 Attractor;
            public float CellSize;
            public float DeltaTime;
            public float Time;
            public float ArenaRadius;
            public float SeparationRadius;
            public int NeighborhoodRange;

            private void Execute(ref LocalTransform transform, ref UnitMotion motion, in UnitGoalOffset goal)
            {
                float3 pos = transform.Position;
                float3 vel = motion.Velocity;

                // 目标点 = 全局吸引点 + 单位自身的公转偏移
                //（每个单位有自己的轨道半径/速度/相位，叠加出群体漩涡形态）
                float theta = Time * goal.OrbitSpeed + goal.Phase;
                float3 target = Attractor + new float3(
                    math.cos(theta) * goal.OrbitRadius, 0f, math.sin(theta) * goal.OrbitRadius);
                float3 toTarget = target - pos;
                toTarget.y = 0f;
                float dist = math.length(toTarget);
                float3 seek = dist > 1e-3f ? toTarget / dist : float3.zero;

                // 分离力：查询邻域网格（range=1 → 3x3；range=0 → 仅所在格），避免局部堆叠；
                // checkedCount 上限 32 防高密度区域查询退化
                float3 separation = float3.zero;
                int checkedCount = 0;
                int cx = (int)math.floor(pos.x / CellSize);
                int cz = (int)math.floor(pos.z / CellSize);
                float sepR2 = SeparationRadius * SeparationRadius;
                for (int dx = -NeighborhoodRange; dx <= NeighborhoodRange; dx++)
                {
                    for (int dz = -NeighborhoodRange; dz <= NeighborhoodRange; dz++)
                    {
                        if (checkedCount >= 32)
                            break;
                        float3 other;
                        NativeParallelMultiHashMapIterator<int> it;
                        if (!Grid.TryGetFirstValue(CrowdMoveSystem.CellKey(cx + dx, cz + dz), out other, out it))
                            continue;
                        do
                        {
                            float3 diff = pos - other;
                            diff.y = 0f;
                            float d2 = math.lengthsq(diff);
                            if (d2 > 1e-8f && d2 < sepR2)
                                separation += diff / (d2 + 0.05f); // 1/d 衰减的排斥
                            if (++checkedCount >= 32)
                                break;
                        } while (Grid.TryGetNextValue(out other, ref it));
                    }
                }

                // 轻微随机游走，避免运动过于机械（每单位独立推进的 PRNG 状态）
                var rng = new Unity.Mathematics.Random(motion.Rng == 0u ? 1u : motion.Rng);
                float2 jitter = rng.NextFloat2(-1f, 1f);
                motion.Rng = rng.NextUInt();

                // 合成期望速度：寻的（满速）+ 分离（强权重，防堆叠）+ 随机抖动（弱）
                float3 desired = seek * math.max(motion.MaxSpeed, 0.1f)
                    + separation * 8f
                    + new float3(jitter.x, 0f, jitter.y) * 1.2f;

                // 场地边界：越界时向内推
                float r = math.length(pos.xz);
                if (r > ArenaRadius)
                {
                    desired -= new float3(pos.x, 0f, pos.z) / r * motion.MaxSpeed * 2f;
                }

                // 速度平滑（转向响应）、限速（留 1.4 倍余量给分离力）+ 积分位置
                vel = math.lerp(vel, desired, math.clamp(DeltaTime * 3f, 0f, 1f));
                float speed = math.length(vel);
                float maxSpeed = math.max(motion.MaxSpeed, 0.1f) * 1.4f;
                if (speed > maxSpeed)
                    vel = vel / speed * maxSpeed;
                motion.Velocity = vel;

                pos += vel * DeltaTime;
                pos.y = 0f; // 纯平面人群，锁死高度
                transform.Position = pos;
            }
        }
    }
}
