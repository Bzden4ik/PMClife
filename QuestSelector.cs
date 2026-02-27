using Comfort.Common;
using EFT;
using EFT.Game.Spawning;
using EFT.Interactive;
using EFT.SynchronizableObjects;
using System.Collections.Generic;
using UnityEngine;

namespace BotQuests
{
    public enum EQuestType
    {
        SpawnRush,
        BossHunter,
        SpawnWander,
        EFTQuest,
        HuntTarget,
        AirdropChaser,
    }

    public enum EHuntFaction { PMC, Scav, Any }

    public class QuestData
    {
        public EQuestType   Type;
        public Vector3      Target;
        public string       Description;
        public float        Desirability;
        public int          MinLevel;
        public EHuntFaction HuntFaction = EHuntFaction.Any;

        // Мульти-точечный маршрут (null — одиночная цель)
        public List<Vector3> Waypoints;
        public int           WaypointIndex;

        /// <summary>Активная точка навигации (текущий вейпоинт или Target).</summary>
        public Vector3 CurrentTarget =>
            Waypoints != null && WaypointIndex < Waypoints.Count
                ? Waypoints[WaypointIndex]
                : Target;

        /// <summary>Есть ли ещё точки после текущей.</summary>
        public bool HasNextWaypoint =>
            Waypoints != null && WaypointIndex + 1 < Waypoints.Count;

        /// <summary>Переход к следующей точке. Возвращает true если перешли, false если маршрут завершён.</summary>
        public bool AdvanceWaypoint()
        {
            if (!HasNextWaypoint) return false;
            WaypointIndex++;
            return true;
        }
    }

    public static class QuestSelector
    {
        private static int   _spawnRushCount = 0;
        private static float _raidStartTime  = -1f;
        private static Vector3? _playerSpawnPos = null;
        private static readonly HashSet<string> _spawnRushBots = new HashSet<string>();

        // Кэши объектов сцены
        private static BotZone[]           _cachedBotZones    = null;
        private static ExfiltrationPoint[] _cachedExfils      = null;
        private static List<Vector3>       _cachedSpawnPoints = null;

        // Дальний экстракт для каждого бота
        private static readonly Dictionary<string, Vector3> _botExfilTargets =
            new Dictionary<string, Vector3>();
        private const float EXFIL_REFRESH_DIST = 50f;

        // Кулдаун точки после застревания
        private static readonly Dictionary<string, float> _pointStuckCooldowns =
            new Dictionary<string, float>();

        // Кулдауны и счётчики BossHunter
        private static readonly Dictionary<Vector3, float> _bossZoneCooldowns =
            new Dictionary<Vector3, float>();
        private static readonly Dictionary<Vector3, int> _bossZoneActiveBots =
            new Dictionary<Vector3, int>();

        // Аирдроп: кэш позиций + посещённые
        private static readonly List<Vector3>  _cachedAirdropPositions = new List<Vector3>();
        private static readonly HashSet<string> _visitedAirdrops       = new HashSet<string>();
        private static float _lastAirdropScanTime = -999f;

        // -----------------------------------------------------------------------

        public static Vector3? GetBotExfilTarget(BotOwner bot)
        {
            bool needsNew = !_botExfilTargets.TryGetValue(bot.name, out Vector3 current);
            if (!needsNew && Vector3.Distance(bot.Position, current) <= EXFIL_REFRESH_DIST)
                needsNew = true;
            if (needsNew)
            {
                Vector3? newExfil = FindFurthestExtract(bot);
                if (newExfil.HasValue) { _botExfilTargets[bot.name] = newExfil.Value; return newExfil.Value; }
                return _botExfilTargets.TryGetValue(bot.name, out Vector3 old) ? old : (Vector3?)null;
            }
            return current;
        }

        private static Vector3? FindFurthestExtract(BotOwner bot)
        {
            try
            {
                if (_cachedExfils == null)
                    _cachedExfils = Object.FindObjectsOfType<ExfiltrationPoint>();
                if (_cachedExfils == null || _cachedExfils.Length == 0) return null;

                ExfiltrationPoint furthest = null;
                float maxDist = -1f;
                foreach (var exfil in _cachedExfils)
                {
                    if (exfil == null) continue;
                    float d = Vector3.Distance(bot.Position, exfil.transform.position);
                    if (d > maxDist) { maxDist = d; furthest = exfil; }
                }
                return furthest != null ? furthest.transform.position : (Vector3?)null;
            }
            catch { return null; }
        }

