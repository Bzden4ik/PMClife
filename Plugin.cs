using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;

namespace BotQuests
{
    [BepInPlugin("com.botquests.mod", "BotQuests", "1.0.0")]
    [BepInDependency("xyz.drakia.bigbrain")]
    [BepInDependency("xyz.drakia.waypoints")]
    [BepInDependency("me.sol.sain", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.terkoiz.freecam", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("me.skwizzy.lootingbots", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;
        public static readonly List<BotDebugInfo> ActiveBots = new List<BotDebugInfo>();

        private ConfigEntry<KeyboardShortcut> _toggleOverlay;
        private ConfigEntry<KeyboardShortcut> _nextBot;
        private ConfigEntry<KeyboardShortcut> _prevBot;

        private bool _showOverlay = true;
        private int  _watchedBotIndex = 0;

        private static readonly Dictionary<string, LineRenderer> _pathRenderers = new Dictionary<string, LineRenderer>();

        // FreeCam reflection — инициализируется лениво при первом нажатии
        private Type      _freecamPluginType;      // FreecamPlugin
        private Type      _freecamControllerType;  // FreecamController
        private FieldInfo _instanceField;           // FreecamPlugin.FreecamControllerInstance (static field)
        private FieldInfo _mainCameraField;         // FreecamController._mainCamera (private field)
        private FieldInfo _freeCamScriptField;      // FreecamController._freeCamScript (private field)
        private FieldInfo _isActiveField;           // Freecam.IsActive (public field)
        private bool      _freecamInitDone;
        private bool      _freecamAvailable;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("[BotQuests] Awake start");
            try
            {
                BotQuestsConfig.Init(Config);

                _toggleOverlay = Config.Bind("Hotkeys", "Toggle overlay",
                    new KeyboardShortcut(KeyCode.F10), "Показать/скрыть оверлей");
                _nextBot = Config.Bind("Hotkeys", "FreeCam next bot",
                    new KeyboardShortcut(KeyCode.RightBracket), "Телепорт FreeCam к следующему боту");
                _prevBot = Config.Bind("Hotkeys", "FreeCam prev bot",
                    new KeyboardShortcut(KeyCode.LeftBracket), "Телепорт FreeCam к предыдущему боту");

                Log.LogInfo("[BotQuests] Config OK, регистрируем слои...");

                BrainManager.AddCustomLayer(
                    typeof(BotQuestLayer),
                    new List<string> {
                        "Assault", "AssaultGroup",
                        "Pmcbot", "SptBear", "SptUsec",
                        "CursedAssault", "ExUsec", "ArenaFighter"
                    },
                    25
                );
                Log.LogInfo("[BotQuests] BotQuestLayer OK");

                BrainManager.AddCustomLayer(
                    typeof(BossAILayer),
                    new List<string> {
                        "BossBully", "BossKnight", "BossGluhar", "BossKilla",
                        "BossTagilla", "BossSanitar", "BossZryachiy", "BossPartisan",
                        "BossBoar", "BossBoarSniper", "BossKolontay"
                    },
                    24
                );
                Log.LogInfo("[BotQuests] BossAILayer OK");

                BrainManager.AddCustomLayer(
                    typeof(FollowerAILayer),
                    new List<string> {
                        "FollowerBully",
                        "FollowerGluharAssault", "FollowerGluharScout",
                        "FollowerGluharSecurity", "FollowerGluharSnipe",
                        "FollowerSanitar", "FollowerZryachiy", "FollowerTagilla",
                        "FollowerBigPipe", "FollowerBirdEye",
                        "FollowerKolontayAssault", "FollowerKolontaySecurityGuard",
                        "FollowerBoar", "FollowerBoarClose"
                    },
                    24
                );
                Log.LogInfo("[BotQuests] FollowerAILayer OK");

                Log.LogInfo("[BotQuests] Загружен. F10=оверлей, [/]=FreeCam по ботам.");
            }
            catch (Exception ex)
            {
                Log.LogError($"[BotQuests] Awake FAILED: {ex}");
            }
        }

        private void Update()
        {
            if (_toggleOverlay != null && _toggleOverlay.Value.IsDown()) _showOverlay = !_showOverlay;
            if (_nextBot != null && _nextBot.Value.IsDown()) CycleBot(+1);
            if (_prevBot != null && _prevBot.Value.IsDown()) CycleBot(-1);

            // Подписываем SoundTracker как только GameWorld стал доступен
            if (Singleton<GameWorld>.Instance != null && !SoundTracker.IsInitialized)
                SoundTracker.Init();

            if (Singleton<GameWorld>.Instance == null && ActiveBots.Count > 0)
            {
                ActiveBots.Clear();
                ClearPathRenderers();
                QuestSelector.ResetOnRaidStart();
            }

            // Обновляем LineRenderer-путь для каждого активного бота
            foreach (var info in ActiveBots)
            {
                if (info?.Bot == null || info.Bot.IsDead) continue;

                if (!_pathRenderers.TryGetValue(info.BotName, out var lr))
                {
                    var go = new GameObject($"PathViz_{info.BotName}");
                    lr = go.AddComponent<LineRenderer>();
                    lr.startWidth = 0.08f;
                    lr.endWidth   = 0.08f;
                    lr.material   = new Material(Shader.Find("Sprites/Default"));
                    lr.startColor = Color.cyan;
                    lr.endColor   = Color.cyan;
                    lr.positionCount = 0;
                    _pathRenderers[info.BotName] = lr;
                }

                var target = new Vector3(info.TargetX, 0f, info.TargetZ);
                if (target.sqrMagnitude > 0.01f)
                {
                    NavMesh.SamplePosition(target, out var hit, 10f, UnityEngine.AI.NavMesh.AllAreas);
                    var realTarget = hit.position != Vector3.zero ? hit.position : target;

                    var path = new UnityEngine.AI.NavMeshPath();
                    if (UnityEngine.AI.NavMesh.CalculatePath(info.Bot.Position, realTarget, UnityEngine.AI.NavMesh.AllAreas, path)
                        && path.corners.Length > 1)
                    {
                        lr.positionCount = path.corners.Length;
                        for (int i = 0; i < path.corners.Length; i++)
                            lr.SetPosition(i, path.corners[i] + Vector3.up * 0.15f);
                    }
                    else
                    {
                        // Путь не найден — рисуем прямую линию бот → цель
                        lr.positionCount = 2;
                        lr.SetPosition(0, info.Bot.Position + Vector3.up * 0.15f);
                        lr.SetPosition(1, realTarget + Vector3.up * 0.15f);
                    }
                }
                else
                {
                    lr.positionCount = 0;
                }
            }
        }

        // -----------------------------------------------------------------------
        // FreeCam интеграция
        // -----------------------------------------------------------------------

        private static void ClearPathRenderers()
        {
            foreach (var lr in _pathRenderers.Values)
                if (lr != null) UnityEngine.Object.Destroy(lr.gameObject);
            _pathRenderers.Clear();
        }

        private void TryInitFreecamReflection()
        {
            if (_freecamInitDone) return;
            _freecamInitDone = true;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (_freecamPluginType == null)
                        _freecamPluginType = asm.GetType("Terkoiz.Freecam.FreecamPlugin");
                    if (_freecamControllerType == null)
                        _freecamControllerType = asm.GetType("Terkoiz.Freecam.FreecamController");
                    if (_freecamPluginType != null && _freecamControllerType != null) break;
                }

                if (_freecamPluginType == null || _freecamControllerType == null)
                {
                    Log.LogWarning("[FreeCam] Типы FreecamPlugin/FreecamController не найдены");
                    return;
                }

                Log.LogInfo($"[FreeCam] Тип найден: {_freecamControllerType.FullName}");

                // FreecamPlugin.FreecamControllerInstance — static public FIELD
                _instanceField = _freecamPluginType.GetField(
                    "FreecamControllerInstance",
                    BindingFlags.Public | BindingFlags.Static);
                Log.LogInfo($"[FreeCam] FreecamControllerInstance field: {(_instanceField != null ? "OK" : "НЕ НАЙДЕНО")}");

                // FreecamController._mainCamera — private instance FIELD (GameObject)
                _mainCameraField = _freecamControllerType.GetField(
                    "_mainCamera",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Log.LogInfo($"[FreeCam] _mainCamera field: {(_mainCameraField != null ? "OK" : "НЕ НАЙДЕНО")}");

                // FreecamController._freeCamScript — для проверки IsActive
                _freeCamScriptField = _freecamControllerType.GetField(
                    "_freeCamScript",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Log.LogInfo($"[FreeCam] _freeCamScript field: {(_freeCamScriptField != null ? "OK" : "НЕ НАЙДЕНО")}");

                // Freecam.IsActive — public field
                var freecamScriptType = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Select(a => a.GetType("Terkoiz.Freecam.Freecam"))
                    .FirstOrDefault(t => t != null);
                _isActiveField = freecamScriptType?.GetField("IsActive",
                    BindingFlags.Public | BindingFlags.Instance);
                Log.LogInfo($"[FreeCam] IsActive field: {(_isActiveField != null ? "OK" : "НЕ НАЙДЕНО")}");

                _freecamAvailable = (_instanceField != null && _mainCameraField != null);
                Log.LogInfo($"[FreeCam] Инициализация завершена. Available={_freecamAvailable}");
            }
            catch (Exception ex)
            {
                Log.LogError($"[FreeCam] Ошибка инициализации: {ex.Message}");
            }
        }

        private void CycleBot(int direction)
        {
            Log.LogInfo($"[FreeCam] CycleBot direction={direction}, ActiveBots={ActiveBots.Count}");
            if (ActiveBots.Count == 0) { Log.LogWarning("[FreeCam] ActiveBots пуст — нет ботов для переключения"); return; }

            _watchedBotIndex = (_watchedBotIndex + direction + ActiveBots.Count) % ActiveBots.Count;
            var info = ActiveBots[_watchedBotIndex];
            Log.LogInfo($"[FreeCam] Переключились на [{_watchedBotIndex}] {info?.BotName}, bot={info?.Bot?.name}, dead={info?.Bot?.IsDead}");
            TeleportFreecamToBot(info?.Bot);
        }

        private void TeleportFreecamToBot(BotOwner bot)
        {
            if (!_freecamInitDone) TryInitFreecamReflection();

            if (!_freecamAvailable) { Log.LogWarning("[FreeCam] freecamAvailable=false, телепорт невозможен"); return; }
            if (bot == null)        { Log.LogWarning("[FreeCam] bot == null"); return; }
            if (bot.IsDead)         { Log.LogWarning($"[FreeCam] {bot.name} мёртв"); return; }

            try
            {
                // Берём FreecamPlugin.FreecamControllerInstance (static field)
                var controller = _instanceField.GetValue(null);
                if (controller == null) { Log.LogWarning("[FreeCam] FreecamControllerInstance == null — FreeCam ещё не запущен"); return; }

                // Проверяем IsActive через _freeCamScript.IsActive
                if (_freeCamScriptField != null && _isActiveField != null)
                {
                    var freecamScript = _freeCamScriptField.GetValue(controller);
                    if (freecamScript != null)
                    {
                        bool isActive = (bool)_isActiveField.GetValue(freecamScript);
                        if (!isActive) { Log.LogWarning("[FreeCam] FreeCam не активен — нажми Numpad0 для включения"); return; }
                    }
                }

                // Берём _mainCamera (GameObject) и двигаем его transform
                var mainCameraGO = _mainCameraField.GetValue(controller) as GameObject;
                if (mainCameraGO == null) { Log.LogWarning("[FreeCam] _mainCamera == null"); return; }

                Vector3 target = bot.Position + Vector3.up * 1.5f;
                mainCameraGO.transform.position = target;
                Log.LogInfo($"[FreeCam] Телепорт к {bot.name} → ({target.x:F1}, {target.y:F1}, {target.z:F1}) — успешно");
            }
            catch (Exception ex)
            {
                Log.LogError($"[FreeCam] Исключение при телепорте: {ex}");
            }
        }

        // -----------------------------------------------------------------------
        // Оверлей
        // -----------------------------------------------------------------------

        private void OnGUI()
        {
            if (!_showOverlay) return;

            int lineH = 18;
            int lines  = ActiveBots.Count + 2;
            GUI.Box(new Rect(10, 10, 500, 10 + lines * lineH), "");
            GUI.Label(new Rect(15, 12, 490, lineH),
                "BotQuests  |  F10=скрыть  [/]=FreeCam");
            GUI.Label(new Rect(15, 12 + lineH, 490, lineH),
                $"Активных ботов: {ActiveBots.Count}");

            for (int i = 0; i < ActiveBots.Count; i++)
            {
                var info = ActiveBots[i];
                string botPos = info.Bot != null
                    ? $"({info.Bot.Position.x:F0}, {info.Bot.Position.z:F0})"
                    : "мёртв";
                string marker = (i == _watchedBotIndex) ? "►" : " ";
                GUI.Label(new Rect(15, 12 + lineH * (2 + i), 490, lineH),
                    $"{marker} {info.BotName}  [{info.QuestType}]  {botPos}  →  ({info.TargetX:F0}, {info.TargetZ:F0})");
            }
        }
    }

    public class BotDebugInfo
    {
        public string   BotName;
        public BotOwner Bot;
        public float    TargetX;
        public float    TargetZ;
        public string   QuestType = "?";
    }
}
