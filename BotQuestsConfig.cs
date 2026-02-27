using BepInEx.Configuration;

namespace BotQuests
{
    /// <summary>
    /// Все настройки мода — доступны через F12 / BepInEx ConfigManager.
    /// Инициализируется один раз из Plugin.Awake().
    /// </summary>
    public static class BotQuestsConfig
    {
        // ── Тайминги ────────────────────────────────────────────────────────────
        public static ConfigEntry<float> SpawnRushWindow;
        public static ConfigEntry<float> ObjectiveCooldown;
        public static ConfigEntry<float> BossZoneCooldown;
        public static ConfigEntry<float> StuckPointCooldown;
        public static ConfigEntry<float> ObjectiveTimeout;
        public static ConfigEntry<float> HuntUpdateInterval;
        public static ConfigEntry<float> BossSearchDuration;
        public static ConfigEntry<float> BossSearchWanderInterval;
        public static ConfigEntry<float> CombatCooldownNormal;
        public static ConfigEntry<float> CombatCooldownCombatQuest;
        public static ConfigEntry<float> CombatCooldownBoss;

        // ── Дистанции ───────────────────────────────────────────────────────────
        public static ConfigEntry<float> BossZoneMaxDist;
        public static ConfigEntry<float> BossZoneRadius;
        public static ConfigEntry<float> NearTargetDist;
        public static ConfigEntry<float> BossSearchRadius;
        public static ConfigEntry<float> HuntPatrolRadius;
        public static ConfigEntry<float> HuntPatrolWander;
        public static ConfigEntry<float> AirdropScanInterval;

        // ── Скоринг ─────────────────────────────────────────────────────────────
        public static ConfigEntry<float> DistanceWeighting;
        public static ConfigEntry<float> DesirabilityWeighting;
        public static ConfigEntry<float> ExfilDirWeighting;
        public static ConfigEntry<float> ExfilAngleThreshold;
        public static ConfigEntry<float> DistanceRandomness;
        public static ConfigEntry<float> DesirabilityRandomness;

        // ── Желанность квестов ──────────────────────────────────────────────────
        public static ConfigEntry<float> Desirability_BossHunter;
        public static ConfigEntry<float> Desirability_SpawnWander;
        public static ConfigEntry<float> Desirability_HuntTarget;
        public static ConfigEntry<float> Desirability_AirdropChaser;

        // ── Лимиты ──────────────────────────────────────────────────────────────
        public static ConfigEntry<int> MaxSpawnRush;
        public static ConfigEntry<int> BossZoneMaxBots;
        public static ConfigEntry<int> MaxStuck;

        // ── Включение типов квестов ─────────────────────────────────────────────
        public static ConfigEntry<bool> EnableBossHunter;
        public static ConfigEntry<bool> EnableSpawnWander;
        public static ConfigEntry<bool> EnableHuntTarget;
        public static ConfigEntry<bool> EnableAirdropChaser;
        public static ConfigEntry<bool> EnableEftQuests;

