using BepInEx.Logging;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace BotQuests
{
    public class GoToPointLogic : CustomLogic
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("BotQuests");

        public static readonly Dictionary<string, BotQuestLayer> LayerMap =
            new Dictionary<string, BotQuestLayer>();

        private BotQuestLayer _layer;
        private BotDebugInfo _debugInfo;

        private QuestData _quest;
        private bool _hasTarget = false;

        // Анти-застревание
        private Vector3 _lastPos;
        private float _lastPosTime;
        private const float STUCK_CHECK_INTERVAL = 5f;
        private const float STUCK_DIST           = 0.5f;
        private float _startTime;

        // Переотправка пути
        private float _lastGoToPointTime = -999f;
        private Vector3 _lastGoToPointPos;
        private const float GOTO_RESEND_INTERVAL = 2f;
        private const float GOTO_DEVIATION_DIST  = 3f;

        // Принудительный сброс позы/спринта
        private float _lastPostureFixTime = -999f;
        private const float POSTURE_FIX_INTERVAL = 1f;

        // HuntTarget
        private float _lastHuntUpdateTime = -999f;
        private bool    _inHuntPatrol = false;
        private Vector3 _huntPatrolCenter;
        private Vector3 _huntPatrolTarget;
        private float   _nextHuntPatrolTime = -999f;

        // BossHunter
        private bool  _inBossSearch = false;
        private float _bossSearchStartTime;
        private float _nextSearchWanderTime = -999f;
        private Vector3 _searchWanderTarget;

        /// <summary>Текущая активная точка навигации.</summary>
        private Vector3 ActiveTarget => _quest.CurrentTarget;

        public GoToPointLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Start()
        {
            LayerMap.TryGetValue(BotOwner.name, out _layer);

            _debugInfo = Plugin.ActiveBots.Find(b => b.BotName == BotOwner.name);

            // Восстанавливаем паузу если есть, иначе выбираем новый квест
            bool resuming = false;
            if (_layer?.PausedQuest != null)
            {
                _quest            = _layer.PausedQuest;
                float prevElapsed = _layer.PausedElapsed;
                _layer.PausedQuest   = null;
                _layer.PausedElapsed = 0f;
                _hasTarget = _quest.Target != Vector3.zero;
                // Смещаем startTime назад чтобы таймаут считался включая время до паузы
                _startTime = Time.time - prevElapsed;
                resuming = true;
                Log.LogInfo($"[BotQuests] {BotOwner.name} возобновил паузу [{_quest.Description}] → ({_quest.Target.x:F0}, {_quest.Target.z:F0})");
            }
            else
            {
                _quest = QuestSelector.SelectQuest(BotOwner);
                _hasTarget = _quest != null && _quest.Target != Vector3.zero;
            }

            if (!_hasTarget)
            {
                Log.LogInfo($"[BotQuests] {BotOwner.name} — нет доступных квестов, ждёт.");
                return;
            }

            // Резервируем слот BossHunter только для нового квеста (не возобновления)
            if (!resuming && _quest.Type == EQuestType.BossHunter)
                QuestSelector.RegisterBossZoneStart(_quest.Target);

            // При возобновлении _startTime уже задан выше — не перезаписывать
            if (!resuming)
                _startTime = Time.time;

            _lastPosTime = Time.time;
            _lastPos = BotOwner.Position;
            _lastGoToPointTime = -999f;
            _lastGoToPointPos = Vector3.zero;
            _inBossSearch = false;
            _nextSearchWanderTime = -999f;
            _inHuntPatrol = false;
            _nextHuntPatrolTime = -999f;
            _huntPatrolTarget = Vector3.zero;

            // Спринт включаем один раз при старте
            BotOwner.Mover.SetTargetMoveSpeed(1f);
            BotOwner.Mover.Sprint(true);

            UpdateDebugInfo();
            if (!resuming)
                Log.LogInfo($"[BotQuests] {BotOwner.name} [{_quest.Description}] → ({_quest.Target.x:F0}, {_quest.Target.z:F0})");
        }

        public override void Stop()
        {
            if (_layer != null)
            {
                if (_hasTarget && _quest != null)
                {
                    // Квест не завершён — сохраняем на паузу (бой, лут и т.д.)
                    _layer.PausedQuest   = _quest;
                    _layer.PausedElapsed = Time.time - _startTime;
                }
                else
                {
                    // Квест завершён — чистим паузу
                    _layer.PausedQuest   = null;
                    _layer.PausedElapsed = 0f;
                }
            }
        }

        public override void Update(CustomLayer.ActionData data)
        {
            try
            {
                if (BotOwner == null || BotOwner.Mover == null) return;
                if (!_hasTarget) { CompleteObjective(wasStuck: false); return; }

                float nearDist     = BotQuestsConfig.NearTargetDist.Value;
                float timeout      = BotQuestsConfig.ObjectiveTimeout.Value;
                float distToTarget = Vector3.Distance(BotOwner.Position, ActiveTarget);
                bool  nearTarget   = distToTarget < nearDist;

                // Таймаут (на каждую точку цепочки)
                if (Time.time - _startTime > timeout)
                {
                    if (nearTarget)
                    {
                        Log.LogInfo($"[BotQuests] {BotOwner.name} таймаут рядом с целью — засчитан успех [{_quest.Description}]");
                        OnWaypointReached();
                    }
                    else
                    {
                        Log.LogWarning($"[BotQuests] {BotOwner.name} таймаут [{_quest.Description}]");
                        CompleteObjective(wasStuck: false);
                    }
                    return;
                }

                // Застревание
                if (!nearTarget && Time.time - _lastPosTime > STUCK_CHECK_INTERVAL)
                {
                    if (Vector3.Distance(BotOwner.Position, _lastPos) < STUCK_DIST)
                    {
                        Log.LogWarning($"[BotQuests] {BotOwner.name} застрял [{_quest.Description}]");
                        CompleteObjective(wasStuck: true);
                        return;
                    }
                    _lastPos = BotOwner.Position;
                    _lastPosTime = Time.time;
                }

                // BossHunter: осмотр зоны
                if (_quest.Type == EQuestType.BossHunter)
                {
                    if (!_inBossSearch && distToTarget < nearDist)
                    {
                        _inBossSearch = true;
                        _bossSearchStartTime = Time.time;
                        _nextSearchWanderTime = Time.time;
                        BotOwner.Mover.Sprint(false);
                        Log.LogInfo($"[BotQuests] {BotOwner.name} [Boss Hunter] начал осмотр зоны");
                    }

                    if (_inBossSearch)
                    {
                        if (Time.time - _bossSearchStartTime > BotQuestsConfig.BossSearchDuration.Value)
                        {
                            Log.LogInfo($"[BotQuests] {BotOwner.name} [Boss Hunter] осмотр завершён");
                            QuestSelector.RegisterBossZoneVisited(ActiveTarget);
                            CompleteObjective(wasStuck: false);
                            return;
                        }

                        bool wanderExpired = Time.time > _nextSearchWanderTime;
                        bool wanderReached = _searchWanderTarget != Vector3.zero &&
                                            Vector3.Distance(BotOwner.Position, _searchWanderTarget) < 3f;
                        if (wanderExpired || wanderReached)
                        {
                            _searchWanderTarget = GetRandomNavPoint(ActiveTarget, BotQuestsConfig.BossSearchRadius.Value);
                            _nextSearchWanderTime = Time.time + BotQuestsConfig.BossSearchWanderInterval.Value;
                            if (_searchWanderTarget != Vector3.zero)
                                BotOwner.Mover.GoToPoint(_searchWanderTarget, false, 0.5f);
                        }
                        BotOwner.Steering.LookToMovingDirection();
                        return;
                    }
                }

                // Достигли точки (не-BossHunter)
                if (_quest.Type != EQuestType.BossHunter && distToTarget < 3f)
                {
                    OnWaypointReached();
                    return;
                }

                // AirdropChaser: дошли — регистрируем посещение
                if (_quest.Type == EQuestType.AirdropChaser && distToTarget < nearDist)
                {
                    Log.LogInfo($"[BotQuests] {BotOwner.name} [Airdrop] добрался до аирдропа");
                    QuestSelector.RegisterAirdropVisited(ActiveTarget);
                    CompleteObjective(wasStuck: false);
                    return;
                }

                // HuntTarget: навигация по звукам + патруль
                if (_quest.Type == EQuestType.HuntTarget)
                {
                    bool timeToCheck  = (Time.time - _lastHuntUpdateTime) > BotQuestsConfig.HuntUpdateInterval.Value;
                    bool reachedTarget = distToTarget < nearDist;

                    if (timeToCheck || (reachedTarget && !_inHuntPatrol))
                    {
                        Vector3 shot = QuestSelector.ResolveHuntTarget(BotOwner, _quest.HuntFaction);
                        bool hasFreshShot = shot != Vector3.zero
                                         && Vector3.Distance(BotOwner.Position, shot) > 20f
                                         && Vector3.Distance(shot, ActiveTarget) > 15f;

                        if (hasFreshShot)
                        {
                            _quest.Target = shot;
                            _inHuntPatrol = false;
                            _lastGoToPointTime = -999f;
                            Log.LogInfo($"[BotQuests] {BotOwner.name} [Hunt] → ({shot.x:F0}, {shot.z:F0})");
                        }
                        else if (reachedTarget || _quest.Target == Vector3.zero)
                        {
                            _inHuntPatrol = true;
                            _huntPatrolCenter = BotOwner.Position;
                            _nextHuntPatrolTime = Time.time;
                            BotOwner.Mover.Sprint(false);
                        }
                        _lastHuntUpdateTime = Time.time;
                    }

                    if (_inHuntPatrol)
                    {
                        bool patrolExpired = Time.time > _nextHuntPatrolTime;
                        bool patrolReached = _huntPatrolTarget != Vector3.zero &&
                                            Vector3.Distance(BotOwner.Position, _huntPatrolTarget) < 3f;
                        if (patrolExpired || patrolReached)
                        {
                            _huntPatrolTarget = GetRandomNavPoint(_huntPatrolCenter, BotQuestsConfig.HuntPatrolRadius.Value);
                            _nextHuntPatrolTime = Time.time + BotQuestsConfig.HuntPatrolWander.Value;
                            if (_huntPatrolTarget != Vector3.zero)
                                BotOwner.Mover.GoToPoint(_huntPatrolTarget, false, 1f);
                        }
                        BotOwner.Steering.LookToMovingDirection();
                        return;
                    }
                }

                // Навигация к ActiveTarget
                bool timeExpired = (Time.time - _lastGoToPointTime) > GOTO_RESEND_INTERVAL;
                bool deviated    = Vector3.Distance(BotOwner.Position, _lastGoToPointPos) > GOTO_DEVIATION_DIST;
                if (timeExpired || deviated)
                {
                    BotOwner.Mover.GoToPoint(ActiveTarget, false, 1f);
                    _lastGoToPointTime = Time.time;
                    _lastGoToPointPos  = BotOwner.Position;
                }

                if (BotOwner.Mover.IsMoving)
                    BotOwner.Steering.LookToMovingDirection();

                if (Time.time - _lastPostureFixTime > POSTURE_FIX_INTERVAL)
                {
                    BotOwner.Mover.SetPose(1f);
                    BotOwner.Mover.SetTargetMoveSpeed(1f);
                    if (!BotOwner.Mover.Sprinting) BotOwner.Mover.Sprint(true);
                    _lastPostureFixTime = Time.time;
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"[BotQuests] Update: {ex.Message}");
                CompleteObjective(wasStuck: false);
            }
        }

        /// <summary>
        /// Вызывается когда бот достиг текущей точки.
        /// Если есть следующий вейпоинт — переходим к нему, иначе завершаем квест.
        /// </summary>
        private void OnWaypointReached()
        {
            if (_quest.HasNextWaypoint)
            {
                _quest.AdvanceWaypoint();
                // Сброс таймера и застревания для следующей точки
                _startTime   = Time.time;
                _lastPos     = BotOwner.Position;
                _lastPosTime = Time.time;
                _lastGoToPointTime = -999f;
                _inBossSearch = false;

                Log.LogInfo($"[BotQuests] {BotOwner.name} [{_quest.Description}] → вейпоинт {_quest.WaypointIndex}/{_quest.Waypoints.Count - 1} ({ActiveTarget.x:F0}, {ActiveTarget.z:F0})");
                UpdateDebugInfo();
            }
            else
            {
                if (_quest.Type == EQuestType.BossHunter)
                    QuestSelector.RegisterBossZoneVisited(ActiveTarget);

                Log.LogInfo($"[BotQuests] {BotOwner.name} выполнил [{_quest.Description}]");
                CompleteObjective(wasStuck: false);
            }
        }

        private void CompleteObjective(bool wasStuck)
        {
            if (_hasTarget && wasStuck && _quest != null)
            {
                QuestSelector.RegisterPointStuck(ActiveTarget);
                if (_quest.Type == EQuestType.BossHunter)
                    QuestSelector.RegisterBossZoneAborted(ActiveTarget);
            }
            _hasTarget = false;
            try { BotOwner?.Mover?.Sprint(false); } catch { }
            _layer?.OnObjectiveComplete(wasStuck);
        }

        private void UpdateDebugInfo()
        {
            if (_debugInfo == null) return;
            _debugInfo.TargetX = _quest.Target.x;
            _debugInfo.TargetZ = _quest.Target.z;
            _debugInfo.QuestType = _quest.Description;
        }

        /// <summary>
        /// Случайная точка на NavMesh в радиусе radius от center.
        /// </summary>
        private static Vector3 GetRandomNavPoint(Vector3 center, float radius)
        {
            for (int i = 0; i < 10; i++)
            {
                Vector2 rnd  = UnityEngine.Random.insideUnitCircle * radius;
                Vector3 candidate = center + new Vector3(rnd.x, 0f, rnd.y);
                if (NavMesh.SamplePosition(candidate, out var hit, 3f, 1))  // 1 = Walkable area
                    return hit.position;
            }
            return Vector3.zero;
        }
    }
}
