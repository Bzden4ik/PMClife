using DrakiaXYZ.BigBrain.Brains;
using EFT;
using SAIN;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace BotQuests
{
    /// <summary>
    /// BigBrain-слой для боссов. Приоритет 24 — ниже SAIN боевых слоёв (26+),
    /// поэтому управляет позиционированием только вне активного боя.
    ///
    /// Фаза 1 (Осторожный) → BossHoldLogic   : найти укрытие и держать позицию
    /// Фаза 2 (Агрессивный) → BossAggressiveLogic : преследовать угрозу
    /// </summary>
    public class BossAILayer : CustomLayer
    {
        // Доступ к слою из логик по имени бота
        public static readonly Dictionary<string, BossAILayer> LayerMap =
            new Dictionary<string, BossAILayer>();

        private readonly BossSquadTracker _squadTracker;

        private float _lastCombatTime = -999f;
        private bool  _permanentStop  = false;
        private float _nextActionTime = 0f;
        private bool  _objectiveComplete = false;

        /// <summary>True когда нужно перейти в Фазу 2.</summary>
        public bool IsPhase2 => _squadTracker.IsPhase2();

        public BossAILayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
            _squadTracker = new BossSquadTracker(botOwner);
            LayerMap[botOwner.name] = this;

            Plugin.ActiveBots.Add(new BotDebugInfo { BotName = botOwner.name, Bot = botOwner, QuestType = "Boss" });

            botOwner.GetPlayer.HealthController.DiedEvent += _ =>
            {
                LayerMap.Remove(botOwner.name);
                Plugin.ActiveBots.RemoveAll(b => b.BotName == botOwner.name);
            };
        }

        public override string GetName() => "BossAILayer";

        public override bool IsActive()
        {
            try
            {
                if (BotOwner == null || BotOwner.Mover == null) return false;
                if (BotOwner.BotState != EBotState.Active) return false;
                if (_permanentStop) return false;

                // Обновляем состояние трекера каждый тик (враг, фаза, смерть босса)
                _squadTracker.Tick();

                // Если босс мёртв — наш слой больше не нужен, FollowerAILayer подхватит свиту
                if (BotOwner.IsDead) return false;

                // SAIN (26+) берёт управление во время боя — мы только позиционируем
                bool inCombat = SAINEnableClass.IsBotInCombat((IPlayer)(object)BotOwner);
                if (inCombat)
                {
                    _lastCombatTime = Time.time;
                    return false;
                }

                if (Time.time - _lastCombatTime < BotQuestsConfig.CombatCooldownBoss.Value) return false;
                if (Time.time < _nextActionTime) return false;

                // HP < 30% — держать позицию, но не бегать
                try
                {
                    float hp = BotOwner.Medecine.FirstAid.GetHpPercent(EBodyPart.Common);
                    if (hp < 0.30f) return false;
                }
                catch { }

                return true;
            }
            catch { return false; }
        }

        public override bool IsCurrentActionEnding() => _objectiveComplete;

        /// <summary>
        /// Вызывается из BossHoldLogic / BossAggressiveLogic по завершении цикла.
        /// </summary>
        public void OnObjectiveComplete()
        {
            _objectiveComplete = true;
            _nextActionTime = Time.time + BotQuestsConfig.ObjectiveCooldown.Value;
        }

        public override Action GetNextAction()
        {
            _objectiveComplete = false;

            if (IsPhase2)
            {
                Plugin.Log.LogInfo($"[BossAI] {BotOwner.name} → Фаза 2 (Агрессивный)");
                return new Action(typeof(BossAggressiveLogic), "BossAggressive");
            }
            else
            {
                Plugin.Log.LogInfo($"[BossAI] {BotOwner.name} → Фаза 1 (Осторожный)");
                return new Action(typeof(BossHoldLogic), "BossHold");
            }
        }

        public override void Stop()
        {
            _objectiveComplete = false;
        }
    }
}
