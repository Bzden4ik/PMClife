using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LifePMC
{
    // ─────────────────────────────────────────────────────────────────────────
    // Модели данных — JSON-ключи совпадают с PointEditor (lowercase)
    // ─────────────────────────────────────────────────────────────────────────

    public class QuestPoint
    {
        [JsonProperty("id")]         public string Id        { get; set; }
        [JsonProperty("map")]        public string Map       { get; set; }
        [JsonProperty("quest_name")] public string QuestName { get; set; }
        [JsonProperty("zone_name")]  public string ZoneName  { get; set; }
        [JsonProperty("x")]          public float  X         { get; set; }
        [JsonProperty("y")]          public float  Y         { get; set; }
        [JsonProperty("z")]          public float  Z         { get; set; }
        [JsonProperty("wait_time")]  public float  WaitTime  { get; set; }
        [JsonIgnore] public Vector3 Position => new Vector3(X, Y, Z);
    }

    /// <summary>
    /// Саб-точка навигации — привязана к QuestPoint через ParentId.
    /// Используется для блуждания после прибытия на основную точку.
    /// </summary>
    public class SubPoint
    {
        [JsonProperty("id")]         public string Id       { get; set; }
        [JsonProperty("parent_id")]  public string ParentId { get; set; }
        [JsonProperty("map")]        public string Map      { get; set; }
        [JsonProperty("x")]          public float  X        { get; set; }
        [JsonProperty("y")]          public float  Y        { get; set; }
        [JsonProperty("z")]          public float  Z        { get; set; }
        /// <summary>"pass" = пройти мимо с лут-сканом, "stay" = подойти и постоять WaitTime секунд.</summary>
        [JsonProperty("type")]       public string Type     { get; set; } = "pass";
        [JsonProperty("wait_time")]  public float  WaitTime { get; set; }

        [JsonIgnore] public Vector3 Position => new Vector3(X, Y, Z);
        [JsonIgnore] public bool    IsStay   =>
            string.Equals(Type, "stay", StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Точка взаимодействия — рубильник, дверь, кнопка и т.п.
    // ─────────────────────────────────────────────────────────────────────────

    public class InteractPoint
    {
        [JsonProperty("id")]            public string Id           { get; set; }
        [JsonProperty("map")]           public string Map          { get; set; }
        [JsonProperty("description")]   public string Description  { get; set; } = "";
        [JsonProperty("x")]             public float  X            { get; set; }
        [JsonProperty("y")]             public float  Y            { get; set; }
        [JsonProperty("z")]             public float  Z            { get; set; }
        [JsonProperty("search_radius")] public float  SearchRadius { get; set; } = 3f;
        /// <summary>true = только первый бот за рейд взаимодействует.</summary>
        [JsonProperty("one_shot")]      public bool   OneShot      { get; set; } = true;
        /// <summary>
        /// Имя GameObject-а целевого объекта (записывается в PointEditor когда
        /// игрок сам взаимодействует). Если задано — боты ищут по имени (точно).
        /// </summary>
        [JsonProperty("target_name")]   public string TargetName   { get; set; } = "";
        [JsonIgnore] public Vector3 Position => new Vector3(X, Y, Z);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Статический загрузчик
    // ─────────────────────────────────────────────────────────────────────────

    public static class PointLoader
    {
        private static List<QuestPoint>    _points        = new List<QuestPoint>();
        private static List<SubPoint>      _subPoints     = new List<SubPoint>();
        private static List<InteractPoint> _interactPoints = new List<InteractPoint>();

        /// <summary>ID точек взаимодействия которые уже были выполнены в этом рейде.</summary>
        private static readonly HashSet<string> _triggeredInteractIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Слоты взаимодействия ─────────────────────────────────────────────
        // interactId → набор имён ботов, которые зарезервировали слот.
        // Максимум MaxInteractSlots ботов одновременно идут к одной точке.
        private static readonly Dictionary<string, HashSet<string>> _interactSlots =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        public static int MaxInteractSlots => LifePMCConfig.MaxInteractBots.Value;

        /// <summary>
        /// Бот пытается зарезервировать слот для interact-точки.
        /// Возвращает true если слот занят (или уже был занят этим ботом),
        /// false если все слоты заняты другими ботами.
        /// </summary>
        public static bool TryReserveInteract(string interactId, string botName)
        {
            if (!_interactSlots.TryGetValue(interactId, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _interactSlots[interactId] = set;
            }
            if (set.Contains(botName)) return true;   // уже наш слот
            if (set.Count >= MaxInteractSlots) return false; // все заняты
            set.Add(botName);
            Plugin.Log.LogInfo($"[LifePMC] 🔒 Слот [{interactId}]: {botName} " +
                               $"(занято {set.Count}/{MaxInteractSlots})");
            return true;
        }

        /// <summary>Освобождает слот бота (бой, смерть, выполнение).</summary>
        public static void ReleaseInteract(string interactId, string botName)
        {
            if (_interactSlots.TryGetValue(interactId, out var set) && set.Remove(botName))
                Plugin.Log.LogInfo($"[LifePMC] 🔓 Слот [{interactId}]: {botName} освобождён " +
                                   $"(осталось {set.Count}/{MaxInteractSlots})");
        }

        public static int GetInteractSlotCount(string interactId) =>
            _interactSlots.TryGetValue(interactId, out var set) ? set.Count : 0;

        public static bool IsLoaded { get; private set; }
        public static List<QuestPoint> GetPoints() => _points;

        public static void Load(string mapId, string saveDir)
        {
            _points.Clear();
            _subPoints.Clear();
            _interactPoints.Clear();
            _triggeredInteractIds.Clear();
            _interactSlots.Clear();
            IsLoaded = false;

            Plugin.Log.LogInfo($"[LifePMC] ── Загрузка данных для карты '{mapId}' ──");
            Plugin.Log.LogInfo($"[LifePMC] Папка: {saveDir}");

            // ── Основные точки ────────────────────────────────────────────────
            string pPath = Path.Combine(saveDir, mapId + ".json");
            Plugin.Log.LogInfo($"[LifePMC] Ищу файл точек: {pPath}");

            if (!File.Exists(pPath))
            {
                Plugin.Log.LogWarning($"[LifePMC] ФАЙЛ ТОЧЕК НЕ НАЙДЕН: {pPath}");
                Plugin.Log.LogWarning($"[LifePMC] Создай точки через PointEditor (F7 в рейде)");
            }
            else
            {
                try
                {
                    _points = JsonConvert.DeserializeObject<List<QuestPoint>>(File.ReadAllText(pPath))
                              ?? new List<QuestPoint>();
                    Plugin.Log.LogInfo($"[LifePMC] Загружено {_points.Count} точек:");
                    for (int i = 0; i < _points.Count; i++)
                    {
                        var p = _points[i];
                        Plugin.Log.LogInfo($"[LifePMC]   [{i}] {p.ZoneName}" +
                                           $"  ({p.X:F1},{p.Y:F1},{p.Z:F1})" +
                                           $"  wait={p.WaitTime:F0}с");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"[LifePMC] ОШИБКА загрузки точек: {ex.Message}");
                }
            }

            // ── Саб-точки ─────────────────────────────────────────────────────
            string sPath = Path.Combine(saveDir, mapId + "_subpoints.json");
            if (File.Exists(sPath))
            {
                try
                {
                    _subPoints = JsonConvert.DeserializeObject<List<SubPoint>>(File.ReadAllText(sPath))
                                 ?? new List<SubPoint>();
                    Plugin.Log.LogInfo($"[LifePMC] Загружено {_subPoints.Count} саб-точек:");
                    foreach (var s in _subPoints)
                        Plugin.Log.LogInfo($"[LifePMC]   sub={s.Id}  parent={s.ParentId}" +
                                           $"  type={s.Type}  ({s.X:F1},{s.Z:F1})" +
                                           (s.IsStay ? $"  wait={s.WaitTime:F0}с" : ""));
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"[LifePMC] ОШИБКА загрузки саб-точек: {ex.Message}");
                }
            }
            else
            {
                Plugin.Log.LogInfo($"[LifePMC] Саб-точек нет ({sPath})");
            }

            // ── Точки взаимодействия ──────────────────────────────────────────
            string iPath = Path.Combine(saveDir, mapId + "_interact.json");
            if (File.Exists(iPath))
            {
                try
                {
                    _interactPoints = JsonConvert.DeserializeObject<List<InteractPoint>>(File.ReadAllText(iPath))
                                      ?? new List<InteractPoint>();
                    Plugin.Log.LogInfo($"[LifePMC] Загружено {_interactPoints.Count} точек взаимодействия:");
                    foreach (var ip in _interactPoints)
                        Plugin.Log.LogInfo($"[LifePMC]   [!] {ip.Id}  \"{ip.Description}\"  " +
                                           $"r={ip.SearchRadius:F1}м  one_shot={ip.OneShot}");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"[LifePMC] ОШИБКА загрузки взаимодействий: {ex.Message}");
                }
            }
            else
            {
                Plugin.Log.LogInfo($"[LifePMC] Точек взаимодействия нет ({iPath})");
            }

            IsLoaded = true;
            Plugin.Log.LogInfo($"[LifePMC] Загрузка завершена. Точек={_points.Count}  Саб={_subPoints.Count}  Взаим={_interactPoints.Count}");
            Plugin.Log.LogInfo($"[LifePMC] ──────────────────────────────────────────");
        }

        public static void Reset()
        {
            Plugin.Log.LogInfo($"[LifePMC] PointLoader сброшен (было {_points.Count} точек, {_subPoints.Count} саб, {_interactPoints.Count} взаим)");
            _points.Clear();
            _subPoints.Clear();
            _interactPoints.Clear();
            _triggeredInteractIds.Clear();
            _interactSlots.Clear();
            IsLoaded = false;
        }

        public static QuestPoint SelectPoint()
        {
            if (_points.Count == 0) return null;
            return _points[UnityEngine.Random.Range(0, _points.Count)];
        }

        /// <summary>Возвращает все саб-точки для конкретной QuestPoint.</summary>
        public static List<SubPoint> GetSubPoints(string pointId)
        {
            if (string.IsNullOrEmpty(pointId)) return new List<SubPoint>();
            return _subPoints.FindAll(s =>
                string.Equals(s.ParentId, pointId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Возвращает список доступных точек взаимодействия.
        /// Для one_shot-точек — только ещё не выполненные в этом рейде.
        /// </summary>
        public static List<InteractPoint> GetInteractPoints()
        {
            return _interactPoints.FindAll(ip =>
                !ip.OneShot || !_triggeredInteractIds.Contains(ip.Id));
        }

        /// <summary>Помечает one_shot-точку как выполненную для текущего рейда.</summary>
        public static void MarkInteractTriggered(string id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                _triggeredInteractIds.Add(id);
                _interactSlots.Remove(id);  // снимаем все резервации — точка выполнена
            }
        }
    }
}
