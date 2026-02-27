using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using System;
using UnityEngine;

namespace BotQuests
{
    /// <summary>
    /// Фаза 2 (Агрессивный): выйти из укрытия и преследовать последнюю известную
    /// позицию угрозы. Активируется когда свита < 50% начального состава.
    ///
    /// Порядок поиска цели:
    ///   1) BotOwner.Memory.GoalEnemy  — цель уже в памяти бота
    ///   2) Ближайший реальный игрок в радиусе THREAT_RADIUS
    /// </summary>
    public class BossAggressiveLogic : CustomLogic
    {
        // Может быть BossAILayer (для боссов) или FollowerAILayer (для свиты в Phase2)
        private BossAILayer    _bossLayer;
        private FollowerAILayer _followerLayer;
        private Vector3 _pursuitTarget;
        private bool    _hasTarget;
        private float   _startTime;
        private float   _lastGoToPointTime = -999f;

        private const float PURSUIT_TIMEOUT = 60f;  // максимальное время погони
        private const float GOTO_INTERVAL   = 2f;   // переотправка пути
        private const float REACH_DIST      = 5f;   // "достигли позиции"
        private const float THREAT_RADIUS   = 150f; // радиус поиска реальных игроков

        public BossAggressiveLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Start()
        {
            BossAILayer.LayerMap.TryGetValue(BotOwner.name, out _bossLayer);
            FollowerAILayer.LayerMap.TryGetValue(BotOwner.name, out _followerLayer);
            _startTime = Time.time;

            Vector3? threat = GetLastKnownThreatPosition();
            if (threat.HasValue)
            {
                _pursuitTarget = threat.Value;
                _hasTarget     = true;
                BotOwner.Mover.SetTargetMoveSpeed(1f);
                BotOwner.Mover.Sprint(true);
                Plugin.Log.LogInfo(
                    $"[BossAI] {BotOwner.name} Phase2: преследует " +
                    $"({_pursuitTarget.x:F0}, {_pursuitTarget.z:F0})");

                SignalFollowersRegroup();
            }
            else
            {
                _hasTarget = false;
                Plugin.Log.LogInfo($"[BossAI] {BotOwner.name} Phase2: нет угроз, ждёт");
            }
        }

        public override void Stop()
        {
            try { BotOwner?.Mover?.Sprint(false); } catch { }
        }

        public override void Update(CustomLayer.ActionData data)
        {
            try
            {
                if (!_hasTarget || Time.time - _startTime > PURSUIT_TIMEOUT)
                {
                    _bossLayer?.OnObjectiveComplete();
                    _followerLayer?.OnObjectiveComplete();
                    return;
                }

                if (Vector3.Distance(BotOwner.Position, _pursuitTarget) < REACH_DIST)
                {
                    Plugin.Log.LogInfo($"[BossAI] {BotOwner.name} Phase2: достиг позиции угрозы");
                    _bossLayer?.OnObjectiveComplete();
                    _followerLayer?.OnObjectiveComplete();
                    return;
                }

                if (Time.time - _lastGoToPointTime > GOTO_INTERVAL)
                {
                    BotOwner.Mover.GoToPoint(_pursuitTarget, false, 1f);
                    _lastGoToPointTime = Time.time;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[BossAI] BossAggressiveLogic.Update: {ex.Message}");
                _bossLayer?.OnObjectiveComplete();
                _followerLayer?.OnObjectiveComplete();
            }
        }

        // -----------------------------------------------------------------------

        /// <summary>
        /// Возвращает последнюю известную позицию угрозы.
        /// Порядок: память бота → сканирование AllPlayers в радиусе.
        /// </summary>
        private Vector3? GetLastKnownThreatPosition()
        {
            try
            {
                // 1) Память бота — SAIN или ванильный GoalEnemy
                var goalEnemy = BotOwner.Memory?.GoalEnemy;
                if (goalEnemy != null)
                    return goalEnemy.EnemyLastPosition;

                // 2) Ближайший реальный игрок в радиусе THREAT_RADIUS
                var gameWorld = Singleton<GameWorld>.Instance;
                if (gameWorld == null) return null;

                Player closestEnemy = null;
                float closestDist = THREAT_RADIUS;

                foreach (Player player in gameWorld.AllAlivePlayersList)
                {
                    if (player == null || player.IsAI) continue;
                    float dist = Vector3.Distance(BotOwner.Position, player.Position);
                    if (dist < closestDist)
                    {
                        closestDist  = dist;
                        closestEnemy = player;
                    }
                }

                return closestEnemy?.Position;
            }
            catch { return null; }
        }

        /// <summary>
        /// Перегруппировка: приказываем всем живым follower'ам двигаться к боссу.
        /// API подтверждён через декомпилятор:
        ///   BotOwner.BotsGroup.MembersCount  → int (= Members.Count)
        ///   BotOwner.BotsGroup.Member(int i) → BotOwner
        /// </summary>
        private void SignalFollowersRegroup()
        {
            try
            {
                var group = BotOwner.BotsGroup;
                if (group == null) return;

                int regrouped = 0;
                int total = group.MembersCount;

                for (int i = 0; i < total; i++)
                {
                    BotOwner follower = group.Member(i);
                    if (follower == null || follower == BotOwner || follower.IsDead) continue;

                    follower.Mover.GoToPoint(BotOwner.Position, false, 2f);
                    regrouped++;
                }

                Plugin.Log.LogInfo(
                    $"[BossAI] {BotOwner.name} Phase2: перегруппировка — " +
                    $"приказано {regrouped}/{total - 1} follower(ов)");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[BossAI] SignalFollowersRegroup: {ex.Message}");
            }
        }
    }
}
