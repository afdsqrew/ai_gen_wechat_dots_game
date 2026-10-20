using Unity.Entities;

namespace EcsCrowdDemo
{
    /// <summary>
    /// 敌人重生：隐藏倒计时结束即恢复——全部为纯数据写入（禁用 Hidden、回满血、
    /// 恢复渲染包围盒），不产生任何 structural change。
    /// </summary>
    public partial class RespawnSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            float dt = SystemAPI.Time.DeltaTime;

            // Hidden 为 enableable：作为查询参数只迭代"启用中"（隐藏中）的实体
            foreach (var (hidden, entity) in SystemAPI.Query<RefRW<Hidden>>().WithEntityAccess())
            {
                var h = hidden.ValueRO;
                h.RespawnTimer -= dt;
                if (h.RespawnTimer <= 0f)
                {
                    EntityManager.SetComponentEnabled<Hidden>(entity, false);
                    EntityManager.SetComponentData(entity, new EnemyHealth { Value = 3 });

                    // BRG 路径：恢复渲染包围盒（隐藏期间被置空以剔除渲染）
                    if (EntityManager.HasComponent<Unity.Rendering.RenderBounds>(entity))
                    {
                        EntityManager.SetComponentData(entity, new Unity.Rendering.RenderBounds
                        {
                            Value = CrowdRenderSettings.UnitBounds,
                        });
                    }
                }
                else
                {
                    hidden.ValueRW = h;
                }
            }
        }
    }
}
