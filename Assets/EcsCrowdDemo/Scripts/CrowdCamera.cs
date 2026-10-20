using UnityEngine;

namespace EcsCrowdDemo
{
    /// <summary>
    /// 环绕俯瞰相机：默认跟随士兵（士兵在 LateUpdate 之前由 ECS 更新位置），
    /// 支持鼠标拖拽旋转（带死区，点击不误转）、滚轮/双指缩放、空闲自动环绕。
    /// 纯 MonoBehaviour，不依赖 ECS——所有输入在 LateUpdate 采集，每帧重算相机位姿。
    /// </summary>
    public class CrowdCamera : MonoBehaviour
    {
        public Transform Target;          // 可选：显式跟随目标（优先级最高）
        public bool FollowSoldier = true; // 跟随士兵（从 CrowdRenderSettings.SoldierPosition 取位置）
        public float Distance = 135f;     // 环绕半径（拉远可看全场，拉近可看个体）
        public float MinDistance = 50f;
        public float MaxDistance = 240f;
        [Range(20f, 85f)] public float Elevation = 52f; // 仰角（度），决定俯视角度
        public float AutoOrbitSpeed = 4f;   // 空闲自动环绕角速度（度/秒）
        public float DragSpeed = 0.25f;     // 拖拽旋转灵敏度

        private float _azimuth = -35f;      // 当前方位角（度）
        private float _idleTimer;           // 无输入累计时间（超过阈值恢复自动环绕）
        private float _prevPinch = -1f;     // 上一帧双指间距（-1 = 无捏合状态）
        private bool _dragArmed;            // 拖拽激活标志（越过死区才转，点击不误转视角）
        private Vector3 _dragStart;         // 按下起点（死区判定用）

        private void LateUpdate()
        {
            // 鼠标拖拽（避开左上角 UI 区域）；带死区：点击（位移小）不转相机，交给点击移动
            bool overUi = Input.mousePosition.x < 790f && Input.mousePosition.y > Screen.height - 270f;
            if (Input.GetMouseButtonDown(0) && !overUi)
            {
                _dragArmed = false;
                _dragStart = Input.mousePosition;
            }
            if (Input.GetMouseButton(0) && !overUi)
            {
                // 死区 8px：越过才激活拖拽旋转——与"点击地面移动"互斥
                if (!_dragArmed && (Input.mousePosition - _dragStart).sqrMagnitude > 64f)
                    _dragArmed = true;
                if (_dragArmed)
                {
                    _azimuth -= Input.GetAxis("Mouse X") * DragSpeed;
                    Elevation = Mathf.Clamp(Elevation + Input.GetAxis("Mouse Y") * DragSpeed, 20f, 85f);
                }
                _idleTimer = 0f;
            }
            else
            {
                _idleTimer += Time.unscaledDeltaTime;
            }

            // 滚轮缩放
            Distance = Mathf.Clamp(Distance - Input.mouseScrollDelta.y * 10f, MinDistance, MaxDistance);

            // 双指捏合缩放（触屏）：间距变化比例映射到环绕半径
            if (Input.touchCount == 2)
            {
                var t0 = Input.GetTouch(0);
                var t1 = Input.GetTouch(1);
                float d = Vector2.Distance(t0.position, t1.position);
                if (_prevPinch > 0f && d > 0f)
                    Distance = Mathf.Clamp(Distance * _prevPinch / d, MinDistance, MaxDistance);
                _prevPinch = d;
            }
            else
            {
                _prevPinch = -1f;
            }

            // 空闲 3 秒后恢复自动环绕（跟随士兵模式下不转，保持玩法视角稳定）
            if (_idleTimer > 3f && !FollowSoldier)
                _azimuth += AutoOrbitSpeed * Time.unscaledDeltaTime;

            // 环绕中心：显式 Target > 士兵位置（ECS 每帧写入的静态位） > 原点
            Vector3 center;
            if (Target != null)
                center = Target.position;
            else if (FollowSoldier)
                center = (Vector3)CrowdRenderSettings.SoldierPosition;
            else
                center = Vector3.zero;

            // 球坐标 → 相机位姿（仰角、方位角、距离）
            float el = Elevation * Mathf.Deg2Rad;
            float az = _azimuth * Mathf.Deg2Rad;
            var offset = new Vector3(
                Mathf.Cos(el) * Mathf.Sin(az),
                Mathf.Sin(el),
                Mathf.Cos(el) * Mathf.Cos(az)) * Distance;
            transform.position = center + offset;
            transform.LookAt(center);
        }
    }
}
