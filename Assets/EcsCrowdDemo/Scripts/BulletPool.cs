using System.Collections.Generic;
using Unity.Entities;

namespace EcsCrowdDemo
{
    /// <summary>
    /// 子弹对象池（主线程单线程访问，无锁）：
    /// 预建一批禁用的子弹实体循环复用，发射取用 / 回收归还，
    /// 消除高频 Create/Destroy 结构变化（约 800 次/秒）。
    /// 禁用通过 BulletTag 的 enableable 位实现（查询自动视为不存在）。
    /// </summary>
    public static class BulletPool
    {
        /// <summary>空闲子弹（已禁用，等待取用）。域重载自动重置，由 SoldierFireSystem 在 OnCreate 重建。</summary>
        public static readonly List<Entity> Free = new List<Entity>(1024);
    }
}
