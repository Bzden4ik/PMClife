using DrakiaXYZ.BigBrain.Brains;
using EFT;
using System;
using UnityEngine;
using UnityEngine.AI;

namespace ScavTaskMod
{
    // ── ActionData охранника ───────────────────────────────────────────
    public class ReshalaGuardData : CustomLayer.ActionData
    {
        public ReshalaPhase Phase;
        public Vector3      TargetPos;
        public bool         HasTarget;
        public float        ReachDist;
        public bool         ShouldWait; // Vengeance: мы опережаем группу → ждём
    }

    // ─────────────────────────────────────────────────────────────────────
    // BigBrain Layer для охраны Решалы (WildSpawnType.followerBully)
    //
    // Protect  — охранники стоят в кольце 8 м вокруг Решалы.
    //            В бою рядом с боссом → SAIN стреляет; вышли за 10 м → возвращаются.
    //
    // Aggress  — охранники давят врага: чётные (index) выдвигаются, нечётные
    //            прикрывают с позиции 25 м от Решалы в сторону угрозы.
    //            Если боец врывается в ближний бой (≤ 20 м) → SAIN перехватывает.
    //            Максимальная дистанция от Решалы: 90 м.
    //
    // Vengeance — вся свита идёт к убийце плотной группой (отрыв ≤ 5 м).
    //             Охранники, опередившие группу, останавливаются и ждут.
    // ─────────────────────────────────────────────────────────────────────
    public class ReshalaGuardLayer : CustomLayer
    {
        // Расстояния
        private const float PROTECT_REACH    = 3f;
        private const float AGGRESS_REACH    = 15f;
        private const float VENGEANCE_REACH  = 10f;
        private const float SPRINT_DIST      = 20f;

        // Роли в Aggress-фазе чередуются каждые 8 секунд
        private const float ROLE_SWAP_INTERVAL = 8f;

        public ReshalaGuardLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
            // Регистрируемся в общем реестре
            ReshalaShared.GuardLayers[botOwner.Id] = this;

            // Определяем начальное число охранников из BotsGroup
            UpdateInitialGuardCount();

            ScavTaskPlugin.Log.LogInfo(
                $"[Reshala] Guard layer created for bot {botOwner.Id}");
        }

        public override string GetName() => "ReshalaGuard";

        // ── Начальный счёт охранников ──────────────────────────────────
        private void UpdateInitialGuardCount()
        {
            if (ReshalaShared.InitialGuardCount > 0) return;

            var group = BotOwner.BotsGroup;
            if (group == null) return;

            int cnt = 0;
            foreach (var m in group.Members)
            {
                if (m == null) continue;
                var role = m.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault;
                if (role == WildSpawnType.followerBully) cnt++;
            }

            if (cnt > 0)
            {
                ReshalaShared.InitialGuardCount = cnt;
                ScavTaskPlugin.Log.LogInfo($"[Reshala] Initial guard count set to {cnt}");
            }
        }

        // ── IsActive ───────────────────────────────────────────────────
        public override bool IsActive()
        {
            if (BotOwner == null || BotOwner.IsDead) return false;

            ReshalaShared.UpdatePhase();

            switch (ReshalaShared.CurrentPhase)
            {
                case ReshalaPhase.Protect:   return IsActiveProtect();
                case ReshalaPhase.Aggress:   return IsActiveAggress();
                case ReshalaPhase.Vengeance: return IsActiveVengeance();
                default: return false;
            }
        }

        // ──────── Protect ─────────────────────────────────────────────
        private bool IsActiveProtect()
        {
            bool inCombat = IsBotInCombat();

            // Если Решала мёртв или не зарегистрирован — ничего не делаем
            if (ReshalaShared.BossOwner == null || ReshalaShared.BossOwner.IsDead)
                return false;

            float distToBoss = Vector3.Distance(BotOwner.Position, ReshalaShared.BossPosition);

            // Бой рядом с боссом — SAIN стреляет из позиции
            if (inCombat && distToBoss <= ReshalaShared.GUARD_PROTECT_DIST)
                return false;

            // Уже на месте и спокойно — SAIN держит позицию
            var formPos = GetProtectFormationPos();
            if (!inCombat && Vector3.Distance(BotOwner.Position, formPos) <= PROTECT_REACH)
                return false;

            return true; // надо вернуться в формацию
        }