        // -----------------------------------------------------------------------

        public static QuestData SelectQuest(BotOwner bot)
        {
            if (_raidStartTime < 0)
            {
                _raidStartTime = Time.time;
                var gw = Singleton<GameWorld>.Instance;
                if (gw?.MainPlayer != null) _playerSpawnPos = gw.MainPlayer.Position;
            }

            float timeSinceStart = Time.time - _raidStartTime;

            // SpawnRush — жёсткий приоритет, каждый бот может взять только один раз
            if (timeSinceStart < BotQuestsConfig.SpawnRushWindow.Value
                && _spawnRushCount < BotQuestsConfig.MaxSpawnRush.Value
                && !_spawnRushBots.Contains(bot.name))
            {
                var rushTarget = TryGetSpawnRushTarget(bot);
                if (rushTarget.HasValue)
                {
                    _spawnRushCount++;
                    _spawnRushBots.Add(bot.name);
                    return new QuestData { Type = EQuestType.SpawnRush, Target = rushTarget.Value, Description = "Spawn Rush" };
                }
            }

            var candidates = new List<QuestData>();
            int botLevel   = GetBotLevel(bot);

            if (BotQuestsConfig.EnableEftQuests.Value)
            {
                var allEft = EftQuestLoader.GetCandidates();
                for (int i = 0; i < allEft.Count; i++)
                    if (allEft[i].MinLevel <= botLevel) candidates.Add(allEft[i]);
            }

            if (BotQuestsConfig.EnableBossHunter.Value && timeSinceStart < 300f)
                GetBossHunterCandidates(bot, candidates);

            if (BotQuestsConfig.EnableSpawnWander.Value)
                GetSpawnWanderCandidates(bot, candidates);

            if (BotQuestsConfig.EnableHuntTarget.Value)
                GetHuntTargetCandidates(bot, candidates);

            if (BotQuestsConfig.EnableAirdropChaser.Value)
                GetAirdropCandidates(bot, candidates);

            candidates.RemoveAll(c => IsPointOnStuckCooldown(c.CurrentTarget));

            if (candidates.Count == 0) return null;
            return ScoreAndSelect(bot, candidates);
        }

        // -----------------------------------------------------------------------

        private static QuestData ScoreAndSelect(BotOwner bot, List<QuestData> candidates)
        {
            Vector3? exfilPos = GetBotExfilTarget(bot);

            float distRnd  = BotQuestsConfig.DistanceRandomness.Value;
            float desirRnd = BotQuestsConfig.DesirabilityRandomness.Value;

            var distScores  = new float[candidates.Count];
            float maxDist = 0f;
            for (int i = 0; i < candidates.Count; i++)
            {
                float raw = Vector3.Distance(bot.Position, candidates[i].CurrentTarget);
                float rnd = 1f + Random.Range(-distRnd, distRnd);
                distScores[i] = raw * rnd;
                if (distScores[i] > maxDist) maxDist = distScores[i];
            }
            if (maxDist > 0f)
                for (int i = 0; i < distScores.Length; i++)
                    distScores[i] = (distScores[i] / maxDist) * BotQuestsConfig.DistanceWeighting.Value;

            var desirScores = new float[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
            {
                float rnd = 1f + Random.Range(-desirRnd, desirRnd);
                desirScores[i] = (candidates[i].Desirability * rnd / 100f) * BotQuestsConfig.DesirabilityWeighting.Value;
            }

            var exfilScores = new float[candidates.Count];
            if (exfilPos.HasValue)
            {
                Vector3 toExfil = exfilPos.Value - bot.Position; toExfil.y = 0f;
                float threshold = BotQuestsConfig.ExfilAngleThreshold.Value;
                for (int i = 0; i < candidates.Count; i++)
                {
                    Vector3 toTarget = candidates[i].CurrentTarget - bot.Position; toTarget.y = 0f;
                    float angle = Vector3.Angle(toExfil, toTarget);
                    exfilScores[i] = angle <= threshold
                        ? BotQuestsConfig.ExfilDirWeighting.Value
                        : (1f - (angle - threshold) / (180f - threshold)) * BotQuestsConfig.ExfilDirWeighting.Value;
                }
            }

            QuestData best = null;
            float bestScore = float.MinValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                float total = distScores[i] + desirScores[i] + exfilScores[i];
                if (total > bestScore) { bestScore = total; best = candidates[i]; }
            }
            return best;
        }

