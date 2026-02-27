using DrakiaXYZ.BigBrain.Brains;
using EFT;
using System;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

namespace BotQuests
{
    /// <summary>
    /// Фаза 1 (Осторожный): найти укрытие в радиусе 30–40м, дойти и удерживать позицию.
    /// Свита получает периодические команды держаться рядом с боссом (эскорт).
    /// SAIN продолжает управлять боевым поведением (стрельба, реакция на врагов).
    /// </summary>
    public class BossHoldLogic : CustomLogic
    {
        private BossAILayer _layer;
        private Vector3 _coverTarget;
        private bool    _hasTarget;
        private float   _startTime;
        private float   _lastGoToPointTime = -999f;
        private float   _lastEscortTime    = -999f;

        private const float HOLD_TIMEOUT      = 120f; // максимальное время всей фазы
        private const float GOTO_INTERVAL     = 3f;   // переотправка пути к укрытию
        private const float REACH_DIST        = 3f;   // "дошли до укрытия"
        private const float HOLD_RESEND       = 6f;   // переотправка когда стоим (антидрейф)
        private const float ESCORT_INTERVAL   = 5f;   // как часто переотправлять свиту
        private const float ESCORT_SPREAD     = 8f;   // разброс вокруг босса (радиус кольца охраны)
        private const float COVER_MIN_RADIUS  = 30f;
        private const float COVER_MAX_RADIUS  = 40f;
        private const int   COVER_ATTEMPTS    = 20;
        private const float COVER_CHECK_RADIUS = 3f;  // радиус поиска объектов-укрытий

        public BossHoldLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Start()
        {
            BossAILayer.LayerMap.TryGetValue(BotOwner.name, out _layer);
            _startTime = Time.time;

            Vector3? cover = FindCoverPosition();
            if (cover.HasValue)
            {
                _coverTarget = cover.Value;
                _hasTarget   = true;
                // Осторожное движение, не спринт
                BotOwner.Mover.SetTargetMoveSpeed(0.7f);
                BotOwner.Mover.Sprint(false);
                Plugin.Log.LogInfo(
                    $"[BossAI] {BotOwner.name} Phase1: укрытие " +
                    $"({_coverTarget.x:F0}, {_coverTarget.z:F0})");
            }
            else
            {
                _hasTarget = false;
                Plugin.Log.LogInfo($"[BossAI] {BotOwner.name} Phase1: укрытие не найдено");
            }
        }

        public override void Stop() { }

        public override void Update(CustomLayer.ActionData data)
        {
            try
            {
                if (!_hasTarget || Time.time - _startTime > HOLD_TIMEOUT)
                {
                    _layer?.OnObjectiveComplete();
                    return;
                }

                bool inCover = Vector3.Distance(BotOwner.Position, _coverTarget) < REACH_DIST;

                // Эскорт: свита держится рядом с боссом (и в движении, и в укрытии)
                if (Time.time - _lastEscortTime > ESCORT_INTERVAL)
                {
                    OrderFollowersToGuard();
                    _lastEscortTime = Time.time;
                }

                if (inCover)
                {
                    // Уже в укрытии — редкие переотправки против случайного дрейфа NavMesh
                    if (Time.time - _lastGoToPointTime > HOLD_RESEND)
                    {
                        BotOwner.Mover.GoToPoint(_coverTarget, true, 1f);
                        _lastGoToPointTime = Time.time;
                    }
                    return;
                }

                // Движение к укрытию
                if (Time.time - _lastGoToPointTime > GOTO_INTERVAL)
                {
                    BotOwner.Mover.GoToPoint(_coverTarget, false, 1f);
                    _lastGoToPointTime = Time.time;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[BossAI] BossHoldLogic.Update: {ex.Message}");
                _layer?.OnObjectiveComplete();
            }
        }

        /// <summary>
        /// Отдаёт каждому живому follower'у команду встать рядом с боссом.
        /// Каждый получает свою точку в кольце радиуса ESCORT_SPREAD вокруг босса.
        /// </summary>
        private void OrderFollowersToGuard()
        {
            try
            {
                var group = BotOwner.BotsGroup;
                if (group == null) return;

                int total = group.MembersCount;
                int followerIdx = 0;

                for (int i = 0; i < total; i++)
                {
                    BotOwner follower = group.Member(i);
                    if (follower == null || follower == BotOwner || follower.IsDead) continue;

                    // Равномерно распределяем follower'ов по кругу вокруг босса
                    float angle = followerIdx * (360f / Mathf.Max(1, total - 1));
                    Vector3 offset = new Vector3(
                        Mathf.Cos(angle * Mathf.Deg2Rad) * ESCORT_SPREAD,
                        0f,
                        Mathf.Sin(angle * Mathf.Deg2Rad) * ESCORT_SPREAD);

                    Vector3 guardPos = BotOwner.Position + offset;

                    // Привязываем к NavMesh
                    if (NavMesh.SamplePosition(guardPos, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                        guardPos = hit.position;

                    follower.Mover.GoToPoint(guardPos, false, 1f);
                    followerIdx++;
                }
            }
            catch { }
        }

        /// <summary>
        /// Ищет позицию укрытия: точка на NavMesh в радиусе COVER_MIN–MAX_RADIUS,
        /// рядом с которой больше всего объектов с коллайдерами (стены, ящики, машины).
        /// </summary>
        private Vector3? FindCoverPosition()
        {
            try
            {
                Vector3 bossPos  = BotOwner.Position;
                Vector3 bestPoint = Vector3.zero;
                int     bestScore = -1;
                bool    foundAny  = false;

                for (int i = 0; i < COVER_ATTEMPTS; i++)
                {
                    // Случайное направление в горизонтальной плоскости
                    Vector3 dir = Random.insideUnitSphere;
                    dir.y = 0f;
                    if (dir.magnitude < 0.01f) dir = Vector3.forward;
                    dir.Normalize();

                    float dist      = Random.Range(COVER_MIN_RADIUS, COVER_MAX_RADIUS);
                    Vector3 candidate = bossPos + dir * dist;

                    if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                        continue;

                    foundAny = true;

                    // Количество объектов с коллайдерами рядом = оценка укрытия
                    Collider[] cols = Physics.OverlapSphere(hit.position, COVER_CHECK_RADIUS);
                    int score = cols.Length;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPoint = hit.position;
                    }
                }

                return foundAny ? bestPoint : (Vector3?)null;
            }
            catch { return null; }
        }
    }
}
