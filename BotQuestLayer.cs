using DrakiaXYZ.BigBrain.Brains;
using EFT;
using LootingBots;
using LootingBots.Components;
using SAIN;
using SAIN.SAINComponent.Classes.Memory;
using System;
using System.Reflection;
using UnityEngine;

namespace BotQuests
{
    /// <summary>
    /// Слой квест-системы. Решает КОГДА боту квеститься.
    /// Активен для всех типов ботов с приоритетом 25.
    /// </summary>
    public class BotQuestLayer : CustomLayer
    {
        // Время последнего боя — пауза 30 сек после него
        private float _lastCombatTime = -999f;

        private bool _permanentStop = false;
        private int _stuckCount = 0;
        private float _nextActionTime = 0f;

        // Квест на паузе (бот ушёл в бой или лут — восстанавливаем при возврате)
        public QuestData PausedQuest { get; set; }
        public float PausedElapsed { get; set; }

        public BotQuestLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
            GoToPointLogic.LayerMap[botOwner.name] = this;

            var debugInfo = new BotDebugInfo { BotName = botOwner.name, Bot = botOwner };
            Plugin.ActiveBots.Add(debugInfo);

            botOwner.GetPlayer.HealthController.DiedEvent += _ =>
            {
                GoToPointLogic.LayerMap.Remove(botOwner.name);
                Plugin.ActiveBots.RemoveAll(b => b.BotName == botOwner.name);
            };
        }

        public override string GetName() => "BotQuestLayer";

        public override bool IsActive()
        {
            try
            {
                if (BotOwner == null || BotOwner.Mover == null) return false;
                if (BotOwner.BotState != EBotState.Active) return false;

                // --- ПОСТОЯННЫЕ СТОПЫ ---
                if (_permanentStop) return false;
                if (_stuckCount >= BotQuestsConfig.MaxStuck.Value) { _permanentStop = true; return false; }

                // Проверяем экстракт через SAIN
                if (SAINEnableClass.GetSAIN(BotOwner.ProfileId, out var sain))
                {
                    if (sain.Memory?.Extract?.ExtractStatus != EExtractStatus.None)
                    {
                        _permanentStop = true;
                        return false;
                    }
                }

                // Инвентарь полон — больше некуда брать лут, квесты не нужны
                if (LootingBots.External.CheckIfInventoryFull(BotOwner))
                {
                    _permanentStop = true;
                    return false;
                }

                // Бот сейчас лутает — не мешаем
                var lootBrain = BotOwner.GetPlayer?.gameObject?.GetComponent<LootingBrain>();
                if (lootBrain != null && lootBrain.IsBotLooting)
                    return false;

                // HP < 30% — останавливаем квесты
                try
                {
                    float hp = BotOwner.Medecine.FirstAid.GetHpPercent(EBodyPart.Common);
                    if (hp < 0.30f) return false;
                }
                catch { }

                // --- ВРЕМЕННЫЕ ПАУЗЫ ---

                // Бой: сначала спрашиваем SAIN, потом нативный EFT fallback
                bool inCombat = IsBotInCombat();
                if (inCombat)
                {
                    _lastCombatTime = Time.time;
                    return false;
                }
                // Kill/BossHunter квесты возобновляются быстро — бой и есть их цель
                bool isCombatQuest = PausedQuest != null &&
                    (PausedQuest.Type == EQuestType.HuntTarget || PausedQuest.Type == EQuestType.BossHunter);
                float combatCooldown = isCombatQuest
                    ? BotQuestsConfig.CombatCooldownCombatQuest.Value
                    : BotQuestsConfig.CombatCooldownNormal.Value;
                if (Time.time - _lastCombatTime < combatCooldown) return false;

                // Пауза после завершения цели
                if (Time.time < _nextActionTime) return false;

                return true;
            }
            catch { return false; }
        }

        public override bool IsCurrentActionEnding()
        {
            // Logic сам сообщает о завершении через OnObjectiveComplete
            return _objectiveComplete;
        }

        private bool _objectiveComplete = false;

        public void OnObjectiveComplete(bool wasStuck)
        {
            _objectiveComplete = true;
            if (wasStuck) _stuckCount++;
            else _stuckCount = 0;
            _nextActionTime = Time.time + BotQuestsConfig.ObjectiveCooldown.Value;
        }

        public override Action GetNextAction()
        {
            _objectiveComplete = false;
            return new Action(typeof(GoToPointLogic), "GoToPoint");
        }

        public override void Stop()
        {
            _objectiveComplete = false;
        }

        // Рефлексия на SAIN.Plugin.External.CanBotQuest — инициализируется один раз на первом боте
        private static MethodInfo _canBotQuestMethod;
        private static bool _sainReflectionInited;

        private static void InitSainReflection()
        {
            if (_sainReflectionInited) return;
            _sainReflectionInited = true;
            try
            {
                var externalType = Type.GetType("SAIN.Plugin.External, SAIN");
                _canBotQuestMethod = externalType?.GetMethod("CanBotQuest",
                    BindingFlags.Public | BindingFlags.Static);
            }
            catch { }
        }

        private bool IsBotInCombat()
        {
            InitSainReflection();

            // Приоритет: SAIN.Plugin.External.CanBotQuest
            if (_canBotQuestMethod != null)
            {
                try
                {
                    var result = _canBotQuestMethod.Invoke(null, new object[] { BotOwner, BotOwner.Position, 0.33f });
                    if (result is bool canQuest)
                        return !canQuest; // false = бот в бою, значит inCombat=true
                }
                catch { }
            }

            // Fallback: нативные EFT проверки
            try
            {
                var memory = BotOwner.Memory;
                if (memory == null) return false;

                if (memory.GoalEnemy != null && memory.GoalEnemy.IsVisible)
                    return true;

                if (memory.GoalEnemy != null && Time.time - memory.GoalEnemy.TimeLastSeen < 10f)
                    return true;

                if (memory.IsUnderFire)
                    return true;
            }
            catch { }

            return false;
        }
    }
}