        // ──────── Aggress ─────────────────────────────────────────────
        private bool IsActiveAggress()
        {
            if (ReshalaShared.BossOwner == null || ReshalaShared.BossOwner.IsDead)
                return false;

            Vector3 threat;
            bool hasThreat = ReshalaShared.TryGetThreatPosition(BotOwner, out threat);

            if (!hasThreat)
            {
                // Нет угрозы — если бот сильно отошёл от Решалы, возвращаем
                float d = Vector3.Distance(BotOwner.Position, ReshalaShared.BossPosition);
                return d > 30f;
            }

            float distToThreat = Vector3.Distance(BotOwner.Position, threat);

            // Ближний бой (≤ 20 м) — SAIN занимает оборону/атакует
            if (distToThreat <= 20f) return false;

            return true; // продолжаем выдвигаться
        }

        // ──────── Vengeance ───────────────────────────────────────────
        private bool IsActiveVengeance()
        {
            if (ReshalaShared.KillerProfileId == null) return false;

            // Если убийца живой — охотимся
            Vector3 pos;
            if (ReshalaShared.TryGetKillerPosition(out pos)) return true;

            // Убийца мёртв — ищем любую угрозу
            return ReshalaShared.TryGetThreatPosition(BotOwner, out pos);
        }

        // ── GetNextAction ──────────────────────────────────────────────
        public override CustomLayer.Action GetNextAction()
        {
            var data = BuildActionData();
            string reason = ReshalaShared.CurrentPhase.ToString();
            return new CustomLayer.Action(typeof(ReshalaGuardLogic), reason, data);
        }

        private ReshalaGuardData BuildActionData()
        {
            var d = new ReshalaGuardData { Phase = ReshalaShared.CurrentPhase };

            switch (ReshalaShared.CurrentPhase)
            {
                case ReshalaPhase.Protect:
                    d.TargetPos = GetProtectFormationPos();
                    d.HasTarget = true;
                    d.ReachDist = PROTECT_REACH;
                    break;

                case ReshalaPhase.Aggress:
                    d.HasTarget = BuildAggressTarget(ref d);
                    d.ReachDist = AGGRESS_REACH;
                    break;

                case ReshalaPhase.Vengeance:
                    d.HasTarget = BuildVengeanceTarget(ref d);
                    d.ReachDist = VENGEANCE_REACH;
                    break;
            }

            return d;
        }

        // ── IsCurrentActionEnding ─────────────────────────────────────
        public override bool IsCurrentActionEnding()
        {
            switch (ReshalaShared.CurrentPhase)
            {
                case ReshalaPhase.Protect:
                    // Возвращаемся, пока не у места
                    return IsBotInCombat()
                        || ReshalaShared.BossOwner == null
                        || ReshalaShared.BossOwner.IsDead;

                case ReshalaPhase.Aggress:
                    // Действие завершается когда меняется угроза или бот входит в ближний бой
                    return !IsActiveAggress();

                case ReshalaPhase.Vengeance:
                    // Продолжаем охоту пока есть цель
                    return !IsActiveVengeance();

                default:
                    return true;
            }
        }

        public override void Stop()
        {
            BotOwner.Mover.Stop();
            BotOwner.Mover.Sprint(false);
            base.Stop();
        }

        // ──────────────────────────────────────────────────────────────
        //  Вспомогательные методы формирования позиций
        // ──────────────────────────────────────────────────────────────