        public static void Init(ConfigFile cfg)
        {
            const string T = "BotQuests | Тайминги";
            const string D = "BotQuests | Дистанции";
            const string S = "BotQuests | Скоринг";
            const string Q = "BotQuests | Желанность";
            const string L = "BotQuests | Лимиты";
            const string E = "BotQuests | Включение";

            SpawnRushWindow        = cfg.Bind(T, "SpawnRush окно (сек)",          60f,  "Время от старта рейда, в которое выдаётся SpawnRush");
            ObjectiveCooldown      = cfg.Bind(T, "Пауза после цели (сек)",         15f,  "Пауза после выполнения квеста перед выбором следующего");
            BossZoneCooldown       = cfg.Bind(T, "Кулдаун босс-зоны (сек)",       600f, "Сколько не отправлять ботов в проверенную босс-зону");
            StuckPointCooldown     = cfg.Bind(T, "Кулдаун точки (застряли, сек)", 300f, "Блокировка точки для всех ботов после застревания");
            ObjectiveTimeout       = cfg.Bind(T, "Таймаут квеста (сек)",          180f, "Максимальное время на выполнение одного квеста");
            HuntUpdateInterval     = cfg.Bind(T, "Интервал Hunt обновления (сек)", 60f, "Как часто бот ищет новые выстрелы (Hunt квест)");
            BossSearchDuration     = cfg.Bind(T, "Длительность осмотра зоны (сек)", 30f, "Сколько сек бот осматривает босс-зону после прибытия");
            BossSearchWanderInterval = cfg.Bind(T, "Интервал блуждания в зоне (сек)", 8f, "Как часто менять точку блуждания при осмотре");
            CombatCooldownNormal   = cfg.Bind(T, "Пауза после боя — обычный (сек)", 30f, "Пауза квест-слоя после боя для обычных ботов");
            CombatCooldownCombatQuest = cfg.Bind(T, "Пауза после боя — бой-квест (сек)", 3f, "Пауза для Hunt/BossHunter квестов после боя");
            CombatCooldownBoss     = cfg.Bind(T, "Пауза после боя — босс (сек)",  10f,  "Пауза после боя для боссов и свиты");

            BossZoneMaxDist   = cfg.Bind(D, "Макс. дистанция до босс-зоны",   400f, "Боты дальше этой дистанции не получают BossHunter квест");
            BossZoneRadius    = cfg.Bind(D, "Радиус блокировки зон (м)",       200f, "Соседние зоны в этом радиусе блокируются после проверки");
            NearTargetDist    = cfg.Bind(D, "Дистанция рядом с целью (м)",    15f, "В этом радиусе stick-детекция и Boss осмотр начинаются");
            BossSearchRadius  = cfg.Bind(D, "Радиус осмотра босс-зоны (м)",     25f, "Радиус блуждания при осмотре зоны");
            HuntPatrolRadius  = cfg.Bind(D, "Радиус Hunt патруля (м)",           30f, "Радиус патруля когда выстрелов нет");
            HuntPatrolWander  = cfg.Bind(D, "Интервал смены точки патруля (сек)", 10f, "Пауза между сменой точки патруля Hunt");
            AirdropScanInterval = cfg.Bind(D, "Интервал сканирования аирдропов (сек)", 30f, "Как часто обновлять список упавших аирдропов");

            DistanceWeighting      = cfg.Bind(S, "Вес дистанции",           1.0f, "Влияние расстояния на выбор квеста (чем дальше — тем интереснее)");
            DesirabilityWeighting  = cfg.Bind(S, "Вес желанности",          1.0f, "Влияние Desirability на скоринг");
            ExfilDirWeighting      = cfg.Bind(S, "Вес направления к экстракту", 1.0f, "Бонус за цели по пути к экстракту");
            ExfilAngleThreshold    = cfg.Bind(S, "Порог угла к экстракту (°)", 90f, "Цели в этом угле считаются 'по пути' и получают макс. бонус");
            DistanceRandomness     = cfg.Bind(S, "Рандом дистанции (±%)",    0.2f, "Случайный разброс дистанции при скоринге (0.2 = ±20%)");
            DesirabilityRandomness = cfg.Bind(S, "Рандом желанности (±%)",   0.2f, "Случайный разброс желанности при скоринге");

            Desirability_BossHunter  = cfg.Bind(Q, "Желанность: BossHunter",  75f, "Базовая желанность квеста проверки босс-зоны");
            Desirability_SpawnWander = cfg.Bind(Q, "Желанность: SpawnWander", 25f, "Базовая желанность блуждания по точкам спавна");
            Desirability_HuntTarget  = cfg.Bind(Q, "Желанность: HuntTarget",  65f, "Базовая желанность охоты по звукам выстрелов");
            Desirability_AirdropChaser = cfg.Bind(Q, "Желанность: AirdropChaser", 85f, "Базовая желанность квеста к аирдропу");

            MaxSpawnRush    = cfg.Bind(L, "Макс. SpawnRush ботов",   5, "Сколько ботов получат SpawnRush в начале рейда");
            BossZoneMaxBots = cfg.Bind(L, "Макс. ботов на босс-зону", 2, "Одновременно идущих ботов к одной зоне");
            MaxStuck        = cfg.Bind(L, "Макс. застреваний (перм. стоп)", 5, "После N застреваний бот полностью останавливается");

            EnableBossHunter   = cfg.Bind(E, "Включить BossHunter",    true, "Боты идут проверять зоны спавна боссов");
            EnableSpawnWander  = cfg.Bind(E, "Включить SpawnWander",   true, "Боты блуждают по точкам спавна PMC");
            EnableHuntTarget   = cfg.Bind(E, "Включить HuntTarget",    true, "Боты идут на звуки выстрелов");
            EnableAirdropChaser = cfg.Bind(E, "Включить AirdropChaser", true, "Боты идут к упавшим аирдропам");
            EnableEftQuests    = cfg.Bind(E, "Включить EFT квесты",    true, "Боты ходят по зонам EFT-квестов");
        }
    }
}
