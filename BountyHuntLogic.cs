using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using System;
using UnityEngine;

namespace BotQuests
{
    /// <summary>
    /// BOUNTY HUNT: Boss убит — свита охотится за его убийцей.
    /// Приоритеты поиска цели:
    ///   1) Последняя известная позиция врага босса (BountyTargetPos из трекера)
    ///   2) Ближайший живой игрок в радиусе HUNT_RADIUS
    /// Свита спринтует, двигается группой, агрессивна ко всем.
    /// </summary>
    public class BountyHuntLogic : CustomLogic
    {
        private FollowerAILayer  _layer;
        private BossSquadTracker _tracker;

        private Vector3 _targetPos;
        private bool    _hasTarget;
        private float   _startTime;
        private float   _lastGoToTime       = -999f;
        private float   _lastGroupSignalTime = -999f;
        private float   _lastScanTime        = -999f;

        private const float HUNT_TIMEOUT          = 120f; // сек до сброса цикла
        private const float GOTO_INTERVAL         = 2f;   // переотправка пути
        private const float REACH_DIST            = 10f;  // «достигли точки»
        private const float GROUP_SIGNAL_INTERVAL = 6f;   // как часто собирать группу
        private const float SCAN_INTERVAL         = 5f;   // как часто сканировать новую цель
        private const float HUNT_RADIUS           = 300f; // радиус поиска живых игроков

        public BountyHuntLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Start()
        {
            FollowerAILayer.LayerMap.TryGetValue(BotOwner.name, out _layer);
            _tracker  = BossSquadTracker.GetForFollower(BotOwner);
            _startTime = Time.time;

            BotOwner.Mover.Sprint(true);
            BotOwner.Mover.SetTargetMoveSpeed(1f);

            UpdateTarget();

            Plugin.Log.LogInfo($"[BountyHunt] {BotOwner.name} начал охоту. Цель: {(_hasTarget ? $"({_targetPos.x:F0},{_targetPos.z:F0})" : "неизвестна")}");
        }

        public override void Stop()
        {
            try { BotOwner?.Mover?.Sprint(false); } catch { }
        }

        public override void Update(CustomLayer.ActionData data)
        {
            try
            {
                if (Time.time - _startTime > HUNT_TIMEOUT)
                {
                    _layer?.OnObjectiveComplete();
                    return;
                }

                // Периодически обновляем цель (ищем живого игрока)
                if (Time.time - _lastScanTime > SCAN_INTERVAL)
                {
                    UpdateTarget();
                    _lastScanTime = Time.time;
                }

                if (!_hasTarget)
                {
                    _layer?.OnObjectiveComplete();
                    return;
                }

                // Сигнализируем группе сходиться на цель
                if (Time.time - _lastGroupSignalTime > GROUP_SIGNAL_INTERVAL)
                {
                    SignalGroup();
                    _lastGroupSignalTime = Time.time;
                }

                // Достигли точки — ищем следующую
                if (Vector3.Distance(BotOwner.Position, _targetPos) < REACH_DIST)
                {
                    _hasTarget = false;
                    return;
                }

                if (Time.time - _lastGoToTime > GOTO_INTERVAL)
                {
                    BotOwner.Mover.GoToPoint(_targetPos, false, 1f);
                    _lastGoToTime = Time.time;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[BountyHunt] {BotOwner.name}: {ex.Message}");
                _layer?.OnObjectiveComplete();
            }
        }

        // -----------------------------------------------------------------------

        private void UpdateTarget()
        {
            // 1) Ближайший живой игрок
            Vector3? nearest = FindNearestHumanPlayer(HUNT_RADIUS);
            if (nearest.HasValue) { _targetPos = nearest.Value; _hasTarget = true; return; }

            // 2) Запомненная позиция убийцы босса (даже если его уже нет рядом)
            if (_tracker?.BountyTargetPos.HasValue == true)
            {
                _targetPos = _tracker.BountyTargetPos.Value;
                _hasTarget = true;
                return;
            }

            _hasTarget = false;
        }

        private Vector3? FindNearestHumanPlayer(float radius)
        {
            try
            {
                var gw = Singleton<GameWorld>.Instance;
                if (gw == null) return null;

                Player closest = null;
                float  closestDist = radius;

                foreach (Player p in gw.AllAlivePlayersList)
                {
                    if (p == null || p.IsAI) continue;
                    float d = Vector3.Distance(BotOwner.Position, p.Position);
                    if (d < closestDist) { closestDist = d; closest = p; }
                }

                return closest?.Position;
            }
            catch { return null; }
        }

        /// <summary>
        /// Сигнализируем всем живым членам группы идти к нашей цели.
        /// </summary>
        private void SignalGroup()
        {
            try
            {
                var group = BotOwner.BotsGroup;
                if (group == null || !_hasTarget) return;

                for (int i = 0; i < group.MembersCount; i++)
                {
                    BotOwner m = group.Member(i);
                    if (m == null || m == BotOwner || m.IsDead) continue;
                    m.Mover.GoToPoint(_targetPos, false, 1f);
                }
            }
            catch { }
        }
    }
}
