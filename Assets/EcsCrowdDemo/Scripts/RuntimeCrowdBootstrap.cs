using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace EcsCrowdDemo
{
    /// <summary>
    /// 运行时人群引导器：不依赖 SubScene 烘焙（WeixinMiniGame 构建目标不会部署
    /// SubScene 烘焙文件 StreamingAssets/EntityScenes，运行时解析 .entityheader 失败）。
    /// 渲染路径自动选择：
    /// - 平台支持 BatchRendererGroup（有 compute shader，如 PC/编辑器）→ Entities Graphics 渲染；
    /// - 否则（微信小游戏 / WebGL，Entities Graphics 被官方禁用）→ WebGLGraphicsSystem
    ///   的 Graphics.RenderMeshInstanced 后备渲染。
    /// 职责清单：
    /// 1. 创建敌人模板实体（Prefab 标记 + 全部人群组件）；
    /// 2. 创建生成配置单例（人数/场地/档位）与初始生成请求；
    /// 3. 创建玩法单例（GameplayState）与玩家士兵实体；
    /// 4. 注册 BRG 渲染资源（仅编辑器/PC 路径）。
    /// </summary>
    public class RuntimeCrowdBootstrap : MonoBehaviour
    {
        [Header("渲染资源")]
        public Mesh UnitMesh;              // 敌人/士兵/子弹共用的低模棋子网格
        public Material UnitMaterial;      // 敌人材质（白色基准，实例颜色由组件覆盖）
        public Material SoldierMaterial;   // 士兵材质（蓝色）
        public Material BulletMaterial;    // 子弹材质（亮黄）

        [Header("人群参数")]
        [Min(1)] public int Count = 500;          // 默认敌人数（微信端基准）
        public float ArenaRadius = 60f;           // 场地半径（米）
        public float MaxSpeed = 10f;              // 敌人平均移动速度
        public float SeparationRadius = 1f;       // 分离力作用半径（也是空间哈希的格子尺寸）
        public float UnitScale = 0.7f;            // 敌人整体缩放

        [Header("调试")]
        [Tooltip("强制使用 Instanced 后备渲染（不依赖 compute shader），用于在编辑器验证微信/WebGL 环境的渲染路径。")]
        public bool ForceInstancedRendering = false;

        [Header("性能档位（真机发热/掉帧时调）")]
        [Tooltip("模拟频率：1 = 每帧模拟；2 = 隔帧模拟（模拟成本减半，dt 自动补偿）。")]
        [Range(1, 4)] public int SimInterval = 1;
        [Tooltip("只查单位所在网格的邻居（分离精度略降），大幅减少邻域查询成本。")]
        public bool SingleCellSeparation = false;
        [Tooltip("限制 Job 工作线程数（微信多线程 WASM 生效时用于控内存/发热；0 = 不限制，按平台默认）。")]
        [Min(0)] public int MaxJobWorkers = 0;

        [Header("玩法：士兵扇形喷射")]
        public float SoldierMoveSpeed = 14f;
        [Tooltip("喷射间隔（秒），每次喷射 100 粒。")]
        [Range(0.05f, 3f)] public float FireInterval = 1f;
        public float BulletSpeed = 40f;
        [Tooltip("子弹寿命兜底（秒）；正常情况下离开视野即回收。")]
        public float BulletLife = 3f;
        [Tooltip("扇形喷射总张角（度）。")]
        [Range(10f, 180f)] public float SpreadAngle = 80f;
        [Tooltip("敌人被击败后的隐藏时长（秒），到点原地满血重生。")]
        public float RespawnTime = 5f;

        private void Start()
        {
            // 多线程 WASM 生效时按需限制 Job 工作线程数（编辑器/PC 无需关心）
            if (MaxJobWorkers > 0)
                Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount = MaxJobWorkers;

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                Debug.LogError("Default ECS world is not created; crowd bootstrap skipped.");
                return;
            }

            var em = world.EntityManager;

            // Entities Graphics 需要 compute shader（BatchRendererGroup compute 路径），
            // 微信小游戏/WebGL2 无 compute 支持，EntitiesGraphicsUtils 的内部判定与之等价
            bool useBrg = !ForceInstancedRendering &&
                          SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null &&
                          SystemInfo.supportsComputeShaders &&
                          UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null;
            CrowdRenderSettings.UseBatchRendererGroup = useBrg;
            CrowdRenderSettings.Mesh = UnitMesh;
            CrowdRenderSettings.SoldierMaterial = SoldierMaterial != null ? SoldierMaterial : UnitMaterial;
            CrowdRenderSettings.BulletMaterial = BulletMaterial != null ? BulletMaterial : UnitMaterial;
            if (!useBrg)
            {
                // 克隆材质并开启 GPU Instancing，避免改动原材质资产
                CrowdRenderSettings.Material = new Material(UnitMaterial) { enableInstancing = true };
                CrowdRenderSettings.SoldierMaterial = new Material(CrowdRenderSettings.SoldierMaterial) { enableInstancing = true };
                CrowdRenderSettings.BulletMaterial = new Material(CrowdRenderSettings.BulletMaterial) { enableInstancing = true };
            }

            // ── 敌人模板实体：携带人群组件；Prefab 标记使其不参与渲染与查询 ──
            var template = em.CreateEntity();
            em.AddComponent<Unity.Entities.Prefab>(template);
            em.AddComponent<LocalTransform>(template);
            em.SetComponentData(template, LocalTransform.FromScale(UnitScale));
            em.AddComponent<UnitTag>(template);
            em.AddComponent<UnitMotion>(template);
            em.SetComponentData(template, new UnitMotion { MaxSpeed = MaxSpeed });
            em.AddComponent<UnitGoalOffset>(template);
            em.AddComponent<URPMaterialPropertyBaseColor>(template);
            em.AddComponent<EnemyHealth>(template);
            em.SetComponentData(template, new EnemyHealth { Value = 3 });
            // Hidden 常驻（enableable）：禁用 = 未隐藏；隐藏/重生只做启用位切换，零结构变化
            em.AddComponent<Hidden>(template);
            em.SetComponentEnabled<Hidden>(template, false);

            // BRG 路径渲染包围盒的恢复值（隐藏时被置空以剔除渲染）
            CrowdRenderSettings.UnitBounds = new Unity.Mathematics.AABB
            {
                Center = (Unity.Mathematics.float3)UnitMesh.bounds.center * UnitScale,
                Extents = (Unity.Mathematics.float3)UnitMesh.bounds.extents * UnitScale,
            };

            if (useBrg)
            {
                // BRG 路径：挂渲染组件。
                // Entities Graphics 1.4 变更：MaterialMeshInfo 的 BatchID 构造是"运行时引用"
                // 语义（正值编码，IsRuntimeMesh=true），与 RenderMeshArray 搭配时 GetMesh
                // 返回 null 并报错；必须用 FromRenderMeshArrayIndices（负数静态索引编码）。
                // RenderMeshArray 由 Entities Graphics 托管解析，无需手动 RegisterMesh/RegisterMaterial
                RenderMeshUtility.AddComponents(
                    template,
                    em,
                    new RenderMeshDescription(UnityEngine.Rendering.ShadowCastingMode.Off),
                    new RenderMeshArray(new[] { UnitMaterial }, new[] { UnitMesh }),
                    MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            }

            // ── 生成配置实体：承担原 CrowdSpawner Baker 的职责 ──
            var settings = em.CreateEntity(
                typeof(CrowdSpawnSettings), typeof(CrowdSpawnRequest), typeof(MoveActive));
            em.SetComponentData(settings, new CrowdSpawnSettings
            {
                UnitPrefab = template,
                Count = Count,
                ArenaRadius = ArenaRadius,
                MaxSpeed = MaxSpeed,
                SeparationRadius = SeparationRadius,
                SimInterval = (byte)Mathf.Clamp(SimInterval, 1, 4),
                NeighborhoodMode = (byte)(SingleCellSeparation ? 1 : 0),
            });
            em.SetComponentData(settings, new CrowdSpawnRequest { Count = Count });

            // ── 玩法状态单例（击杀数 + 玩法参数）──
            var gameplay = em.CreateEntity(typeof(GameplayState));
            em.SetComponentData(gameplay, new GameplayState
            {
                BulletSpeed = BulletSpeed,
                BulletLife = BulletLife,
                RespawnTime = RespawnTime,
                SpreadAngle = SpreadAngle,
            });

            // ── 玩家士兵：位置在场地内，由 SoldierControlSystem（输入）驱动 ──
            var soldier = em.CreateEntity(typeof(LocalTransform), typeof(SoldierTag), typeof(SoldierState));
            var soldierTransform = LocalTransform.FromScale(1.2f);
            soldierTransform.Position = new float3(0f, 0f, 30f);
            em.SetComponentData(soldier, soldierTransform);
            em.SetComponentData(soldier, new SoldierState
            {
                CurrentTarget = Entity.Null,
                MoveSpeed = SoldierMoveSpeed,
                FireInterval = FireInterval,
            });
            CrowdRenderSettings.SoldierPosition = new float3(0f, 0f, 30f);

            if (useBrg)
            {
                // 1.4 语义：RenderMeshArray 静态索引模式（见敌人模板处的说明）
                RenderMeshUtility.AddComponents(
                    soldier,
                    em,
                    new RenderMeshDescription(UnityEngine.Rendering.ShadowCastingMode.Off),
                    new RenderMeshArray(new[] { SoldierMaterial != null ? SoldierMaterial : UnitMaterial }, new[] { UnitMesh }),
                    MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
            }
        }
    }
}
