using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace ScavTaskMod
{
    // ── ActionData для Решалы ──────────────────────────────────────────
    public class ReshalaBossData : CustomLayer.ActionData
    {
        public ReshalaPhase Phase;
        public Vector3      HidePos;
        public bool         HasHidePos;
    }

    // ─────────────────────────────────────────────────────────────────────
    // BigBrain Layer для Решалы (WildSpawnType.bossBully)
    //
    // Protect phase  — навигирует Решалу к укрытию и держит его там.
    //                   Бой (SAIN) ведётся из этой точки.
    // Aggress phase  — уступает SAIN (return false); SAIN агрессивно стреляет.
    // Vengeance      — Решала мёртв, слой не активируется.
    // ─────────────────────────────────────────────────────────────────────
    public class ReshalaBossLayer : CustomLayer
    {
        // Позиция укрытия
        private Vector3 _hidePos;
        private bool    _hasHidePos      = false;
        private float   _hidePosRefresh  = 0f;
        private const float HIDE_REFRESH = 20f;   // обновляем каждые 20с
        private const float HIDE_SEARCH_RADIUS = 20f;
        private const float REACH_DIST   = 5f;

        public ReshalaBossLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
            // Регистрируем Решалу в общем состоянии
            ReshalaShared.BossOwner = botOwner;

            // Подписываемся на смерть — записываем убийцу
            var player = botOwner.GetPlayer;
            if (player != null)
                player.OnPlayerDead += OnBossDead;

            ScavTaskPlugin.Log.LogInfo(
                $"[Reshala] Boss layer created for bot {botOwner.Id}");
        }

        public override string GetName() => "ReshalaBoss";

        // ── Смерть Решалы ──────────────────────────────────────────────
        private void OnBossDead(Player player, IPlayer lastAggressor,
            DamageInfoStruct dmg, EBodyPart part)
        {
            ReshalaShared.KillerProfileId = lastAggressor?.ProfileId;
            ReshalaShared.CurrentPhase    = ReshalaPhase.Vengeance;

            ScavTaskPlugin.Log.LogInfo(
                $"[Reshala] Boss killed by '{ReshalaShared.KillerProfileId}' → Vengeance");

            if (player != null)
                player.OnPlayerDead -= OnBossDead;
        }

        // ── IsActive ───────────────────────────────────────────────────
        public override bool IsActive()
        {
            if (BotOwner == null || BotOwner.IsDead) return false;

            ReshalaShared.UpdatePhase();

            // В фазе Vengeance Решала уже мёртв — слой не нужен
            if (ReshalaShared.CurrentPhase == ReshalaPhase.Vengeance) return false;

            // В фазе Aggress — SAIN полностью управляет Решалой (агрессивный огонь из текущей позиции)
            if (ReshalaShared.CurrentPhase == ReshalaPhase.Aggress) return false;

            // Protect phase: если Решала в бою — SAIN стреляет, мы не мешаем
            if (IsBotInCombat()) return false;

            // Уже в укрытии — BigBrain не нужен, SAIN держит позицию
            if (_hasHidePos && Vector3.Distance(BotOwner.Position, _hidePos) <= REACH_DIST)
                return false;

            // Нужно найти / дойти до укрытия
            if (!_hasHidePos || Time.time > _hidePosRefresh)
                FindHidePosition();

            return _hasHidePos;
        }

        // ── GetNextAction ──────────────────────────────────────────────
        public override CustomLayer.Action GetNextAction()
        {
            var data = new ReshalaBossData
            {
                Phase      = ReshalaShared.CurrentPhase,
                HidePos    = _hidePos,
                HasHidePos = _hasHidePos
            };
            return new CustomLayer.Action(typeof(ReshalaBossLogic), "HideInCover", data);
        }

        // ── IsCurrentActionEnding ─────────────────────────────────────
        public override bool IsCurrentActionEnding()
        {
            if (IsBotInCombat()) return true;
            if (!_hasHidePos)    return true;
            return Vector3.Distance(BotOwner.Position, _hidePos) <= REACH_DIST;
        }

        public override void Stop()
        {
            BotOwner.Mover.Stop();
            BotOwner.Mover.Sprint(false);
            base.Stop();
        }

        // ── Поиск позиции укрытия ──────────────────────────────────────
        // Простая эвристика: случайная NavMesh-точка рядом с текущей позицией.
        // SAIN сам найдёт лучшее укрытие в бою; нам важно лишь «закрепить» Решалу
        // в разумной исходной точке.
        private void FindHidePosition()
        {
            for (int i = 0; i < 10; i++)
            {
                var dir = UnityEngine.Random.insideUnitSphere;
                dir.y = 0f;
                var candidate = BotOwner.Position
                    + dir.normalized * UnityEngine.Random.Range(5f, HIDE_SEARCH_RADIUS);

                NavMeshHit hit;
                if (NavMesh.SamplePosition(candidate, out hit, 5f, NavMesh.AllAreas))
                {
                    _hidePos       = hit.position;
                    _hasHidePos    = true;
                    _hidePosRefresh = Time.time + HIDE_REFRESH;
                    return;
                }
            }
            _hasHidePos = false;
        }

        // ── Проверка боя (нативный EFT fallback) ──────────────────────
        private bool IsBotInCombat()
        {
            try
            {
                var mem = BotOwner.Memory;
                if (mem == null) return false;
                if (mem.GoalEnemy != null && mem.GoalEnemy.IsVisible)                   return true;
                if (mem.GoalEnemy != null && Time.time - mem.GoalEnemy.TimeLastSeen < 10f) return true;
                if (mem.IsUnderFire)                                                     return true;
            }
            catch { }
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Action Logic: двигает Решалу к позиции укрытия
    // ─────────────────────────────────────────────────────────────────────
    public class ReshalaBossLogic : CustomLogic
    {
        private Vector3 _lastTarget;
        private bool    _pathSet       = false;
        private float   _nextRecalc    = 0f;
        private const float PATH_INT   = 2f;
        private const float REACH_DIST = 5f;
        private const float SPRINT_DIST = 25f;

        public ReshalaBossLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Start()
        {
            _pathSet    = false;
            _nextRecalc = 0f;
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

            var d = data as ReshalaBossData;
            if (d == null || !d.HasHidePos) return;

            float dist = Vector3.Distance(BotOwner.Position, d.HidePos);
            if (dist <= REACH_DIST)
            {
                BotOwner.Mover.Stop();
                BotOwner.Mover.Sprint(false);
                return;
            }

            NavigateTo(d.HidePos);
        }

        private void NavigateTo(Vector3 target)
        {
            bool moved = Vector3.Distance(target, _lastTarget) > 3f;
            if (!_pathSet || moved || Time.time >= _nextRecalc)
            {
                NavMeshHit hit;
                var navPos = NavMesh.SamplePosition(target, out hit, 10f, NavMesh.AllAreas)
                    ? hit.position : target;

                var status = BotOwner.Mover.GoToPoint(navPos, false, REACH_DIST);
                if (status == NavMeshPathStatus.PathInvalid) return;

                _pathSet    = true;
                _lastTarget = target;
                _nextRecalc = Time.time + PATH_INT;
            }

            float dist = Vector3.Distance(BotOwner.Position, target);
            BotOwner.Mover.SetPose(1f);
            BotOwner.SetTargetMoveSpeed(1f);
            BotOwner.Steering.LookToMovingDirection();

            bool sprint = dist > SPRINT_DIST;
            if (BotOwner.Mover.Sprinting != sprint)
                BotOwner.Mover.Sprint(sprint);
        }
    }
}
