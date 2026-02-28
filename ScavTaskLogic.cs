using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace ScavTaskMod
{
    public class ScavTaskLogic : CustomLogic
    {
        private const float REACH_DIST         = 10f;
        private const float PATH_RECALC_INT    = 2f;
        private const float SPRINT_MIN_DIST    = 20f;
        private const float SEARCH_DURATION    = 30f;
        private const float SEARCH_RADIUS      = 25f;
        private const float SEARCH_REACH_DIST  = 4f;

        // CheckPMCSpawn — увеличенный радиус, т.к. PMC может быть за кустом с NavMeshObstacle
        private const float PMC_REACH_DIST     = 18f;
        // Если мувер встал и мы ближе этой дистанции — засчитываем как «прибыли»
        private const float PMC_NEAR_STOP_DIST = 28f;

        private float   _nextPathRecalc  = 0f;
        private Vector3 _lastTargetPos;
        private bool    _pathSet         = false;
        private float   _lastPathRecord  = 0f;
        private const float PATH_RECORD_INT = 1f;

        // После PathInvalid блокируем повтор на 5с чтобы не спамить лог каждый кадр
        private float   _pathInvalidUntil = 0f;
        private const float PATH_INVALID_COOLDOWN = 5f;
        // Throttle предупреждений PathInvalid — не чаще раза в 10с
        private float   _lastPathInvalidLog = -999f;
        private const float PATH_INVALID_LOG_INT = 10f;

        // HuntBoss search sub-state
        private bool    _searching        = false;
        private float   _searchUntil      = 0f;
        private Vector3 _searchCenter;
        private Vector3 _currentSearchPt;
        private bool    _searchPtSet      = false;
        private float   _nextSearchPt     = 0f;

        // EFT-квест visit/find: sub-state для блуждания в зоне/у лута
        private bool    _eftWandering    = false;
        private float   _eftWanderUntil = 0f;

        // Stuck detection
        private Vector3 _stuckCheckPos;
        private float   _stuckCheckTime  = 0f;
        private const float STUCK_CHECK_INTERVAL = 5f;
        private const float STUCK_MIN_MOVE       = 0.8f;
        private int     _stuckCount      = 0;
        private const int STUCK_MAX      = 3;

        public ScavTaskLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Start()
        {
            _pathSet        = false;
            _nextPathRecalc = 0f;
            _searching      = false;
            _searchPtSet    = false;
            _eftWandering   = false;
            _stuckCount     = 0;
            _stuckCheckTime = Time.time + STUCK_CHECK_INTERVAL;
            _stuckCheckPos  = Vector3.zero;
            base.Start();
        }

        public override void Stop()
        {
            BotOwner.Mover.Stop();
            BotOwner.Mover.Sprint(false);
            base.Stop();
        }

        public override void Update(CustomLayer.ActionData data)
        {
            if (BotOwner == null || BotOwner.IsDead) return;

            var td = data as ScavTaskData;
            if (td == null || !td.HasTarget)
            {
                var fallbackLayer = GetLayer();
                if (fallbackLayer == null) { StopAndComplete(); return; }
                Vector3 fallbackTarget;
                if (fallbackLayer.CurrentTask == ScavTaskType.None ||
                    !fallbackLayer.TryFindTarget(out fallbackTarget))
                {
                    StopAndComplete();
                    return;
                }
                td = new ScavTaskData
                {
                    TaskType  = fallbackLayer.CurrentTask,
                    TargetPos = fallbackTarget,
                    HasTarget = true
                };
            }

            if (Time.time >= _lastPathRecord)
            {
                ScavTaskLayer.RecordPathPoint(BotOwner.Id, BotOwner.Position);
                _lastPathRecord = Time.time + PATH_RECORD_INT;
            }

            // Stuck detection — до switch
            if (CheckStuck()) return;

            switch (td.TaskType)
            {
                case ScavTaskType.SpawnRush:     UpdateSpawnRush(td);  break;
                case ScavTaskType.HuntBoss:      UpdateHuntBoss(td);   break;
                case ScavTaskType.CheckPMCSpawn: UpdateCheckPMC(td);   break;
                case ScavTaskType.EftVisit:      UpdateEftVisit(td);   break;
                case ScavTaskType.EftKill:       UpdateEftKill(td);    break;
                case ScavTaskType.EftFind:       UpdateEftFind(td);    break;
                default: StopAndComplete(); break;
            }
        }

        // ── SpawnRush ─────────────────────────────────────────────────
        private void UpdateSpawnRush(ScavTaskData td)
        {
            Vector3 target = td.TargetPos;

            float distToPlayer = float.MaxValue;
            var gw = Comfort.Common.Singleton<GameWorld>.Instance;
            if (gw?.MainPlayer != null && gw.MainPlayer.HealthController.IsAlive)
                distToPlayer = Vector3.Distance(BotOwner.Position, gw.MainPlayer.Position);

            if (distToPlayer < 15f)
            {
                BotOwner.Mover.Stop();
                return;
            }

            NavigateTo(target, REACH_DIST);

            if (Vector3.Distance(BotOwner.Position, target) <= REACH_DIST)
                StopAndComplete();
        }

        // ── HuntBoss ──────────────────────────────────────────────────
        private void UpdateHuntBoss(ScavTaskData td)
        {
            float distToTarget = Vector3.Distance(BotOwner.Position, td.TargetPos);

            if (!_searching)
            {
                // Фаза 1: движемся к зоне босса
                bool pathOk = NavigateTo(td.TargetPos, REACH_DIST);

                bool arrived = distToTarget <= REACH_DIST;

                // PathInvalid (нет пути), либо PathPartial — мувер встал но цель недостижима.
                // Это случай с лестницей/другим уровнем. Начинаем поиск от текущей позиции.
                bool pathBlocked = !pathOk
                    || (!BotOwner.Mover.IsMoving && !BotOwner.Mover.HasPathAndNoComplete
                        && _pathSet && distToTarget > REACH_DIST && distToTarget < 80f);

                if (arrived || pathBlocked)
                {
                    if (pathBlocked && !arrived)
                        ScavTaskPlugin.Log.LogInfo(
                            $"[ScavTaskMod] [{BotOwner.Id}] HuntBoss: path blocked at dist={distToTarget:F1}m → search from current pos");

                    _searching    = true;
                    _searchUntil  = Time.time + SEARCH_DURATION;
                    // Если путь заблокирован — ищем вокруг текущей позиции, иначе вокруг цели
                    _searchCenter = arrived ? td.TargetPos : BotOwner.Position;
                    _searchPtSet  = false;
                    BotOwner.Mover.Stop();
                    BotOwner.Mover.Sprint(false);
                }
            }
            else
            {
                // Фаза 2: патрулируем вокруг зоны 30 секунд
                if (Time.time >= _searchUntil)
                {
                    ScavTaskLayer.MarkBossAreaSearched(_searchCenter);
                    StopAndComplete();
                    return;
                }

                float distToSearchPt = _searchPtSet
                    ? Vector3.Distance(BotOwner.Position, _currentSearchPt)
                    : float.MaxValue;

                if (!_searchPtSet || distToSearchPt <= SEARCH_REACH_DIST
                    || Time.time >= _nextSearchPt)
                {
                    Vector3 pt;
                    if (TryRandomNavPoint(_searchCenter, SEARCH_RADIUS, out pt))
                    {
                        _currentSearchPt = pt;
                        _searchPtSet     = true;
                        _nextSearchPt    = Time.time + 8f;
                        _pathSet         = false;
                    }
                }

                if (_searchPtSet)
                    NavigateTo(_currentSearchPt, SEARCH_REACH_DIST, sprint: false);
            }
        }

        // ── CheckPMCSpawn ─────────────────────────────────────────────
        private void UpdateCheckPMC(ScavTaskData td)
        {
            bool pathOk = NavigateTo(td.TargetPos, PMC_REACH_DIST);

            float dist = Vector3.Distance(BotOwner.Position, td.TargetPos);

            // Достигли цели по дистанции
            if (dist <= PMC_REACH_DIST)
            {
                StopAndComplete();
                return;
            }

            // Мувер встал (путь кончился — PathPartial или бот упёрся в куст/NavMeshObstacle)
            // и мы достаточно близко — считаем засчитанным
            if (_pathSet && !BotOwner.Mover.IsMoving && !BotOwner.Mover.HasPathAndNoComplete
                && dist <= PMC_NEAR_STOP_DIST)
            {
                ScavTaskPlugin.Log.LogInfo(
                    $"[ScavTaskMod] [{BotOwner.Id}] CheckPMC: mover stopped at dist={dist:F1}m → complete");
                StopAndComplete();
                return;
            }

            // Путь не построен вообще
            if (!pathOk)
            {
                ScavTaskPlugin.Log.LogWarning(
                    $"[ScavTaskMod] [{BotOwner.Id}] CheckPMC: PathInvalid → complete");
                StopAndComplete();
            }
        }

        // ── EftVisit: прийти в зону квеста и погулять 60 секунд ──────
        private const float EFT_VISIT_DURATION = 60f;
        private const float EFT_VISIT_RADIUS   = 20f;

        private void UpdateEftVisit(ScavTaskData td)
        {
            float dist = Vector3.Distance(BotOwner.Position, td.TargetPos);

            if (!_eftWandering)
            {
                bool pathOk = NavigateTo(td.TargetPos, REACH_DIST);
                bool arrived = dist <= REACH_DIST;
                // Путь заблокирован, но мы достаточно близко — начинаем блуждание
                bool pathStuck = !pathOk
                    || (!BotOwner.Mover.IsMoving && !BotOwner.Mover.HasPathAndNoComplete
                        && _pathSet && dist > REACH_DIST && dist < 60f);

                if (arrived || pathStuck)
                {
                    _eftWandering  = true;
                    _eftWanderUntil = Time.time + EFT_VISIT_DURATION;
                    _searchCenter  = arrived ? td.TargetPos : BotOwner.Position;
                    _searchPtSet   = false;
                    BotOwner.Mover.Stop();
                    BotOwner.Mover.Sprint(false);
                    ScavTaskPlugin.Log.LogInfo(
                        $"[ScavTaskMod] [{BotOwner.Id}] EftVisit: arrived, wandering for {EFT_VISIT_DURATION}s");
                }
            }
            else
            {
                // Блуждаем в зоне отведённое время
                if (Time.time >= _eftWanderUntil)
                {
                    ScavTaskPlugin.Log.LogInfo(
                        $"[ScavTaskMod] [{BotOwner.Id}] EftVisit: zone visit complete");
                    StopAndComplete();
                    return;
                }

                float distToPt = _searchPtSet
                    ? Vector3.Distance(BotOwner.Position, _currentSearchPt)
                    : float.MaxValue;

                if (!_searchPtSet || distToPt <= SEARCH_REACH_DIST || Time.time >= _nextSearchPt)
                {
                    Vector3 pt;
                    if (TryRandomNavPoint(_searchCenter, EFT_VISIT_RADIUS, out pt))
                    {
                        _currentSearchPt = pt;
                        _searchPtSet     = true;
                        _nextSearchPt    = Time.time + 8f;
                        _pathSet         = false;
                    }
                }

                if (_searchPtSet)
                    NavigateTo(_currentSearchPt, SEARCH_REACH_DIST, sprint: false);
            }
        }

        // ── EftKill: охота на PMC или босса ───────────────────────────
        // Boss → HuntBoss (завершается по обыску зоны)
        // PMC  → навигируемся к цели, НЕ завершаем по дистанции —
        //        квест закроется только через OnTrackedPlayerDead в IsActive()
        private void UpdateEftKill(ScavTaskData td)
        {
            if (td.EftKillTarget == EftKillTargetType.Boss)
            {
                UpdateHuntBoss(td);
                return;
            }

            // PMC hunt: двигаемся к цели
            bool pathOk = NavigateTo(td.TargetPos, PMC_REACH_DIST);
            if (!pathOk) return; // нет пути — ждём пока TryFindTarget обновит цель

            // Добрались — стоп, SAIN увидит врага и вступит в бой
            float dist = Vector3.Distance(BotOwner.Position, td.TargetPos);
            if (dist <= PMC_REACH_DIST)
                BotOwner.Mover.Stop();
        }

        // ── EftFind: найти предмет — прийти и обыскать зону 30 секунд ─
        private const float EFT_FIND_DURATION = 30f;
        private const float EFT_FIND_RADIUS   = 15f;

        private void UpdateEftFind(ScavTaskData td)
        {
            float dist = Vector3.Distance(BotOwner.Position, td.TargetPos);

            if (!_eftWandering)
            {
                bool pathOk = NavigateTo(td.TargetPos, REACH_DIST);
                bool arrived = dist <= REACH_DIST;
                bool pathStuck = !pathOk
                    || (!BotOwner.Mover.IsMoving && !BotOwner.Mover.HasPathAndNoComplete
                        && _pathSet && dist > REACH_DIST && dist < 60f);

                if (arrived || pathStuck)
                {
                    _eftWandering   = true;
                    _eftWanderUntil = Time.time + EFT_FIND_DURATION;
                    _searchCenter   = arrived ? td.TargetPos : BotOwner.Position;
                    _searchPtSet    = false;
                    BotOwner.Mover.Stop();
                    BotOwner.Mover.Sprint(false);
                    ScavTaskPlugin.Log.LogInfo(
                        $"[ScavTaskMod] [{BotOwner.Id}] EftFind: at loot area, searching for {EFT_FIND_DURATION}s");
                }
            }
            else
            {
                if (Time.time >= _eftWanderUntil)
                {
                    ScavTaskPlugin.Log.LogInfo(
                        $"[ScavTaskMod] [{BotOwner.Id}] EftFind: search complete");
                    StopAndComplete();
                    return;
                }

                float distToPt = _searchPtSet
                    ? Vector3.Distance(BotOwner.Position, _currentSearchPt)
                    : float.MaxValue;

                if (!_searchPtSet || distToPt <= SEARCH_REACH_DIST || Time.time >= _nextSearchPt)
                {
                    Vector3 pt;
                    if (TryRandomNavPoint(_searchCenter, EFT_FIND_RADIUS, out pt))
                    {
                        _currentSearchPt = pt;
                        _searchPtSet     = true;
                        _nextSearchPt    = Time.time + 6f;
                        _pathSet         = false;
                    }
                }

                if (_searchPtSet)
                    NavigateTo(_currentSearchPt, SEARCH_REACH_DIST, sprint: false);
            }
        }

        // ── Навигация ─────────────────────────────────────────────────
        // Возвращает false если путь построить не удалось (PathInvalid)
        private bool NavigateTo(Vector3 target, float reachDist, bool sprint = true)
        {
            float dist       = Vector3.Distance(BotOwner.Position, target);
            bool targetMoved = Vector3.Distance(target, _lastTargetPos) > 3f;

            if (!_pathSet || targetMoved || Time.time >= _nextPathRecalc)
            {
                Vector3 navTarget = target;
                NavMeshHit snapHit;
                if (NavMesh.SamplePosition(target, out snapHit, 10f, NavMesh.AllAreas))
                    navTarget = snapHit.position;

                // Ещё в cooldown после предыдущего PathInvalid — не пытаемся
                if (Time.time < _pathInvalidUntil)
                    return false;

                var status = BotOwner.Mover.GoToPoint(navTarget, false, reachDist);
                if (status == NavMeshPathStatus.PathInvalid)
                {
                    // Throttled лог — не спамим каждый кадр
                    if (Time.time - _lastPathInvalidLog >= PATH_INVALID_LOG_INT)
                    {
                        ScavTaskPlugin.Log.LogWarning(
                            $"[ScavTaskMod] [{BotOwner.Id}] NavigateTo: PathInvalid to {navTarget}");
                        _lastPathInvalidLog = Time.time;
                    }
                    // Блокируем повтор на 5с — иначе спам каждый кадр
                    _pathInvalidUntil = Time.time + PATH_INVALID_COOLDOWN;
                    // _pathSet намеренно НЕ ставим в true
                    return false;
                }
                _pathSet          = true;
                _pathInvalidUntil = 0f;   // сбрасываем cooldown при успехе
                _lastTargetPos    = target;
                _nextPathRecalc   = Time.time + PATH_RECALC_INT;
            }

            BotOwner.Mover.SetPose(1f);
            BotOwner.SetTargetMoveSpeed(1f);
            BotOwner.Steering.LookToMovingDirection();

            bool shouldSprint = sprint && dist > SPRINT_MIN_DIST;
            if (BotOwner.Mover.Sprinting != shouldSprint)
                BotOwner.Mover.Sprint(shouldSprint);

            return true;
        }

        // ── Stuck detection ───────────────────────────────────────────
        // Возвращает true если обнаружен стак и задание завершено принудительно
        private bool CheckStuck()
        {
            if (Time.time < _stuckCheckTime) return false;
            _stuckCheckTime = Time.time + STUCK_CHECK_INTERVAL;

            float moved = Vector3.Distance(BotOwner.Position, _stuckCheckPos);
            _stuckCheckPos = BotOwner.Position;

            // Стак: путь есть, мувер «должен» двигаться, но позиция почти не изменилась
            bool hasPendingPath = _pathSet && BotOwner.Mover.HasPathAndNoComplete;
            if (hasPendingPath && moved < STUCK_MIN_MOVE)
            {
                _stuckCount++;
                ScavTaskPlugin.Log.LogWarning(
                    $"[ScavTaskMod] [{BotOwner.Id}] Stuck detected ({_stuckCount}/{STUCK_MAX}), moved={moved:F2}m");

                if (_stuckCount >= STUCK_MAX)
                {
                    ScavTaskPlugin.Log.LogWarning(
                        $"[ScavTaskMod] [{BotOwner.Id}] Stuck limit reached → force complete");
                    StopAndComplete();
                    return true;
                }

                // Принудительный пересчёт пути на следующем тике
                _pathSet = false;
            }
            else
            {
                _stuckCount = 0;
            }
            return false;
        }

        // ── Вспомогательные ──────────────────────────────────────────
        private void StopAndComplete()
        {
            BotOwner.Mover.Stop();
            BotOwner.Mover.Sprint(false);
            GetLayer()?.MarkTaskComplete();
        }

        private ScavTaskLayer GetLayer()
        {
            ScavTaskLayer layer;
            ScavTaskLayer.LayersByBotId.TryGetValue(BotOwner.Id, out layer);
            return layer;
        }

        private static bool TryRandomNavPoint(Vector3 center, float radius, out Vector3 result)
        {
            for (int i = 0; i < 8; i++)
            {
                float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float dist  = UnityEngine.Random.Range(5f, radius);
                Vector3 candidate = center + new Vector3(
                    Mathf.Cos(angle) * dist, 0f, Mathf.Sin(angle) * dist);

                NavMeshHit hit;
                if (NavMesh.SamplePosition(candidate, out hit, 5f, NavMesh.AllAreas))
                {
                    result = hit.position;
                    return true;
                }
            }
            result = center;
            return false;
        }
    }
}