        // Protect: позиция в кольце 8 м вокруг Решалы
        private Vector3 GetProtectFormationPos()
        {
            if (ReshalaShared.BossOwner == null) return BotOwner.Position;

            int alive = Math.Max(1, ReshalaShared.GetAliveGuardCount());
            int myIdx = ReshalaShared.GetGuardIndex(BotOwner.Id);
            float angle = myIdx * (360f / alive);
            Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * Vector3.forward;
            Vector3 candidate = ReshalaShared.BossPosition + dir * 8f;

            NavMeshHit hit;
            return NavMesh.SamplePosition(candidate, out hit, 5f, NavMesh.AllAreas)
                ? hit.position : candidate;
        }

        // Aggress: чётные охранники — вперёд к угрозе,
        //          нечётные — позиция прикрытия 25 м от Решалы в сторону угрозы
        private bool BuildAggressTarget(ref ReshalaGuardData d)
        {
            Vector3 threat;
            if (!ReshalaShared.TryGetThreatPosition(BotOwner, out threat))
            {
                // Нет угрозы — вернуться к Решале
                d.TargetPos = GetProtectFormationPos();
                return true;
            }

            bool isAdvanceRole = IsAdvanceRole();
            // Если Решала ещё не зарегистрирован, используем позицию бота как базу
            Vector3 bossPos    = ReshalaShared.BossOwner != null
                ? ReshalaShared.BossPosition
                : BotOwner.Position;

            Vector3 rawDir   = threat - bossPos;
            float threatDist = rawDir.magnitude;
            Vector3 dirToThreat = threatDist > 0.01f ? rawDir / threatDist : Vector3.forward;

            Vector3 target;

            if (isAdvanceRole)
            {
                // Выдвигаемся к угрозе, но не дальше GUARD_AGGRESS_DIST от Решалы
                float advanceDist = Mathf.Min(threatDist - 20f, ReshalaShared.GUARD_AGGRESS_DIST);
                advanceDist = Mathf.Max(advanceDist, 15f); // хотя бы 15 м вперёд
                target = bossPos + dirToThreat * advanceDist;
            }
            else
            {
                // Позиция прикрытия: 25 м от Решалы в сторону угрозы
                target = bossPos + dirToThreat * Mathf.Min(25f, ReshalaShared.GUARD_AGGRESS_DIST);
            }

            NavMeshHit hit;
            d.TargetPos = NavMesh.SamplePosition(target, out hit, 10f, NavMesh.AllAreas)
                ? hit.position : target;

            return true;
        }

        // Vengeance: группой к убийце, отрыв не более GUARD_VENGEANCE_GAP
        private bool BuildVengeanceTarget(ref ReshalaGuardData d)
        {
            Vector3 target;
            bool found = ReshalaShared.TryGetKillerPosition(out target)
                      || ReshalaShared.TryGetThreatPosition(BotOwner, out target);

            if (!found) return false;

            NavMeshHit hit;
            d.TargetPos = NavMesh.SamplePosition(target, out hit, 10f, NavMesh.AllAreas)
                ? hit.position : target;

            // Проверяем отрыв от группы
            Vector3 center = ReshalaShared.GetGroupCenter();
            float myDistToTarget    = Vector3.Distance(BotOwner.Position, d.TargetPos);
            float groupDistToTarget = Vector3.Distance(center, d.TargetPos);

            // Если мы ближе к цели чем центр группы + GAP → мы опередили, ждём
            if (myDistToTarget < groupDistToTarget - ReshalaShared.GUARD_VENGEANCE_GAP)
                d.ShouldWait = true;

            return true;
        }

        // ── Роль в Aggress: чётные выдвигаются, нечётные прикрывают ──
        private bool IsAdvanceRole()
        {
            int idx = ReshalaShared.GetGuardIndex(BotOwner.Id);
            // Роли меняются раз в ROLE_SWAP_INTERVAL секунд
            bool flip = ((int)(Time.time / ROLE_SWAP_INTERVAL) % 2) == 0;
            return flip ? (idx % 2 == 0) : (idx % 2 == 1);
        }

