using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace EcsCrowdDemo
{
    /// <summary>
    /// WebGL / 微信小游戏平台的自定义渲染系统，无感知接管 Entities Graphics System
    /// （平台无 compute shader 时由 RuntimeCrowdBootstrap 切换启用）：
    /// - 多渲染批次（敌人 / 士兵 / 子弹），按各自 EntityQuery 收集矩阵，已隐藏的敌人自动排除；
    /// - Graphics.RenderMeshInstanced 直接接收 NativeArray 实例数据，零托管拷贝；
    /// - IJobEntity 单遍收集矩阵并做视锥裁剪，相机外的实例不提交 GPU；
    /// - 视锥平面用预分配数组刷新（零 GC），近远平面按相机朝向重建；
    /// - 敌人批次跟随模拟版本跳帧重建；士兵 / 子弹每帧重建；
    /// - RenderParams 预设场地包围盒，避免引擎按实例重算整体剔除盒。
    ///
    /// 为什么需要这个系统：
    /// Entities Graphics（BatchRendererGroup）依赖 compute shader 上传实例数据，
    /// 而 WebGL2 / 微信小游戏没有 compute 能力，EntitiesGraphicsSystem 会因
    /// IsEntitiesGraphicsSupportedOnSystem() 判定失败而自禁（日志 "Entities Graphics package disabled"）。
    /// 本系统用引擎提供的传统 GPU 实例化接口（GLES3 原生 instanced draw）补齐这条渲染链路。
    /// </summary>
    [UpdateAfter(typeof(CrowdMoveSystem))]
    public partial class WebGLGraphicsSystem : SystemBase
    {
        /// <summary>Unity 单次 GPU 实例化绘制的硬上限（引擎限制，超过必须拆分批次）。</summary>
        private const int kMaxBatchSize = 1023;

        /// <summary>批次类型：同一套收集/绘制流程，仅查询、材质与重建策略不同。</summary>
        private enum BatchKind { Enemy, Soldier, Bullet }

        /// <summary>一次渲染批次：一组同类实体的查询 + 实例矩阵缓冲 + 重建策略。</summary>
        private class RenderBatch
        {
            public BatchKind Kind;
            public EntityQuery Query;

            /// <summary>本批所有实例的矩阵（Persistent 常驻，按需扩容，跨帧复用避免分配）。</summary>
            public NativeList<Matrix4x4> Matrices;

            /// <summary>敌人批次按 1023 分片着彩虹色相；士兵 / 子弹用材质本色。</summary>
            public bool Rainbow;

            /// <summary>true = 仅在模拟推进（模拟版本变化）时重建矩阵，模拟暂停/隔帧时复用缓存，
            /// 这是"跳帧重建"性能优化的载体；士兵/子弹每帧都在动，必须每帧重建。</summary>
            public bool FollowSimVersion;

            /// <summary>上次重建时的实体数 / 模拟版本号——与当前值不一致则视为"状态已变化"。</summary>
            public int LastCount = -1;
            public int LastSimVersion = -1;
        }

        private readonly List<RenderBatch> _batches = new List<RenderBatch>();

        private MaterialPropertyBlock _props;
        private Camera _camera;

        /// <summary>上一帧相机的位置与朝向——用于检测视锥变化（触发敌人批次重建）。</summary>
        private float3 _lastCamPos;
        private quaternion _lastCamRot;
        private bool _hasLastCam;

        /// <summary>最近一帧提交渲染的实例总数（含敌人/士兵/子弹，视锥裁剪后），供调试与验证。</summary>
        public static int LastDrawnInstances;

        /// <summary>矩阵实际重建次数（跳帧优化生效时增长慢于帧数），供调试与验证。</summary>
        public static int RebuildCount;

        protected override void OnCreate()
        {
            // ── 敌人批次 ──
            // EntityQueryDesc 显式排除 Hidden（enableable）：被击败隐藏的敌人不参与渲染。
            // 注：enableable 组件的 None 语义 = "无该组件或该组件禁用"，因此敌人重生的
            // 禁用位切换会自动让查询重新匹配，本查询无需任何配合改动。
            _batches.Add(new RenderBatch
            {
                Kind = BatchKind.Enemy,
                Query = GetEntityQuery(new EntityQueryDesc
                {
                    All = new[]
                    {
                        ComponentType.ReadOnly<UnitTag>(),
                        ComponentType.ReadOnly<LocalTransform>(),
                    },
                    None = new[] { ComponentType.ReadOnly<Hidden>() },
                }),
                Matrices = new NativeList<Matrix4x4>(1024, Allocator.Persistent),
                Rainbow = true,
                FollowSimVersion = true,
            });

            // ── 士兵批次 ──（单实体，每帧位置由输入驱动）
            _batches.Add(new RenderBatch
            {
                Kind = BatchKind.Soldier,
                Query = GetEntityQuery(
                    ComponentType.ReadOnly<SoldierTag>(),
                    ComponentType.ReadOnly<LocalTransform>()),
                Matrices = new NativeList<Matrix4x4>(16, Allocator.Persistent),
            });

            // ── 子弹批次 ──（对象池预建实体，禁用的池子弹被 enableable 排除在查询外）
            _batches.Add(new RenderBatch
            {
                Kind = BatchKind.Bullet,
                Query = GetEntityQuery(
                    ComponentType.ReadOnly<BulletTag>(),
                    ComponentType.ReadOnly<LocalTransform>()),
                Matrices = new NativeList<Matrix4x4>(512, Allocator.Persistent),
            });

            _props = new MaterialPropertyBlock();
        }

        protected override void OnDestroy()
        {
            foreach (var batch in _batches)
                batch.Matrices.Dispose();
        }

        protected override void OnUpdate()
        {
            // BRG 可用（编辑器/PC）时由 Entities Graphics 负责渲染，本系统让位；
            // 渲染网格由 RuntimeCrowdBootstrap 在启动时注册
            if (CrowdRenderSettings.UseBatchRendererGroup ||
                CrowdRenderSettings.Mesh == null)
                return;

            // 共享视锥缓存：本帧任一消费者（子弹回收/血条）已刷新则直接复用（零重复计算、零 GC）
            if (_camera == null)
                _camera = Camera.main;
            bool cull = _camera != null;
            var planes = cull ? FrustumUtility.GetPlanes(_camera) : default;

            // 相机视锥变化（移动/旋转）也触发敌人批次重建：确保"视锥外物体不参与渲染"
            // 在相机运动时同样严格成立（相机静止 + 隔帧模拟时跳帧优化仍然生效）
            bool cameraMoved = false;
            if (cull)
            {
                var camPos = (float3)_camera.transform.position;
                var camRot = (quaternion)_camera.transform.rotation;
                if (_hasLastCam && (!math.all(camPos == _lastCamPos) || !camRot.value.Equals(_lastCamRot.value)))
                    cameraMoved = true;
                _lastCamPos = camPos;
                _lastCamRot = camRot;
                _hasLastCam = true;
            }

            // 模拟版本号：CrowdMoveSystem 每次真正执行模拟时递增（SharedStatic，
            // Burst 编译的系统内可写）；未变化的帧复用上次矩阵，跳过整个收集 Job
            int simVersion = CrowdMoveSystem.SimVersion.Data;

            // ── 阶段 1：调度各批次的矩阵收集 Job（链式依赖）──
            var handle = Dependency;
            foreach (var batch in _batches)
            {
                var mesh = CrowdRenderSettings.Mesh;
                if (MaterialFor(batch.Kind) == null)
                    continue;

                int count = batch.Query.CalculateEntityCount();
                if (count == 0)
                {
                    batch.Matrices.Clear();
                    batch.LastCount = 0;
                    continue;
                }

                // 重建条件：士兵/子弹每帧必重建；敌人仅在模拟推进、实体数变化或相机视锥
                // 变化时重建（隔帧模拟/暂停时复用缓存矩阵）
                bool rebuild = !batch.FollowSimVersion ||
                               count != batch.LastCount ||
                               simVersion != batch.LastSimVersion ||
                               cameraMoved;
                if (!rebuild)
                    continue; // 复用上次矩阵（上帧已 Complete，可安全读取）

                batch.LastCount = count;
                batch.LastSimVersion = simVersion;
                RebuildCount++;

                // 容量自适应：不足时按 2 倍向上取 2 的幂扩容（避免反复增长）
                if (batch.Matrices.Capacity < count)
                    batch.Matrices.SetCapacity(math.ceilpow2(count * 2));
                batch.Matrices.Clear();

                var job = new RenderDataJob
                {
                    Output = batch.Matrices.AsParallelWriter(),
                    CullingPlanes = planes,
                    MeshExtents = mesh.bounds.extents,
                    Cull = cull,
                };
                // 链式依赖：三个批次的收集 Job 串成一条链——同步点从 3 次合并为 1 次
                handle = job.ScheduleParallel(batch.Query, handle);
            }

            // ── 阶段 2：单次同步（Immediate Rendering 语义需要本帧矩阵）──
            handle.Complete();
            Dependency = handle; // 保持依赖链完整（已完成状态，无阻塞）

            // ── 阶段 3：绘制各批次 ──
            int totalDrawn = 0;
            foreach (var batch in _batches)
            {
                var mesh = CrowdRenderSettings.Mesh;
                var material = MaterialFor(batch.Kind);
                if (material == null)
                    continue;

                int drawn = batch.Matrices.Length;
                if (drawn == 0)
                    continue; // 视锥内无实例（例如相机背对场地）——全批剔除

                // RenderParams 为 struct（栈分配，无 GC）；worldBounds 预设一个覆盖全场
                // 的大盒，避免引擎每帧按实例矩阵重算整体剔除盒
                var rparams = new RenderParams(material)
                {
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = false,
                    lightProbeUsage = LightProbeUsage.Off,
                    matProps = _props,
                    worldBounds = new Bounds(Vector3.zero, new Vector3(400f, 20f, 400f)),
                };

                var array = batch.Matrices.AsArray();
                int batchCount = (drawn + kMaxBatchSize - 1) / kMaxBatchSize;
                for (int b = 0; b < batchCount; b++)
                {
                    // GetSubArray 生成零拷贝的切片视图（非分配）；实体按 chunk 顺序排列，
                    // 分片与"径向环带"天然对应——敌人据此做彩虹环带配色
                    int n = math.min(kMaxBatchSize, drawn - b * kMaxBatchSize);
                    var slice = array.GetSubArray(b * kMaxBatchSize, n);

                    if (batch.Rainbow)
                    {
                        // 每批一个色相（实体按 chunk 排批 ≈ 径向环带，整体呈彩虹环）；
                        // MPB 是共享引用，改内容即改下批颜色
                        float3 rgb = HsvToRgb(b * (1f / batchCount), 0.8f, 1f);
                        _props.SetColor("_BaseColor", new Color(rgb.x, rgb.y, rgb.z));
                    }
                    else
                    {
                        _props.SetColor("_BaseColor", Color.white);
                    }

                    Graphics.RenderMeshInstanced(rparams, mesh, 0, slice, n);
                }

                totalDrawn += drawn;
            }

            LastDrawnInstances = totalDrawn;
        }

        /// <summary>按批次类型解析材质（来自 RuntimeCrowdBootstrap 的启动注入）。</summary>
        private static Material MaterialFor(BatchKind kind)
        {
            switch (kind)
            {
                case BatchKind.Enemy: return CrowdRenderSettings.Material;
                case BatchKind.Soldier: return CrowdRenderSettings.SoldierMaterial;
                case BatchKind.Bullet: return CrowdRenderSettings.BulletMaterial;
                default: return null;
            }
        }

        /// <summary>
        /// 单遍收集 Job：对批次内每个实例做视锥剔除，然后生成 billboard 不需要的
        /// 简化矩阵。微信端 Burst 不可用（平台限制），此 Job 走 IL2CPP 托管执行，
        /// 因此矩阵构造做了省略旋转的简化（实例无旋转，仅平移 + 统一缩放）。
        /// </summary>
        [BurstCompile]
        private partial struct RenderDataJob : IJobEntity
        {
            /// <summary>批次矩阵缓冲的并行写入端（依赖链保证无写冲突）。</summary>
            public NativeList<Matrix4x4>.ParallelWriter Output;

            [ReadOnly] public NativeArray<float4> CullingPlanes;
            public float3 MeshExtents;
            public bool Cull;

            private void Execute(in LocalTransform transform)
            {
                // 视锥剔除：AABB 完全在视锥外则跳过——CPU 侧就不生成该实例，
                // 不产生 DrawInstance 提交（省矩阵上传带宽）
                if (Cull && FrustumUtility.IsOutside(CullingPlanes, transform.Position, MeshExtents * transform.Scale))
                    return;

                // 实例仅平移 + 统一缩放（无旋转），直接构造列主序矩阵（省去完整 TRS）
                float s = transform.Scale;
                Output.AddNoResize(new float4x4(
                    new float4(s, 0f, 0f, 0f),
                    new float4(0f, s, 0f, 0f),
                    new float4(0f, 0f, s, 0f),
                    new float4(transform.Position, 1f)));
            }
        }

        /// <summary>标准 HSV → RGB（用于彩虹环带配色）。</summary>
        private static float3 HsvToRgb(float h, float s, float v)
        {
            float3 k = new float3(1f, 2f / 3f, 1f / 3f);
            float3 p = math.abs(math.frac(new float3(h, h, h) + k) * 6f - 3f);
            return v * math.lerp(new float3(1f), math.clamp(p - 1f, 0f, 1f), s);
        }
    }
}
