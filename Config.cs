using BepInEx.Configuration;

namespace LifePMC
{
    public static class LifePMCConfig
    {
        // ── Тайминги ────────────────────────────────────────────────────────────
        public static ConfigEntry<float> ObjectiveCooldown;
        public static ConfigEntry<float> ObjectiveTimeout;
        public static ConfigEntry<float> CombatCooldown;
        public static ConfigEntry<float> DefaultWaitTime;

        // ── Саб-точки ────────────────────────────────────────────────────────────
        public static ConfigEntry<float> SubPointWaitTime;

        // ── Взаимодействия ───────────────────────────────────────────────────────
        public static ConfigEntry<float> InteractChance;
        public static ConfigEntry<int>   MaxInteractBots;
        public static ConfigEntry<float> MaxInteractDist;

        // ── Лимиты ──────────────────────────────────────────────────────────────
        public static ConfigEntry<int> MaxStuck;

        public static void Init(ConfigFile cfg)
        {
            const string T = "LifePMC | Тайминги";
            const string L = "LifePMC | Лимиты";

            ObjectiveCooldown = cfg.Bind(T, "Пауза после цели (сек)", 20f,
                "Задержка перед выбором следующей точки после достижения текущей");

            ObjectiveTimeout = cfg.Bind(T, "Таймаут квеста (сек)", 300f,
                "Максимальное время на достижение одной точки (включая все вейпоинты маршрута)");

            CombatCooldown = cfg.Bind(T, "Пауза после боя (сек)", 30f,
                "Задержка после боя перед возобновлением движения к точке");

            DefaultWaitTime = cfg.Bind(T, "Время ожидания на точке (сек)", 30f,
                "Сколько секунд бот стоит на точке если у неё wait_time = 0");

            SubPointWaitTime = cfg.Bind(T, "Время ожидания на саб-точке (сек)", 15f,
                "Сколько секунд бот стоит на каждой саб-точке если у неё wait_time = 0");

            const string I = "LifePMC | Взаимодействия";
            InteractChance = cfg.Bind(I, "Шанс посетить рубильник (0-1)", 0.5f,
                new BepInEx.Configuration.ConfigDescription(
                    "Вероятность (0.0–1.0) что бот посетит точку взаимодействия перед первым заданием. " +
                    "0 = никогда, 1 = всегда (если есть точки).",
                    new BepInEx.Configuration.AcceptableValueRange<float>(0f, 1f)));

            MaxInteractBots = cfg.Bind(I, "Макс. ботов на одну точку", 2,
                new BepInEx.Configuration.ConfigDescription(
                    "Сколько ботов одновременно могут идти к одной interact-точке. " +
                    "Остальные пропускают её и идут к обычным заданиям.",
                    new BepInEx.Configuration.AcceptableValueRange<int>(1, 10)));

            MaxInteractDist = cfg.Bind(I, "Макс. дистанция до точки (м)", 600f,
                new BepInEx.Configuration.ConfigDescription(
                    "Боты дальше этого расстояния от interact-точки не будут к ней идти.",
                    new BepInEx.Configuration.AcceptableValueRange<float>(50f, 2000f)));

            MaxStuck = cfg.Bind(L, "Макс. застреваний", 5,
                "После N застреваний бот перестаёт выполнять задания на весь рейд");
        }
    }
}