        // ── Проверка боя ──────────────────────────────────────────────
        private bool IsBotInCombat()
        {
            try
            {
                var mem = BotOwner.Memory;
                if (mem == null) return false;
                if (mem.GoalEnemy != null && mem.GoalEnemy.IsVisible)                    return true;
                if (mem.GoalEnemy != null && Time.time - mem.GoalEnemy.TimeLastSeen < 10f) return true;
                if (mem.IsUnderFire)                                                      return true;
            }
            catch { }
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Action Logic: двигает охранника к целевой позиции
    // ─────────────────────────────────────────────────────────────────────
    public class ReshalaGuardLogic : CustomLogic
    {
        private Vector3 _lastTarget;
        private bool    _pathSet      = false;
        private float   _nextRecalc   = 0f;
        private const float PATH_INT  = 2f;
        private const float SPRINT_DIST = 20f;

        // Stuck detection
        private Vector3 _stuckPos;
        private float   _stuckCheck  = 0f;
        private int     _stuckCount  = 0;
        private const float STUCK_INTERVAL = 5f;
        private const float STUCK_MIN_MOVE = 0.8f;
        private const int   STUCK_MAX      = 3;

        public ReshalaGuardLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Start()
        {
            _pathSet    = false;
            _nextRecalc = 0f;
            _stuckCount = 0;
            _stuckCheck = Time.time + STUCK_INTERVAL;
            _stuckPos   = Vector3.zero;
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

            var d = data as ReshalaGuardData;
            if (d == null || !d.HasTarget) return;

            // Vengeance: ждём пока группа подтянется
            if (d.ShouldWait)
            {
                BotOwner.Mover.Stop();
                BotOwner.Mover.Sprint(false);
                return;
            }

            float dist = Vector3.Distance(BotOwner.Position, d.TargetPos);
            if (dist <= d.ReachDist)
            {
                BotOwner.Mover.Stop();
                return;
            }

            // Stuck detection
            if (CheckStuck(d)) return;

            NavigateTo(d.TargetPos, d.ReachDist,
                sprint: d.Phase != ReshalaPhase.Protect);
        }

        private void NavigateTo(Vector3 target, float reachDist, bool sprint)
        {
            bool moved = Vector3.Distance(target, _lastTarget) > 3f;
            if (!_pathSet || moved || Time.time >= _nextRecalc)
            {
                NavMeshHit hit;
                var navPos = NavMesh.SamplePosition(target, out hit, 10f, NavMesh.AllAreas)
                    ? hit.position : target;

                var status = BotOwner.Mover.GoToPoint(navPos, false, reachDist);
                if (status == NavMeshPathStatus.PathInvalid) return;

                _pathSet    = true;
                _lastTarget = target;
                _nextRecalc = Time.time + PATH_INT;
            }

            float dist = Vector3.Distance(BotOwner.Position, target);
            BotOwner.Mover.SetPose(1f);
            BotOwner.SetTargetMoveSpeed(1f);
            BotOwner.Steering.LookToMovingDirection();

            bool doSprint = sprint && dist > SPRINT_DIST;
            if (BotOwner.Mover.Sprinting != doSprint)
                BotOwner.Mover.Sprint(doSprint);
        }

        // Обнаружение застревания: если с путём есть, но бот почти не двигается
        private bool CheckStuck(ReshalaGuardData d)
        {
            if (Time.time < _stuckCheck) return false;
            _stuckCheck = Time.time + STUCK_INTERVAL;

            float moved = Vector3.Distance(BotOwner.Position, _stuckPos);
            _stuckPos = BotOwner.Position;

            bool hasPending = _pathSet && BotOwner.Mover.HasPathAndNoComplete;
            if (hasPending && moved < STUCK_MIN_MOVE)
            {
                _stuckCount++;
                ScavTaskPlugin.Log.LogWarning(
                    $"[Reshala] Guard {BotOwner.Id} stuck ({_stuckCount}/{STUCK_MAX})");
                if (_stuckCount >= STUCK_MAX)
                {
                    _stuckCount = 0;
                    _pathSet    = false; // принудительный пересчёт пути
                }
                return true;
            }

            _stuckCount = 0;
            return false;
        }
    }
}