        // -----------------------------------------------------------------------

        private static void GetBossHunterCandidates(BotOwner bot, List<QuestData> candidates)
        {
            try
            {
                if (_cachedBotZones == null)
                    _cachedBotZones = Object.FindObjectsOfType<BotZone>();

                float now    = Time.time;
                Vector3 botPos = bot.Position;
                float maxDist  = BotQuestsConfig.BossZoneMaxDist.Value;
                float radius   = BotQuestsConfig.BossZoneRadius.Value;
                int   maxBots  = BotQuestsConfig.BossZoneMaxBots.Value;

                BotZone nearest = null;
                float nearestDist = float.MaxValue;

                foreach (var zone in _cachedBotZones)
                {
                    if (zone == null || !zone.CanSpawnBoss) continue;
                    Vector3 center = zone.CenterOfSpawnPoints;
                    float dist = Vector3.Distance(botPos, center);
                    if (dist > maxDist) continue;

                    bool onCooldown = false;
                    foreach (var kv in _bossZoneCooldowns)
                        if (now < kv.Value && Vector3.Distance(center, kv.Key) <= radius)
                        { onCooldown = true; break; }
                    if (onCooldown) continue;

                    _bossZoneActiveBots.TryGetValue(center, out int active);
                    if (active >= maxBots) continue;

                    if (dist < nearestDist) { nearestDist = dist; nearest = zone; }
                }

                if (nearest != null)
                    candidates.Add(new QuestData
                    {
                        Type        = EQuestType.BossHunter,
                        Target      = nearest.CenterOfSpawnPoints,
                        Description = "Boss Hunter",
                        Desirability = BotQuestsConfig.Desirability_BossHunter.Value
                    });
            }
            catch { }
        }

        public static void RegisterBossZoneStart(Vector3 point)
        {
            _bossZoneActiveBots.TryGetValue(point, out int count);
            _bossZoneActiveBots[point] = count + 1;
        }

        public static void RegisterBossZoneVisited(Vector3 point)
        {
            if (_bossZoneActiveBots.TryGetValue(point, out int count) && count > 0)
                _bossZoneActiveBots[point] = count - 1;
            _bossZoneCooldowns[point] = Time.time + BotQuestsConfig.BossZoneCooldown.Value;
        }

        public static void RegisterBossZoneAborted(Vector3 point)
        {
            if (_bossZoneActiveBots.TryGetValue(point, out int count) && count > 0)
                _bossZoneActiveBots[point] = count - 1;
        }

        // -----------------------------------------------------------------------

        private static void GetSpawnWanderCandidates(BotOwner bot, List<QuestData> candidates)
        {
            try
            {
                if (_cachedSpawnPoints == null)
                {
                    if (_cachedBotZones == null)
                        _cachedBotZones = Object.FindObjectsOfType<BotZone>();
                    _cachedSpawnPoints = new List<Vector3>();
                    foreach (var zone in _cachedBotZones)
                    {
                        if (zone?.SpawnPoints == null) continue;
                        foreach (var sp in zone.SpawnPoints)
                            if (sp != null) _cachedSpawnPoints.Add(sp.Position);
                    }
                }

                Vector3 botPos = bot.Position;
                Vector3 nearest = Vector3.zero;
                float nearestDist = float.MaxValue;

                foreach (var pos in _cachedSpawnPoints)
                {
                    float dist = Vector3.Distance(botPos, pos);
                    if (dist < 30f || dist > 500f) continue;
                    if (IsPointOnStuckCooldown(pos)) continue;
                    if (dist < nearestDist) { nearestDist = dist; nearest = pos; }
                }

                if (nearest != Vector3.zero)
                    candidates.Add(new QuestData
                    {
                        Type        = EQuestType.SpawnWander,
                        Target      = nearest,
                        Description = "Spawn Wander",
                        Desirability = BotQuestsConfig.Desirability_SpawnWander.Value
                    });
            }
            catch { }
        }

