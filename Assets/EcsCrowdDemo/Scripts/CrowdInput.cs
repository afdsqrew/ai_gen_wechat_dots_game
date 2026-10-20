using Unity.Mathematics;
using UnityEngine;

namespace EcsCrowdDemo
{
    /// <summary>
    /// 玩法输入采集：键盘 WASD/方向键 + 触摸左半屏虚拟摇杆 + 点击地面设定移动目标。
    /// Move（xz 平面单位方向）与 ClickMoveTarget（地面点）由 SoldierControlSystem 消费；
    /// 测试时可直接写 ExternalMove / ClickMoveTarget。
    /// </summary>
    public static class CrowdInput
    {
        public static float2 Move;

        /// <summary>外部注入的移动方向（优先于键盘/触摸采样），供测试或外部摇杆接入使用。</summary>
        public static float2 ExternalMove;

        /// <summary>最新点击移动目标（地面交点，未做场地 clamp，由消费端处理）；消费后置空。</summary>
        public static float3? ClickMoveTarget;

        private static int _moveTouchId = -1;
        private static Vector2 _touchOrigin;
        private static Vector3? _mouseDownPos;
        private static int _clickTouchId = -1;
        private static Vector2 _clickTouchStart;
        private static Camera _cam;

        public static void Sample()
        {
            SampleMove();
            SampleClickMove();
        }

        private static void SampleMove()
        {
            // 外部注入优先
            if (math.lengthsq(ExternalMove) > 0.0001f)
            {
                Move = ExternalMove;
                return;
            }

            float2 dir = float2.zero;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) dir.y += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) dir.y -= 1f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) dir.x -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) dir.x += 1f;

            // 触摸：左半屏第一个触点作为虚拟摇杆（起点到当前触点的方向）
            for (int i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);
                if (_moveTouchId < 0 && t.phase == TouchPhase.Began && t.position.x < Screen.width * 0.5f)
                {
                    _moveTouchId = t.fingerId;
                    _touchOrigin = t.position;
                }
                if (t.fingerId == _moveTouchId)
                {
                    if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                    {
                        _moveTouchId = -1;
                    }
                    else
                    {
                        var d = t.position - _touchOrigin;
                        if (d.magnitude > 20f)
                            dir = math.normalizesafe(new float2(d.x, d.y), float2.zero);
                    }
                }
            }
            if (Input.touchCount == 0)
                _moveTouchId = -1;

            Move = dir;
        }

        private static void SampleClickMove()
        {
            // 鼠标：按下-抬起位移小于阈值视为点击（拖拽是转相机）
            if (Input.GetMouseButtonDown(0))
                _mouseDownPos = Input.mousePosition;
            if (Input.GetMouseButtonUp(0) && _mouseDownPos.HasValue)
            {
                var up = Input.mousePosition;
                if ((up - _mouseDownPos.Value).sqrMagnitude < 100f && !IsOverUi(up) &&
                    ScreenToGround(up, out var hit))
                {
                    ClickMoveTarget = hit;
                }
                _mouseDownPos = null;
            }

            // 触摸：右半屏单指 tap（左半屏是摇杆）；双指捏合时不触发
            for (int i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);
                if (_clickTouchId < 0 && t.phase == TouchPhase.Began &&
                    t.position.x >= Screen.width * 0.5f && !IsOverUi(t.position))
                {
                    _clickTouchId = t.fingerId;
                    _clickTouchStart = t.position;
                }
                if (t.fingerId == _clickTouchId &&
                    (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled))
                {
                    if (t.phase == TouchPhase.Ended &&
                        (t.position - _clickTouchStart).sqrMagnitude < 400f &&
                        ScreenToGround(t.position, out var touchHit))
                    {
                        ClickMoveTarget = touchHit;
                    }
                    _clickTouchId = -1;
                }
            }
            if (Input.touchCount == 0)
                _clickTouchId = -1;
        }

        /// <summary>屏幕点 → 地面（y=0 平面）交点；相机朝上或交点在身后时返回 false。</summary>
        public static bool ScreenToGround(Vector3 screenPos, out float3 hit)
        {
            hit = default;
            if (_cam == null)
                _cam = Camera.main;
            if (_cam == null)
                return false;

            var ray = _cam.ScreenPointToRay(screenPos);
            if (Mathf.Abs(ray.direction.y) < 1e-4f)
                return false;
            float t = -ray.origin.y / ray.direction.y;
            if (t < 0f)
                return false;

            var point = ray.origin + ray.direction * t;
            hit = new float3(point.x, 0f, point.z);
            return true;
        }

        /// <summary>屏幕点是否落在左上角 UI 面板区域内（与 CrowdCamera 的屏蔽区一致）。</summary>
        private static bool IsOverUi(Vector3 screenPos)
        {
            return screenPos.x < 790f && screenPos.y > Screen.height - 270f;
        }
    }
}
