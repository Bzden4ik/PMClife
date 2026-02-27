using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace BotQuests
{
    /// <summary>
    /// Загружает квесты EFT из quests.json, резолвит zoneId → позиции через TriggerWithId на сцене.
    /// </summary>
    public static class EftQuestLoader
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("BotQuests.EftQuestLoader");

        // Путь к базе квестов SPT — в подпапке SPT относительно папки игры
        private static readonly string QuestsJsonPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "SPT", "SPT_Data", "database", "templates", "quests.json");

        // Маппинг locationId → имя папки
        private static readonly Dictionary<string, string> LocationIdToFolder =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "56f40101d2720b2a4d8b45d6", "bigmap" },
            { "55f2d3fd4bdc2d5f408b4567", "factory4_day" },
            { "59fc81d786f774390775787e", "factory4_night" },
            { "5714dbc024597771384a510d", "interchange" },
            { "5704e4dad2720bb55b8b4567", "lighthouse" },
            { "5704e5fad2720bc05b8b4567", "rezervbase" },
            { "5704e554d2720bac5b8b456e", "shoreline" },
            { "5714dc692459777137212e12", "tarkovstreets" },
            { "5704e3c2d2720bac5b8b4567", "woods" },
            { "5b0fc42d86f7744a585f9105", "laboratory" },
            { "653e6760052c01c1c805532f", "sandbox" },
            { "65b8d6f5cdde2479cb2a3125", "sandbox_high" },
            { "6733700029c367a3d40b02af", "labyrinth" },
        };

        // Кэш: zoneId → позиция (заполняется один раз при старте рейда)
        private static Dictionary<string, Vector3> _zonePositions = null;

        // Кэш квестов для текущей карты
        private static List<QuestData> _cachedCandidates = null;
        private static List<QuestData> _cachedKillCandidates = null;
        private static string _cachedLocationId = null;

        // -----------------------------------------------------------------------

        /// <summary>
        /// Вызывается при старте рейда — сбрасывает кэш.
        /// </summary>
        public static void Reset()
        {
            _zonePositions = null;
            _cachedCandidates = null;
            _cachedKillCandidates = null;
            _cachedLocationId = null;
        }

        /// <summary>
        /// Возвращает всех кандидатов EFT-квестов для текущей карты.
        /// Результат кэшируется на весь рейд.
        /// </summary>
        public static List<QuestData> GetCandidates()
        {
            try
            {
                var gw = Singleton<GameWorld>.Instance;
                if (gw == null) return new List<QuestData>();

                string locationId = gw.LocationId;
                if (string.IsNullOrEmpty(locationId)) return new List<QuestData>();

                // Возвращаем кэш если карта не изменилась
                if (_cachedCandidates != null && _cachedLocationId == locationId)
                    return _cachedCandidates;

                // Строим кэш позиций зон один раз
                if (_zonePositions == null)
                    _zonePositions = BuildZonePositionMap();

                _cachedLocationId = locationId;
                _cachedCandidates = BuildCandidates(locationId);

                Log.LogInfo($"[EftQuestLoader] Карта {locationId}: загружено {_cachedCandidates.Count} EFT-квест точек.");
                return _cachedCandidates;
            }
            catch (Exception ex)
            {
                Log.LogError($"[EftQuestLoader] GetCandidates: {ex.Message}");
                return new List<QuestData>();
            }
        }

        /// <summary>
        /// Возвращает HuntTarget-кандидатов (квесты с условием Kills) для текущей карты.
        /// Target=Vector3.zero — заполняется в QuestSelector.
        /// </summary>
        public static List<QuestData> GetKillCandidates()
        {
            try
            {
                var gw = Singleton<GameWorld>.Instance;
                if (gw == null) return new List<QuestData>();

                string locationId = gw.LocationId;
                if (string.IsNullOrEmpty(locationId)) return new List<QuestData>();

                // Триггерим основный кэш если ещё не строился
                if (_cachedCandidates == null || _cachedLocationId != locationId)
                    GetCandidates();

                if (_cachedKillCandidates != null)
                    return _cachedKillCandidates;

                return new List<QuestData>();
            }
            catch { return new List<QuestData>(); }
        }

        // -----------------------------------------------------------------------

        /// <summary>
        /// Сканирует все TriggerWithId на сцене → строит словарь id → центр коллайдера/позиция.
        /// </summary>
        private static Dictionary<string, Vector3> BuildZonePositionMap()
        {
            var map = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);

            var triggers = UnityEngine.Object.FindObjectsOfType<TriggerWithId>();
            foreach (var t in triggers)
            {
                if (t == null || string.IsNullOrEmpty(t.Id)) continue;
                if (!map.ContainsKey(t.Id))
                    map[t.Id] = t.transform.position;
            }

            Log.LogInfo($"[EftQuestLoader] Найдено {map.Count} TriggerWithId зон на сцене.");
            return map;
        }

        /// <summary>
        /// Читает quests.json, фильтрует по locationId, резолвит zoneId → позиции.
        /// </summary>
        private static List<QuestData> BuildCandidates(string locationId)
        {
            var result     = new List<QuestData>();
            var killResult = new List<QuestData>();

            if (!File.Exists(QuestsJsonPath))
            {
                Log.LogWarning($"[EftQuestLoader] Файл не найден: {QuestsJsonPath}");
                return result;
            }

            string json;
            try { json = File.ReadAllText(QuestsJsonPath); }
            catch (Exception ex) { Log.LogError($"[EftQuestLoader] Чтение файла: {ex.Message}"); return result; }

            JObject root;
            try { root = JObject.Parse(json); }
            catch (Exception ex) { Log.LogError($"[EftQuestLoader] Парсинг JSON: {ex.Message}"); return result; }

            foreach (var questProp in root.Properties())
            {
                try
                {
                    var q = questProp.Value as JObject;
                    if (q == null) continue;

                    string questLocation = q["location"]?.Value<string>() ?? "";
                    string questName     = q["QuestName"]?.Value<string>() ?? questProp.Name;
                    int    minLevel      = q["min_level"]?.Value<int>() ?? 0;

                    if (!string.Equals(questLocation, locationId, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(questLocation, "any", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var finishConditions = q["conditions"]?["AvailableForFinish"] as JArray;
                    if (finishConditions == null) continue;

                    // Собираем все точки квеста — потом упакуем в один QuestData с Waypoints
                    var questZones = new List<Vector3>();

                    foreach (var cond in finishConditions)
                    {
                        string condType = cond["conditionType"]?.Value<string>() ?? "";

                        if (condType == "PlaceBeacon" || condType == "LeaveItemAtLocation")
                        {
                            string zoneId = cond["zoneId"]?.Value<string>();
                            if (!string.IsNullOrEmpty(zoneId)
                                && _zonePositions.TryGetValue(zoneId, out Vector3 pos))
                                questZones.Add(pos);
                        }

                        if (condType == "CounterCreator")
                        {
                            var innerConditions = cond["counter"]?["conditions"] as JArray;
                            if (innerConditions == null) continue;

                            foreach (var inner in innerConditions)
                            {
                                string innerType = inner["conditionType"]?.Value<string>() ?? "";

                                if (innerType == "InZone")
                                {
                                    var zoneIds = inner["zoneIds"] as JArray;
                                    if (zoneIds == null) continue;
                                    foreach (var zid in zoneIds)
                                    {
                                        string zoneId = zid.Value<string>();
                                        if (!string.IsNullOrEmpty(zoneId)
                                            && _zonePositions.TryGetValue(zoneId, out Vector3 pos))
                                            questZones.Add(pos);
                                    }
                                }

                                if (innerType == "VisitPlace")
                                {
                                    string triggerId = inner["target"]?.Value<string>();
                                    if (!string.IsNullOrEmpty(triggerId)
                                        && _zonePositions.TryGetValue(triggerId, out Vector3 pos))
                                        questZones.Add(pos);
                                }

                                if (innerType == "Kills")
                                {
                                    killResult.Add(new QuestData
                                    {
                                        Type        = EQuestType.HuntTarget,
                                        Target      = Vector3.zero,
                                        Description = $"Kill: {questName}",
                                        Desirability = 70f,
                                        MinLevel    = minLevel
                                    });
                                }
                            }
                        }
                    }

                    // Упаковываем точки квеста: одна точка → Target, несколько → Waypoints
                    if (questZones.Count == 1)
                    {
                        result.Add(new QuestData
                        {
                            Type        = EQuestType.EFTQuest,
                            Target      = questZones[0],
                            Description = $"EFT: {questName}",
                            Desirability = 60f,
                            MinLevel    = minLevel
                        });
                    }
                    else if (questZones.Count > 1)
                    {
                        result.Add(new QuestData
                        {
                            Type          = EQuestType.EFTQuest,
                            Target        = questZones[0],   // первая точка для скоринга
                            Waypoints     = questZones,
                            WaypointIndex = 0,
                            Description   = $"EFT: {questName} ({questZones.Count} зон)",
                            Desirability  = 65f,             // чуть выше за полный маршрут
                            MinLevel      = minLevel
                        });
                    }
                }
                catch { /* пропускаем битый квест */ }
            }

            _cachedKillCandidates = killResult;
            Log.LogInfo($"[EftQuestLoader] Kill-квестов (HuntTarget): {killResult.Count}");
            return result;
        }
    }
}
