using Comfort.Common;
using EFT;
using System.Collections.Generic;
using UnityEngine;

namespace ScavTaskMod
{
    public enum ReshalaPhase
    {
        Protect,   // >50% свиты живо — Решала прячется, охрана держит периметр
        Aggress,   // <50% свиты живо — Решала отстреливается, охрана давит
        Vengeance  // Решала убит    — вся свита охотится на убийцу
    }

    /// <summary>
    /// Статическое состояние, общее для ReshalaBossLayer и ReshalaGuardLayer.
    /// Сбрасывается при каждом новом рейде.
    /// </summary>
    public static class ReshalaShared
    {
        // ── Ссылка на самого Решалу ────────────────────────────────────
        public static BotOwner BossOwner = null;

        // ── Слои охранников ───────────────────────────────────────────
        public static readonly Dictionary<int, ReshalaGuardLayer> GuardLayers =
            new Dictionary<int, ReshalaGuardLayer>();

        // Исходное количество охранников в начале рейда
        public static int InitialGuardCount = 0;

        // ── Убийца Решалы (ProfileId) ─────────────────────────────────
        public static string KillerProfileId = null;

        // ── Текущая фаза ──────────────────────────────────────────────
        public static ReshalaPhase CurrentPhase = ReshalaPhase.Protect;

        // ── Константы ─────────────────────────────────────────────────
        public const float CONTROL_ZONE_RADIUS = 150f; // зона контроля Решалы
        public const float GUARD_PROTECT_DIST  = 10f;  // макс дистанция от босса (Protect)
        public const float GUARD_AGGRESS_DIST  = 90f;  // макс дистанция от босса (Aggress)
        public const float GUARD_VENGEANCE_GAP = 5f;   // макс отрыв от группы (Vengeance)

        // ── Запрос позиции Решалы ──────────────────────────────────────
        public static Vector3 BossPosition =>
            BossOwner != null ? BossOwner.Position : Vector3.zero;

        // ── Количество живых охранников ───────────────────────────────
        public static int GetAliveGuardCount()
        {
            int n = 0;
            foreach (var kv in GuardLayers)
                if (kv.Value?.BotOwner != null && !kv.Value.BotOwner.IsDead) n++;
            return n;
        }

        /// <summary>Порядковый индекс охранника среди живых (0, 1, 2…)</summary>
        public static int GetGuardIndex(int botId)
        {
            int idx = 0;
            foreach (var kv in GuardLayers)
            {
                if (kv.Value?.BotOwner == null || kv.Value.BotOwner.IsDead) continue;
                if (kv.Key == botId) return idx;
                idx++;
            }
            return 0;
        }

        /// <summary>Геометрический центр всей живой свиты.</summary>
        public static Vector3 GetGroupCenter()
        {
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (var kv in GuardLayers)
            {
                if (kv.Value?.BotOwner == null || kv.Value.BotOwner.IsDead) continue;
                sum += kv.Value.BotOwner.Position;
                n++;
            }
            return n > 0 ? sum / n : Vector3.zero;
        }

        // ── Пересчёт фазы ─────────────────────────────────────────────
        public static void UpdatePhase()
        {
            // Vengeance: Решала мёртв и убийца установлен
            if (BossOwner == null || BossOwner.IsDead)
            {
                if (KillerProfileId != null)
                    CurrentPhase = ReshalaPhase.Vengeance;
                return;
            }

            if (InitialGuardCount == 0) return;

            int alive = GetAliveGuardCount();
            // Строго больше 50% → Protect
            bool majorityAlive = alive * 2 > InitialGuardCount;
            CurrentPhase = majorityAlive ? ReshalaPhase.Protect : ReshalaPhase.Aggress;
        }

        // ── Известная позиция угрозы (для Aggress/Vengeance) ──────────
        /// <summary>
        /// Возвращает позицию известного врага: сначала GoalEnemy бота,
        /// затем GoalEnemy любого члена группы, затем живого игрока в зоне контроля.
        /// </summary>
        public static bool TryGetThreatPosition(BotOwner forBot, out Vector3 pos)
        {
            pos = Vector3.zero;

            // 1. GoalEnemy самого бота
            var goal = forBot?.Memory?.GoalEnemy;
            if (goal != null)
            {
                pos = goal.CurrPosition;
                return true;
            }

            // 2. GoalEnemy любого члена BotsGroup
            var group = forBot?.BotsGroup;
            if (group != null)
            {
                foreach (var member in group.Members)
                {
                    if (member == null || member.IsDead || member == forBot) continue;
                    var g = member.Memory?.GoalEnemy;
                    if (g != null) { pos = g.CurrPosition; return true; }
                }
            }

            // 3. Живой игрок (не AI) в зоне контроля Решалы
            if (BossOwner != null)
            {
                var gw = Singleton<GameWorld>.Instance;
                if (gw != null)
                {
                    foreach (var player in gw.AllAlivePlayersList)
                    {
                        if (player == null || !player.HealthController.IsAlive) continue;
                        if (player.IsAI) continue;
                        if (Vector3.Distance(BossPosition, player.Position) < CONTROL_ZONE_RADIUS)
                        {
                            pos = player.Position;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        // ── Позиция убийцы (для Vengeance) ────────────────────────────
        public static bool TryGetKillerPosition(out Vector3 pos)
        {
            pos = Vector3.zero;
            if (KillerProfileId == null) return false;

            var gw = Singleton<GameWorld>.Instance;
            if (gw == null) return false;

            foreach (var player in gw.AllAlivePlayersList)
            {
                if (player == null || !player.HealthController.IsAlive) continue;
                if (player.ProfileId == KillerProfileId)
                {
                    pos = player.Position;
                    return true;
                }
            }
            return false;
        }

        // ── Сброс при новом рейде ──────────────────────────────────────
        public static void Reset()
        {
            BossOwner         = null;
            InitialGuardCount = 0;
            KillerProfileId   = null;
            CurrentPhase      = ReshalaPhase.Protect;
            GuardLayers.Clear();
        }
    }
}
