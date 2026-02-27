using DrakiaXYZ.BigBrain.Brains;
using EFT;
using SAIN;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace BotQuests
{
    /// <summary>
    /// BigBrain-слой для свиты босса. Приоритет 24 — ниже SAIN боевых слоёв (26+).
    ///
    /// Фаза 2 (Boss alive, < 50% свиты)   → BossAggressiveLogic  (преследование угрозы)
    /// BountyHunt     (Boss dead)          → BountyHuntLogic      (охота на убийцу)
    ///
    /// В Фазе 1 слой неактивен — свиту позиционирует OrderFollowersToGuard() из BossHoldLogic.
    /// </summary>
    public class FollowerAILayer : CustomLayer
    {
        // Доступ к слою из логик по имени бота
        public static readonly Dictionary<string, FollowerAILayer> LayerMap =
            new Dictionary<string, FollowerAILayer>();

        private BossSquadTracker _tracker;
        private BossAIState      _lastLoggedState = (BossAIState)(-1);

        private float _lastCombatTime    = -999f;
        private float _nextActionTime    = 0f;
        private bool  _objectiveComplete = false;

        public FollowerAILayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
            LayerMap[botOwner.name] = this;

            Plugin.ActiveBots.Add(new BotDebugInfo { BotName = botOwner.name, Bot = botOwner, QuestType = "Follower" });

            botOwner.GetPlayer.HealthController.DiedEvent += _ =>
            {
                LayerMap.Remove(botOwner.name);
                Plugin.ActiveBots.RemoveAll(b => b.BotName == botOwner.name);
            };
        }

        public override string GetName() => "FollowerAILayer";

        public override bool IsActive()
        {
            try
            {
                if (BotOwner == null || BotOwner.IsDead) return false;
                if (BotOwner.BotState != EBotState.Active) return false;

                // Ленивая инициализация — BotsGroup может не быть готова в конструкторе
                if (_tracker == null)
                    _tracker = BossSquadTracker.GetForFollower(BotOwner);

                if (_tracker == null) return false;

                var state = _tracker.State;

                // BountyHunt: активируемся всегда (игнорируем SAIN — максимальная агрессия)
                if (state == BossAIState.BountyHunt)
                    return Time.time >= _nextActionTime;

                // Phase2: активируемся вне SAIN-боя
                if (state == BossAIState.Phase2)
                {
                    if (SAINEnableClass.IsBotInCombat((IPlayer)(object)BotOwner))
                    {
                        _lastCombatTime = Time.time;
                        return false;
                    }
                    if (Time.time - _lastCombatTime < BotQuestsConfig.CombatCooldownBoss.Value) return false;
                    if (Time.time < _nextActionTime) return false;

                    // HP < 30% — не бегать в атаку
                    try
                    {
                        float hp = BotOwner.Medecine.FirstAid.GetHpPercent(EBodyPart.Common);
                        if (hp < 0.30f) return false;
                    }
                    catch { }

                    return true;
                }

                // Phase1: не активируемся — позиционирование делает BossHoldLogic
                return false;
            }
            catch { return false; }
        }

        public override bool IsCurrentActionEnding() => _objectiveComplete;

        public void OnObjectiveComplete()
        {
            _objectiveComplete = true;
            _nextActionTime    = Time.time + BotQuestsConfig.ObjectiveCooldown.Value;
        }

        public override Action GetNextAction()
        {
            _objectiveComplete = false;

            var state = _tracker?.State ?? BossAIState.Phase1;

            if (state != _lastLoggedState)
            {
                Plugin.Log.LogInfo($"[FollowerAI] {BotOwner.name} → {state}");
                _lastLoggedState = state;
            }

            if (state == BossAIState.BountyHunt)
                return new Action(typeof(BountyHuntLogic), "BountyHunt");

            // Phase2 — тот же логик что и у босса (ищет GoalEnemy / AllAlivePlayersList)
            return new Action(typeof(BossAggressiveLogic), "FollowerAggressive");
        }

        public override void Stop()
        {
            _objectiveComplete = false;
        }
    }
}
