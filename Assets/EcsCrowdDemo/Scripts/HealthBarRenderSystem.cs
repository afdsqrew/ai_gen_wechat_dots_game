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
    /// 敌人头顶血量条（DOTS）：
    /// - 单遍 IJobEntity 收集所有"受损且未被隐藏"敌人的 billboard 矩阵
    ///   （整宽背景条 + 按血量比例左锚定的填充条）；
    /// - Graphics.RenderMeshInstanced 分批绘制，程序生成 quad + 运行时材质，
    ///   独立于主渲染路径（编辑器 BRG 与微信 Instanced 通用）；
    /// - 满血不显示；被击败（隐藏）不显示；重生回满血自然消失；
    /// - 每帧面向相机，并做视锥剔除（视野外的血条不提交 GPU）；
    /// - 距离补偿：血条世界尺寸随与相机的距离缩放，屏幕上的大小基本恒定。
    ///
    /// 设计取舍：血条是纯渲染层派生数据——不建实体、不改敌人组件（零结构变化），
    /// 受伤/重生不需要任何血条相关的实体操作。
    /// </summary>
    [UpdateAfter(typeof(CrowdMoveSystem))]
    public partial class HealthBarRenderSystem : SystemBase
    {
        private const int kMaxBatchSize = 1023;   // 单次实例化绘制的引擎上限
        private const float kBarWidth = 1.8f;     // 血条宽度（米）
        private const float kBarHeight = 0.6f;    // 血条高度（米）
        private const float kHeadOffset = 1.1f;   // 相对敌人脚底（y=0）的头顶偏移
        private const byte kMaxHealth = 3;        // 与玩法数值一致的最大血量

        /// <summary>距离补偿基准：该相机距离下血条为 1 倍尺寸（更远放大、更近缩小，屏幕大小基本恒定）。</summary>
        private const float kBaseDistance = 135f;

        /// <summary>受伤且可见的敌人查询（Hidden 为 enableable，禁用者不匹配）。</summary>
        private EntityQuery _unitQuery;

        private NativeList<Matrix4x4> _backgrounds; // 背景条矩阵（整宽深色）
        private NativeList<Matrix4x4> _fills;       // 填充条矩阵（按血量比例亮色）

        private Mesh _quadMesh;
        private Material _bgMaterial;
        private Material _fgMaterial;
        private Camera _camera;

        /// <summary>最近一帧渲染的血条数，供调试与验证。</summary>
        public static int LastBarCount;

        protected override void OnCreate()
        {
            // 只收集"有血量数据且未隐藏"的敌人；HP<3 的过滤在 Job 内做（组件值无法在 query 层过滤）
            _unitQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<UnitTag>(),
                    ComponentType.ReadOnly<LocalTransform>(),
                    ComponentType.ReadOnly<EnemyHealth>(),
                },
                None = new[] { ComponentType.ReadOnly<Hidden>() },
            });

            _backgrounds = new NativeList<Matrix4x4>(256, Allocator.Persistent);
            _fills = new NativeList<Matrix4x4>(256, Allocator.Persistent);

            // 程序生成的单位 quad（中心原点，面朝 +Z）——2 三角，billboard 旋转由矩阵承担
            _quadMesh = new Mesh { name = "HealthBarQuad" };
            _quadMesh.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
            });
            _quadMesh.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            _quadMesh.RecalculateBounds();

            // 运行时材质：URP Unlit 已被工程内材质引用、必然存在于构建包中（Shader.Find 安全）；
            // 注意 enableInstancing 必须开，RenderMeshInstanced 才走实例化路径
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            _bgMaterial = new Material(unlit) { enableInstancing = true };
            _bgMaterial.SetColor("_BaseColor", new Color(0.04f, 0.04f, 0.05f)); // 纯黑底（与填充形成边框感）
            _fgMaterial = new Material(unlit) { enableInstancing = true };
            _fgMaterial.SetColor("_BaseColor", new Color(1f, 0.3f, 0.05f));     // 亮橙红填充
        }

        protected override void OnDestroy()
        {
            _backgrounds.Dispose();
            _fills.Dispose();
            if (_quadMesh != null) UnityEngine.Object.Destroy(_quadMesh);
            if (_bgMaterial != null) UnityEngine.Object.Destroy(_bgMaterial);
            if (_fgMaterial != null) UnityEngine.Object.Destroy(_fgMaterial);
        }

        protected override void OnUpdate()
        {
            // 共享视锥缓存（与渲染/子弹系统共用本帧的计算结果）
            if (_camera == null)
                _camera = Camera.main;
            bool cull = _camera != null;
            var planes = cull ? FrustumUtility.GetPlanes(_camera) : default;

            _backgrounds.Clear();
            _fills.Clear();

            // 容量自适应：极端情况下所有敌人都同时受损（上限 = 敌人总数），
            // ParallelWriter 的 AddNoResize 超出容量会抛异常
            int unitCount = _unitQuery.CalculateEntityCount();
            if (_backgrounds.Capacity < unitCount)
            {
                int newCapacity = math.ceilpow2(unitCount * 2);
                _backgrounds.SetCapacity(newCapacity);
                _fills.SetCapacity(newCapacity);
            }

            var camTransform = _camera != null ? _camera.transform : null;
            var camRight = camTransform != null ? (float3)camTransform.right : new float3(1f, 0f, 0f);
            var camUp = camTransform != null ? (float3)camTransform.up : new float3(0f, 1f, 0f);
            var camFwd = camTransform != null ? (float3)camTransform.forward : new float3(0f, 0f, 1f);
            var camPos = camTransform != null ? (float3)camTransform.position : float3.zero;

            var job = new CollectHealthBarsJob
            {
                Backgrounds = _backgrounds.AsParallelWriter(),
                Fills = _fills.AsParallelWriter(),
                CullingPlanes = planes,
                Cull = cull,
                CamRight = camRight,
                CamUp = camUp,
                CamFwd = camFwd,
                CamPos = camPos,
                BaseDistance = kBaseDistance,
                BarWidth = kBarWidth,
                BarHeight = kBarHeight,
                HeadOffset = kHeadOffset,
                MaxHealth = kMaxHealth,
            };
            // 依赖链承接模拟系统；血条每帧必须重建（billboard 随相机转动 + 跟随敌人）
            Dependency = job.ScheduleParallel(_unitQuery, Dependency);
            Dependency.Complete();

            LastBarCount = _backgrounds.Length;
            if (_backgrounds.Length == 0)
                return;

            DrawList(_backgrounds.AsArray(), _bgMaterial);
            DrawList(_fills.AsArray(), _fgMaterial);
        }

        /// <summary>单类血条（背景/填充）的分批绘制。</summary>
        private void DrawList(NativeArray<Matrix4x4> array, Material material)
        {
            int count = array.Length;
            if (count == 0)
                return;

            var rparams = new RenderParams(material)
            {
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                lightProbeUsage = LightProbeUsage.Off,
                worldBounds = new Bounds(Vector3.zero, new Vector3(400f, 20f, 400f)),
            };
            int batchCount = (count + kMaxBatchSize - 1) / kMaxBatchSize;
            for (int b = 0; b < batchCount; b++)
            {
                int n = math.min(kMaxBatchSize, count - b * kMaxBatchSize);
                Graphics.RenderMeshInstanced(rparams, _quadMesh, 0, array.GetSubArray(b * kMaxBatchSize, n), n);
            }
        }

        /// <summary>
        /// 血条收集 Job（并行）：
        /// 对每个受损敌人输出两张 billboard quad 矩阵——
        /// 背景条（整宽）与填充条（宽度 = 血量比例，左锚定：掉血从右侧缩短）。
        /// 矩阵列 = 相机 right/up/forward 的缩放（+Z 面朝相机），
        /// 沿视线偏移 0.01/0.02 消除背景与填充的 z-fighting。
        /// </summary>
        [BurstCompile]
        private partial struct CollectHealthBarsJob : IJobEntity
        {
            public NativeList<Matrix4x4>.ParallelWriter Backgrounds;
            public NativeList<Matrix4x4>.ParallelWriter Fills;

            [ReadOnly] public NativeArray<float4> CullingPlanes;
            public bool Cull;
            public float3 CamRight;
            public float3 CamUp;
            public float3 CamFwd;
            public float3 CamPos;
            public float BaseDistance;
            public float BarWidth;
            public float BarHeight;
            public float HeadOffset;
            public byte MaxHealth;

            private void Execute(in LocalTransform transform, in EnemyHealth health)
            {
                if (health.Value >= MaxHealth)
                    return; // 满血不显示

                // 距离补偿：血条在屏幕上的大小基本恒定（基准距离为 1，夹在 0.6~2.5）
                float dist = math.length(transform.Position - CamPos);
                float scale = math.clamp(dist / BaseDistance, 0.6f, 2.5f);
                float barWidth = BarWidth * scale;
                float barHeight = BarHeight * scale;

                float3 pos = transform.Position + new float3(0f, HeadOffset * scale, 0f);
                if (Cull && FrustumUtility.IsOutside(CullingPlanes, pos,
                        new float3(barWidth * 0.5f + 0.1f, barHeight * 0.5f + 0.1f, 0.1f)))
                    return;

                // 背景条：整宽 billboard（沿视线偏移 0.01 防与填充条 z-fight）
                Backgrounds.AddNoResize(new float4x4(
                    new float4(CamRight * barWidth, 0f),
                    new float4(CamUp * barHeight, 0f),
                    new float4(CamFwd, 0f),
                    new float4(pos + CamFwd * 0.01f, 1f)));

                // 填充条：宽度按血量比例、左锚定（高度略小、偏移 0.02）
                float ratio = math.clamp(health.Value / (float)MaxHealth, 0f, 1f);
                float3 fillCenter = pos - CamRight * (barWidth * 0.5f) + CamRight * (barWidth * ratio * 0.5f);
                Fills.AddNoResize(new float4x4(
                    new float4(CamRight * (barWidth * ratio), 0f),
                    new float4(CamUp * (barHeight * 0.68f), 0f),
                    new float4(CamFwd, 0f),
                    new float4(fillCenter + CamFwd * 0.02f, 1f)));
            }
        }
    }
}
