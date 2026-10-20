using Unity.Entities;
using Unity.Mathematics;

namespace EcsCrowdDemo
{
    /// <summary>标记一个人群单位（敌人，由渲染层实例化绘制）。</summary>
    public struct UnitTag : IComponentData { }

    /// <summary>单位运动状态：当前速度、最大速度、随机游走种子（CrowdMoveSystem 读写）。</summary>
    public struct UnitMotion : IComponentData
    {
        public float3 Velocity;
        public float MaxSpeed;
        public uint Rng; // 每单位独立 PRNG 状态（随机游走用）
    }

    /// <summary>单位围绕全局吸引点的公转轨道参数，让整体人群形成漩涡状流动。</summary>
    public struct UnitGoalOffset : IComponentData
    {
        public float OrbitRadius; // 公转半径
        public float OrbitSpeed;  // 角速度（可负 = 反向公转）
        public float Phase;       // 初始相位
    }

    /// <summary>人群生成配置，由 RuntimeCrowdBootstrap 在运行时填充（单例实体）。</summary>
    public struct CrowdSpawnSettings : IComponentData
    {
        public Entity UnitPrefab;    // 模板实体（Prefab 标记）
        public int Count;            // 目标敌人数
        public int SpawnedTotal;     // 累计生成数（保证追加批次排布/颜色连续）
        public float ArenaRadius;    // 场地半径
        public float MaxSpeed;       // 敌人平均速度
        public float SeparationRadius; // 分离半径（兼作空间哈希格子尺寸）

        /// <summary>模拟频率档位：1 = 每帧模拟；2 = 隔帧模拟（30Hz 模拟 + 60fps 渲染），模拟成本减半。</summary>
        public byte SimInterval;

        /// <summary>邻域查询档位：0 = 3x3 网格；1 = 仅查所在网格（分离精度略降、查询成本约÷3）。</summary>
        public byte NeighborhoodMode;
    }

    /// <summary>生成请求：Bootstrap 产生初始请求，UI 运行时动态追加（+100 按钮）。</summary>
    public struct CrowdSpawnRequest : IComponentData
    {
        public int Count;
    }

    /// <summary>存在该单例组件时人群才更新；UI 的暂停按钮通过增删它实现暂停/恢复。</summary>
    public struct MoveActive : IComponentData { }
}