        // -----------------------------------------------------------------------

        private static void GetAirdropCandidates(BotOwner bot, List<QuestData> candidates)
        {
            try
            {
                float now = Time.time;
                if (now - _lastAirdropScanTime > BotQuestsConfig.AirdropScanInterval.Value)
                {
                    _lastAirdropScanTime = now;
                    _cachedAirdropPositions.Clear();

                    var allMono = Object.FindObjectsOfType<MonoBehaviour>();
                    foreach (var mb in allMono)
                    {
                        var a = mb as AirdropSynchronizableObject;
                        if (a == null || !a.IsInited) continue;
                        // Аирдроп приземлился — LootableContainer включён
                        var container = mb.GetComponentInChildren<LootableContainer>();
                        if (container == null || !container.enabled) continue;

                        string key = AirdropKey(mb.transform.position);
                        if (_visitedAirdrops.Contains(key)) continue;

                        _cachedAirdropPositions.Add(mb.transform.position);
                    }
                }

                if (_cachedAirdropPositions.Count == 0) return;

                // Ближайший непосещённый аирдроп
                Vector3 botPos = bot.Position;
                Vector3 nearest = Vector3.zero;
                float nearestDist = float.MaxValue;
                foreach (var pos in _cachedAirdropPositions)
                {
                    float dist = Vector3.Distance(botPos, pos);
                    if (dist < nearestDist) { nearestDist = dist; nearest = pos; }
                }

                if (nearest != Vector3.zero)
                    candidates.Add(new QuestData
                    {
                        Type        = EQuestType.AirdropChaser,
                        Target      = nearest,
                        Description = "Airdrop Chaser",
                        Desirability = BotQuestsConfig.Desirability_AirdropChaser.Value
                    });
            }
            catch { }
        }

        /// <summary>Вызывается когда бот добрался до аирдропа.</summary>
        public static void RegisterAirdropVisited(Vector3 pos)
        {
            _visitedAirdrops.Add(AirdropKey(pos));
            _cachedAirdropPositions.Remove(pos);
        }

        private static string AirdropKey(Vector3 v) =>
            $"{Mathf.RoundToInt(v.x)},{Mathf.RoundToInt(v.z)}";

        // -----------------------------------------------------------------------

        private static string PointKey(Vector3 v) =>
            $"{Mathf.RoundToInt(v.x)},{Mathf.RoundToInt(v.y)},{Mathf.RoundToInt(v.z)}";

        private static bool IsPointOnStuckCooldown(Vector3 point) =>
            _pointStuckCooldowns.TryGetValue(PointKey(point), out float until) && Time.time < until;

        public static void RegisterPointStuck(Vector3 point)
        {
            _pointStuckCooldowns[PointKey(point)] = Time.time + BotQuestsConfig.StuckPointCooldown.Value;
        }

        public static void ResetOnRaidStart()
        {
            _spawnRushCount = 0;
            _raidStartTime  = -1f;
            _playerSpawnPos = null;
            _spawnRushBots.Clear();
            _botExfilTargets.Clear();
            _pointStuckCooldowns.Clear();
            _bossZoneCooldowns.Clear();
            _bossZoneActiveBots.Clear();
            _cachedBotZones    = null;
            _cachedExfils      = null;
            _cachedSpawnPoints = null;
            _cachedAirdropPositions.Clear();
            _visitedAirdrops.Clear();
            _lastAirdropScanTime = -999f;
            EftQuestLoader.Reset();
            BossSquadTracker.ClearAll();
        }

        private static Vector3? TryGetSpawnRushTarget(BotOwner bot)
        {
            try
            {
                if (!_playerSpawnPos.HasValue) return null;
                float dist = Vector3.Distance(bot.Position, _playerSpawnPos.Value);
                if (dist > 200f) return null;
                return _playerSpawnPos.Value;
            }
            catch { return null; }
        }

