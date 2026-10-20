using Unity.Entities;
using Unity.Mathematics;

namespace EcsCrowdDemo
{
    /// <summary>敌人生命值（1~3），归零后隐藏（击败）；血条按此值/3 显示比例。</summary>
    public struct EnemyHealth : IComponentData
    {
        public byte Value;
    }

    /// <summary>
    /// 敌人隐藏状态（enableable，常驻实体）：启用 = 隐藏中（移动/渲染/索敌跳过，
    /// WithNone&lt;Hidden&gt; 的查询自动把"禁用"视为不存在），倒计时结束禁用即重生。
    /// 启用/禁用是纯数据操作，不引起 structural change。
    /// </summary>
    public struct Hidden : IComponentData, IEnableableComponent
    {
        public float RespawnTimer;
    }

    /// <summary>玩家士兵标记。</summary>
    public struct SoldierTag : IComponentData { }

    /// <summary>士兵运行状态：锁定的目标、开火冷却与点击移动目标。</summary>
    public struct SoldierState : IComponentData
    {
        public Entity CurrentTarget;
        public float FireCooldown;
        public float MoveSpeed;
        public float FireInterval;

        /// <summary>点击地面设定的移动目标（到达即清）。</summary>
        public float3 MoveTarget;
        public bool HasMoveTarget;
    }

    /// <summary>
    /// 子弹标记（enableable，常驻池实体）：启用 = 飞行中；禁用 = 池中空闲。
    /// 查询（渲染批次 / 子弹系统）自动把"禁用"视为不存在，实现零结构变化的回收。
    /// </summary>
    public struct BulletTag : IComponentData, IEnableableComponent { }

    /// <summary>子弹数据：锁定目标、最后已知目标位置、速度与寿命。</summary>
    public struct Bullet : IComponentData
    {
        public Entity Target;
        public float3 LastTargetPos;
        public float Speed;
        public float Life;
    }

    /// <summary>玩法全局单例：击杀数与玩法参数。</summary>
    public struct GameplayState : IComponentData
    {
        public int Kills;
        public float BulletSpeed;
        public float BulletLife;
        public float RespawnTime;

        /// <summary>扇形喷射总张角（度）。</summary>
        public float SpreadAngle;
    }
}
