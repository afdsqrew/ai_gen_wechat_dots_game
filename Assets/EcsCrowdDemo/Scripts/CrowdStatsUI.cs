using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace EcsCrowdDemo
{
    /// <summary>
    /// 左上角运行数据（FPS / 单位数量 / 击杀数 / Workers）与运行时控制按钮。
    /// 使用固定 Rect 绘制（非 GUILayout）+ 统计字符串缓存，OnGUI 每帧零托管分配，
    /// 避免 WASM 环境的 GC 尖刺与 IMGUI 布局开销。
    /// </summary>
    public class CrowdStatsUI : MonoBehaviour
    {
        private const string kTitle = "DOTS Crowd Demo (ECS + Burst + Entities Graphics)";
        private const string kHint = "WASD / Left touch: move   Click ground: go there   Drag: orbit   Pinch: zoom";

        private float _windowTimer;  // 统计窗口计时（0.5s 一窗）
        private int _frames;         // 窗口内帧数
        private float _fps;
        private int _unitCount;
        private int _kills;

        /// <summary>缓存的数据行文本（0.5s 重建一次，OnGUI 期间只读）。</summary>
        private string _statsLine = "Units: 0   Kills: 0   FPS: 0   0.0 ms/frame   Workers: 0   Burst: ?";

        private EntityQuery _unitQuery;
        private EntityQuery _moveQuery;
        private EntityQuery _settingsQuery;
        private EntityQuery _gameplayQuery;
        private bool _queriesCreated;
        private bool _paused;         // UI 状态
        private bool _pauseApplied;   // 已实际生效的暂停状态（变化时才写 ECS）
        private GUIStyle _buttonStyle;

        private void Update()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
                return;
            var em = world.EntityManager;
            EnsureQueries(em);

            _frames++;
            _windowTimer += Time.unscaledDeltaTime;
            if (_windowTimer >= 0.5f)
            {
                // 窗口结算：帧率、数量、击杀——唯一产生字符串的时机（每秒 2 次）
                _fps = _frames / _windowTimer;
                float ms = 1000f / math.max(_fps, 0.01f);
                _unitCount = _unitQuery.CalculateEntityCount();
                if (_gameplayQuery.TryGetSingleton<GameplayState>(out var gameplay))
                    _kills = gameplay.Kills;
                // 统计字符串仅在窗口刷新时重建（0.5s 一次），OnGUI 期间零分配；
                // Burst 状态：真机上若显示 Off，说明 Job 在走 IL2CPP 托管执行（平台限制/构建未产出 Burst 代码）
                _statsLine = $"Units: {_unitCount}   Kills: {_kills}   FPS: {_fps:F0}   {ms:F1} ms/frame   " +
                             $"Workers: {JobsUtility.JobWorkerCount}   Burst: {(BurstCompiler.IsEnabled ? "On" : "Off")}";
                _frames = 0;
                _windowTimer = 0f;
            }

            // 暂停状态变化时才写 ECS（增删 MoveActive 单例）
            if (_paused != _pauseApplied)
                ApplyPause();
        }

        private void OnGUI()
        {
            // 固定 Rect 绘制：无 GUILayout 布局机、无每次调用的字符串插值——零 GC
            EnsureStyles();
            GUI.Box(new Rect(10f, 10f, 760f, 30f), kTitle);
            GUI.Box(new Rect(10f, 44f, 760f, 26f), _statsLine);
            if (GUI.Button(new Rect(10f, 78f, 170f, 52f), "+100", _buttonStyle))
                SpawnRequest(100);
            if (GUI.Button(new Rect(190f, 78f, 170f, 52f), "-100", _buttonStyle))
                Despawn(100);
            if (GUI.Button(new Rect(370f, 78f, 150f, 52f), "Reset", _buttonStyle))
                ResetCrowd();
            // Toggle 复用按钮样式：选中态显示按下背景，四个控件观感统一
            _paused = GUI.Toggle(new Rect(530f, 78f, 200f, 52f), _paused, _paused ? "Resume" : "Pause", _buttonStyle);
            GUI.Label(new Rect(10f, 138f, 640f, 24f), kHint);
        }

        private void EnsureStyles()
        {
            if (_buttonStyle != null)
                return;
            // 按钮放大 2 倍：字号与内边距翻倍，宽高随之约为原来的 2 倍
            _buttonStyle = ScaleStyle2x(new GUIStyle(GUI.skin.button));
        }

        private static GUIStyle ScaleStyle2x(GUIStyle style)
        {
            // fontSize 为 0 表示使用默认字号（IMGUI 默认为 13）
            int baseFont = style.fontSize > 0 ? style.fontSize : 13;
            style.fontSize = baseFont * 2;
            var padding = style.padding;
            style.padding = new RectOffset(
                padding.left * 2, padding.right * 2, padding.top * 2, padding.bottom * 2);
            return style;
        }

        /// <summary>查询缓存：仅在第一次 Update（ECS 世界就绪后）创建。</summary>
        private void EnsureQueries(EntityManager em)
        {
            if (_queriesCreated)
                return;
            _unitQuery = em.CreateEntityQuery(typeof(UnitTag));
            _moveQuery = em.CreateEntityQuery(typeof(MoveActive));
            _settingsQuery = em.CreateEntityQuery(typeof(CrowdSpawnSettings));
            _gameplayQuery = em.CreateEntityQuery(typeof(GameplayState));
            _queriesCreated = true;
        }

        /// <summary>+100 按钮：写入一个生成请求实体，由 UnitSpawnSystem 消费。</summary>
        private void SpawnRequest(int count)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
                return;
            var em = world.EntityManager;
            var e = em.CreateEntity(typeof(CrowdSpawnRequest));
            em.SetComponentData(e, new CrowdSpawnRequest { Count = count });
        }

        /// <summary>-100 按钮：按快照数组销毁前 N 个敌人（快照迭代，销毁过程安全）。</summary>
        private void Despawn(int count)
        {
            if (!_queriesCreated)
                return;
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var entities = _unitQuery.ToEntityArray(Allocator.Temp);
            int n = math.min(count, entities.Length);
            for (int i = 0; i < n; i++)
                em.DestroyEntity(entities[i]);
            entities.Dispose();
        }

        /// <summary>Reset 按钮：清空全部敌人 + 重置排布计数 + 重新发起满量生成请求。</summary>
        private void ResetCrowd()
        {
            if (!_queriesCreated)
                return;
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;

            var entities = _unitQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
                em.DestroyEntity(entities[i]);
            entities.Dispose();

            int count = 10000;
            if (_settingsQuery.TryGetSingleton(out CrowdSpawnSettings settings))
            {
                settings.SpawnedTotal = 0; // 重新从场地中心开始排布
                em.SetComponentData(_settingsQuery.GetSingletonEntity(), settings);
                count = settings.Count;
            }
            SpawnRequest(count);
        }

        /// <summary>暂停切换：增删 MoveActive 单例（CrowdMoveSystem 的 RequireForUpdate 条件）。</summary>
        private void ApplyPause()
        {
            if (!_queriesCreated)
                return;
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            if (_moveQuery.TryGetSingletonEntity<MoveActive>(out Entity moveEntity))
            {
                if (_paused)
                    em.RemoveComponent<MoveActive>(moveEntity);
            }
            else if (!_paused && _settingsQuery.TryGetSingletonEntity<CrowdSpawnSettings>(out Entity spawner))
            {
                em.AddComponent<MoveActive>(spawner);
            }
            _pauseApplied = _paused;
        }
    }
}
