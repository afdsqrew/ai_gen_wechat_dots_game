using Unity.Entities;
using UnityEngine;

namespace EcsCrowdDemo
{
    /// <summary>挂在单位预制体上：为被 Entities Graphics 渲染的实体烘焙出运动组件。</summary>
    public class CrowdUnitAuthoring : MonoBehaviour
    {
        public float MaxSpeed = 10f;
    }

    public class CrowdUnitBaker : Baker<CrowdUnitAuthoring>
    {
        public override void Bake(CrowdUnitAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new UnitTag());
            AddComponent(entity, new UnitMotion { MaxSpeed = authoring.MaxSpeed });
            AddComponent(entity, new UnitGoalOffset());
            // 预留实例颜色组件，生成时按单位写入不同颜色
            AddComponent<Unity.Rendering.URPMaterialPropertyBaseColor>(entity);
        }
    }
}
