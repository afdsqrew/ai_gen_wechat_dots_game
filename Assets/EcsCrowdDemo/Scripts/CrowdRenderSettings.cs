using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace EcsCrowdDemo
{
    /// <summary>渲染路径选择与共享引用，由 RuntimeCrowdBootstrap 在启动时填充。</summary>
    public static class CrowdRenderSettings
    {
        /// <summary>
        /// true = BatchRendererGroup（Entities Graphics，需要平台支持 compute shader）；
        /// false = WebGLGraphicsSystem 的 RenderMeshInstanced 后备（微信小游戏 / WebGL 等无 compute 平台）。
        /// </summary>
        public static bool UseBatchRendererGroup = true;

        /// <summary>敌人 / 士兵 / 子弹共用的低模网格。</summary>
        public static Mesh Mesh;

        public static Material Material;        // 敌人（Instanced 路径下为克隆并开启 instancing 的副本）
        public static Material SoldierMaterial; // 士兵
        public static Material BulletMaterial;  // 子弹

        /// <summary>敌人单位的渲染包围盒（BRG 路径 RenderBounds 的恢复值，隐藏时被置空以剔除渲染）。</summary>
        public static AABB UnitBounds;

        /// <summary>士兵当前位置，SoldierControlSystem 每帧更新，相机跟随用。</summary>
        public static float3 SoldierPosition;
    }
}
