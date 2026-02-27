using Comfort.Common;
using EFT;
using System.Collections.Generic;
using UnityEngine;

namespace BotQuests
{
    /// <summary>
    /// Глобальный трекер выстрелов. Подписывается на BotEventHandler.OnSoundPlayed,
    /// хранит последние выстрелы с фракцией стрелка.
    /// Боты с HuntTarget квестом используют его для навигации к врагам.
    /// </summary>
    public static class SoundTracker
    {
        public struct ShotEvent
        {
            public Vector3      Position;
            public float        Time;
            public EHuntFaction Faction;
        }

        private static readonly List<ShotEvent> _shots = new List<ShotEvent>(64);
        private static bool _subscribed = false;

        private const float SHOT_TTL  = 120f; // хранить 2 минуты
        private const int   MAX_SHOTS = 64;

        public static bool IsInitialized => _subscribed;

        // -----------------------------------------------------------------------

        public static void Init()
        {
            if (_subscribed) return;
            var handler = Singleton<BotEventHandler>.Instance;
            if (handler == null) return;

            handler.OnSoundPlayed += OnSound;
            GameWorld.OnDispose += Reset;
            _subscribed = true;
        }

        public static void Reset()
        {
            _shots.Clear();
            // После OnDispose BotEventHandler пересоздаётся — нужно переподписаться
            _subscribed = false;
        }

        // -----------------------------------------------------------------------

        private static void OnSound(IPlayer player, Vector3 position, float power, AISoundType type)
        {
            // Только выстрелы
            if (type != AISoundType.gun && type != AISoundType.silencedGun) return;
            if (player == null) return;

            EHuntFaction faction = IsPMC(player) ? EHuntFaction.PMC : EHuntFaction.Scav;

            if (_shots.Count >= MAX_SHOTS)
                _shots.RemoveAt(0);

            _shots.Add(new ShotEvent
            {
                Position = position,
                Time     = Time.time,
                Faction  = faction
            });
        }

        // -----------------------------------------------------------------------

        /// <summary>
        /// Возвращает позицию самого свежего выстрела нужной фракции,
        /// не старше maxAge секунд. null если ничего нет.
        /// </summary>
        public static Vector3? GetMostRecentShot(EHuntFaction targetFaction, float maxAge = 90f)
        {
            PurgeOld(maxAge);

            ShotEvent? best = null;
            for (int i = _shots.Count - 1; i >= 0; i--)
            {
                var s = _shots[i];
                if (targetFaction != EHuntFaction.Any && s.Faction != targetFaction) continue;
                if (best == null || s.Time > best.Value.Time) best = s;
            }
            return best?.Position;
        }

        /// <summary>
        /// Возвращает ближайший выстрел нужной фракции к точке from.
        /// </summary>
        public static Vector3? GetNearestShot(Vector3 from, EHuntFaction targetFaction, float maxAge = 90f)
        {
            PurgeOld(maxAge);

            Vector3? nearest = null;
            float nearestDist = float.MaxValue;

            for (int i = 0; i < _shots.Count; i++)
            {
                var s = _shots[i];
                if (targetFaction != EHuntFaction.Any && s.Faction != targetFaction) continue;
                float dist = Vector3.Distance(from, s.Position);
                if (dist < nearestDist) { nearestDist = dist; nearest = s.Position; }
            }
            return nearest;
        }

        private static void PurgeOld(float maxAge)
        {
            float cutoff = Time.time - maxAge;
            for (int i = _shots.Count - 1; i >= 0; i--)
                if (_shots[i].Time < cutoff) _shots.RemoveAt(i);
        }

        // -----------------------------------------------------------------------

        private static bool IsPMC(IPlayer player)
        {
            if (player.IsYourPlayer) return true;
            try
            {
                var role = player.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault;
                return role == WildSpawnType.pmcBot
                    || role == WildSpawnType.pmcBEAR
                    || role == WildSpawnType.pmcUSEC;
            }
            catch { return false; }
        }
    }
}
