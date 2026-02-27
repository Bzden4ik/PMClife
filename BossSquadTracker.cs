using EFT;
using System.Collections.Generic;
using UnityEngine;

namespace BotQuests
{
    public enum BossAIState
    {
        Phase1,      // Босс жив, свита >= 50% → укрытие / эскорт
        Phase2,      // Босс жив, свита < 50%  → агрессивное преследование
        BountyHunt   // Босс мёртв, свита жива → охота на убийцу
    }

    /// <summary>
    /// Отслеживает состояние группы босса и хранит цель Bounty Hunt.
    /// Регистрируется в статическом словаре ByGroup, чтобы FollowerAILayer
    /// мог найти трекер по своей BotsGroup.
    /// </summary>
    public class BossSquadTracker
    {
        // Статический реестр: BotsGroup → трекер. Заполняется в Tick() после инициализации.
        public static readonly Dictionary<BotsGroup, BossSquadTracker> ByGroup =
            new Dictionary<BotsGroup, BossSquadTracker>();

        private readonly BotOwner _boss;
        private int   _initialFollowerCount = -1;
        private bool  _registeredToGroup    = false;
        private bool  _bossDeathProcessed   = false;

        // Последняя известная позиция врага, бившегося с боссом → цель для Bounty Hunt
        private Vector3? _bountyTargetPos;

        public BossAIState State       { get; private set; } = BossAIState.Phase1;
        public Vector3?    BountyTargetPos => _bountyTargetPos;

        public BossSquadTracker(BotOwner boss)
        {
            _boss = boss;
        }

        /// <summary>
        /// Поиск трекера для follower-бота по его BotsGroup.
        /// Возвращает null если группа ещё не зарегистрирована.
        /// </summary>
        public static BossSquadTracker GetForFollower(BotOwner follower)
        {
            if (follower?.BotsGroup == null) return null;
            ByGroup.TryGetValue(follower.BotsGroup, out var tracker);
            return tracker;
        }

        /// <summary>
        /// Вызывается из BossAILayer.IsActive() каждый тик.
        /// Обновляет состояние, следит за врагом, регистрирует группу.
        /// </summary>
        public void Tick()
        {
            try
            {
                // Поздняя регистрация — BotsGroup может отсутствовать в конструкторе
                if (!_registeredToGroup && _boss.BotsGroup != null)
                {
                    ByGroup[_boss.BotsGroup] = this;
                    _registeredToGroup = true;
                }

                if (State == BossAIState.BountyHunt) return;

                // Босс умер → переходим в Bounty Hunt
                if (_boss.IsDead)
                {
                    if (!_bossDeathProcessed)
                    {
                        _bossDeathProcessed = true;
                        State = BossAIState.BountyHunt;
                        Plugin.Log.LogInfo($"[BossAI] Босс {_boss.name} убит → BOUNTY HUNT. Цель: {(_bountyTargetPos.HasValue ? $"({_bountyTargetPos.Value.x:F0},{_bountyTargetPos.Value.z:F0})" : "неизвестна")}");
                    }
                    return;
                }

                // Запоминаем последнюю известную позицию врага (потенциальный убийца)
                var goalEnemy = _boss.Memory?.GoalEnemy;
                if (goalEnemy != null)
                    _bountyTargetPos = goalEnemy.EnemyLastPosition;
                else if (_boss.Memory?.LastEnemy != null)
                    _bountyTargetPos = _boss.Memory.LastEnemy.EnemyLastPosition;

                // Обновляем фазу по проценту живой свиты
                int current = GetCurrentFollowerCount();
                if (_initialFollowerCount < 0)
                    _initialFollowerCount = current;

                if (_initialFollowerCount == 0)
                {
                    State = BossAIState.Phase1; // Соло-босс
                    return;
                }

                float fraction = (float)current / _initialFollowerCount;
                var newState = fraction < 0.5f ? BossAIState.Phase2 : BossAIState.Phase1;
                if (newState != State)
                {
                    State = newState;
                    Plugin.Log.LogInfo($"[BossAI] {_boss.name} → {State} (свита {current}/{_initialFollowerCount})");
                }
            }
            catch { }
        }

        // Для совместимости с BossAILayer
        public bool IsPhase2() => State == BossAIState.Phase2;

        private int GetCurrentFollowerCount()
        {
            int members = _boss.BotsGroup?.MembersCount ?? 1;
            return Mathf.Max(0, members - 1);
        }

        public static void ClearAll()
        {
            ByGroup.Clear();
        }
    }
}