        private static int GetBotLevel(BotOwner bot)
        {
            try { return bot.Profile?.Info?.Level ?? 0; }
            catch { return 0; }
        }

        // -----------------------------------------------------------------------
        // Распознавание боссов и фолловеров
        // -----------------------------------------------------------------------

        private static readonly HashSet<WildSpawnType> _bossTypes = new HashSet<WildSpawnType>
        {
            WildSpawnType.bossBully, WildSpawnType.bossKnight, WildSpawnType.bossGluhar,
            WildSpawnType.bossKilla, WildSpawnType.bossTagilla, WildSpawnType.bossSanitar,
            WildSpawnType.bossZryachiy, WildSpawnType.bossPartisan, WildSpawnType.bossBoar,
            WildSpawnType.bossBoarSniper, WildSpawnType.bossKolontay,
        };

        private static readonly HashSet<WildSpawnType> _followerTypes = new HashSet<WildSpawnType>
        {
            WildSpawnType.followerBully, WildSpawnType.followerGluharAssault,
            WildSpawnType.followerGluharScout, WildSpawnType.followerGluharSecurity,
            WildSpawnType.followerGluharSnipe, WildSpawnType.followerSanitar,
            WildSpawnType.followerZryachiy, WildSpawnType.followerTagilla,
            WildSpawnType.followerBigPipe, WildSpawnType.followerBirdEye,
            WildSpawnType.followerKolontayAssault, WildSpawnType.followerKolontaySecurity,
            WildSpawnType.followerBoar, WildSpawnType.followerBoarClose1, WildSpawnType.followerBoarClose2,
        };

        public static bool IsBoss(BotOwner bot)
        {
            try { return _bossTypes.Contains(bot.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault); }
            catch { return false; }
        }

        public static bool IsFollower(BotOwner bot)
        {
            try { return _followerTypes.Contains(bot.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault); }
            catch { return false; }
        }

        public static bool IsBossOrFollower(BotOwner bot) => IsBoss(bot) || IsFollower(bot);

        public static bool IsEnemyBossOrFollower(IPlayer player)
        {
            try
            {
                if (player == null) return false;
                var role = player.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault;
                return _bossTypes.Contains(role) || _followerTypes.Contains(role);
            }
            catch { return false; }
        }

        // -----------------------------------------------------------------------
        // HuntTarget
        // -----------------------------------------------------------------------

        private static EHuntFaction GetBotHuntFaction(BotOwner bot)
        {
            try
            {
                var role = bot.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault;
                bool isPMC = role == WildSpawnType.pmcBot
                          || role == WildSpawnType.pmcBEAR
                          || role == WildSpawnType.pmcUSEC;
                return isPMC ? EHuntFaction.Scav : EHuntFaction.PMC;
            }
            catch { return EHuntFaction.PMC; }
        }

        private static void GetHuntTargetCandidates(BotOwner bot, List<QuestData> candidates)
        {
            EHuntFaction faction  = GetBotHuntFaction(bot);
            int          botLevel = GetBotLevel(bot);

            var killQuests = EftQuestLoader.GetKillCandidates();
            for (int i = 0; i < killQuests.Count; i++)
            {
                if (killQuests[i].MinLevel > botLevel) continue;
                var q = killQuests[i];
                q.HuntFaction = faction;
                q.Target = ResolveHuntTarget(bot, faction);
                if (q.Target != Vector3.zero) candidates.Add(q);
            }

            Vector3 huntTarget = ResolveHuntTarget(bot, faction);
            if (huntTarget != Vector3.zero)
                candidates.Add(new QuestData
                {
                    Type        = EQuestType.HuntTarget,
                    Target      = huntTarget,
                    Description = faction == EHuntFaction.PMC ? "Hunt PMC" : "Hunt Scav",
                    Desirability = BotQuestsConfig.Desirability_HuntTarget.Value,
                    HuntFaction = faction
                });
        }

        public static Vector3 ResolveHuntTarget(BotOwner bot, EHuntFaction faction)
        {
            var shot = SoundTracker.GetNearestShot(bot.Position, faction);
            return shot ?? Vector3.zero;
        }
    }
}
