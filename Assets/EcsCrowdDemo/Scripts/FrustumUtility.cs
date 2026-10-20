using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace EcsCrowdDemo
{
    /// <summary>
    /// 共享视锥工具：零 GC 刷新相机视锥平面 + AABB 完全在外判定。
    /// 带静态帧缓存：同帧内多个消费者（渲染剔除 / 子弹回收 / 血条）共享一次计算，
    /// 避免每帧 3 次重复的引擎调用；相机切换或跨帧时自动重算。
    /// </summary>
    public static class FrustumUtility
    {
        private static int _cachedFrame = -1;
        private static Camera _cachedCamera;
        private static NativeArray<float4> _cachedPlanes;
        private static readonly Plane[] _cachedManaged = new Plane[6];

        /// <summary>本会话累计的实际刷新次数（诊断用：应约等于帧数，而非 3 倍帧数）。</summary>
        public static int RefreshCount;

#if UNITY_EDITOR
        static FrustumUtility()
        {
            // 编辑器：退出 Play 时释放静态容器，避免泄漏告警
            UnityEditor.EditorApplication.playModeStateChanged += _ => DisposeCache();
        }
#else
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RegisterQuitCleanup() => Application.quitting += DisposeCache;
#endif

        /// <summary>
        /// 获取本帧的视锥平面（同帧重复调用返回缓存，零重复计算）。
        /// 返回的 NativeArray 只读使用（消费者不得修改）。
        /// </summary>
        public static NativeArray<float4> GetPlanes(Camera camera)
        {
            if (_cachedFrame != Time.frameCount ||
                !ReferenceEquals(_cachedCamera, camera) ||
                !_cachedPlanes.IsCreated)
            {
                if (!_cachedPlanes.IsCreated)
                    _cachedPlanes = new NativeArray<float4>(6, Allocator.Persistent);
                Refresh(camera, _cachedManaged, _cachedPlanes);
                _cachedFrame = Time.frameCount;
                _cachedCamera = camera;
            }
            return _cachedPlanes;
        }

        /// <summary>
        /// 从相机刷新 6 个视锥平面：写入预分配托管数组再转 NativeArray，全程零分配。
        /// （Unity.Rendering.FrustumPlanes.FromCamera 内部每帧分配新 Plane[]，在 WASM 上产生 GC 压力；
        /// 近远平面按相机朝向重建，与其修正逻辑一致。）
        /// </summary>
        public static void Refresh(Camera camera, Plane[] managedPlanes, NativeArray<float4> planes)
        {
            GeometryUtility.CalculateFrustumPlanes(camera, managedPlanes);

            var cameraToWorld = camera.cameraToWorldMatrix;
            var eyePos = cameraToWorld.MultiplyPoint(Vector3.zero);
            var viewDir = new float3(cameraToWorld.m02, cameraToWorld.m12, cameraToWorld.m22);
            viewDir = -math.normalizesafe(viewDir);

            // Near plane
            managedPlanes[4].SetNormalAndPosition(viewDir, eyePos);
            managedPlanes[4].distance -= camera.nearClipPlane;

            // Far plane
            managedPlanes[5].SetNormalAndPosition(-viewDir, eyePos);
            managedPlanes[5].distance += camera.farClipPlane;

            for (int i = 0; i < 6; i++)
            {
                planes[i] = new float4(
                    managedPlanes[i].normal.x, managedPlanes[i].normal.y, managedPlanes[i].normal.z,
                    managedPlanes[i].distance);
            }
            RefreshCount++;
        }

        /// <summary>AABB 是否完全在视锥外（用于实例剔除 / 子弹回收）。</summary>
        public static bool IsOutside(in NativeArray<float4> planes, float3 center, float3 extents)
        {
            for (int i = 0; i < planes.Length; i++)
            {
                float4 plane = planes[i];
                float dist = math.dot(plane.xyz, center) + plane.w;
                float radius = math.dot(extents, math.abs(plane.xyz));
                if (dist + radius <= 0f)
                    return true;
            }
            return false;
        }

        private static void DisposeCache()
        {
            if (_cachedPlanes.IsCreated)
                _cachedPlanes.Dispose();
            _cachedFrame = -1;
            _cachedCamera = null;
        }
    }
}
