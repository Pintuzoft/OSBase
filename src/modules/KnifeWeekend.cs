using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using MySqlConnector;

namespace OSBase.Modules;

public class KnifeWeekend : IModule {
    public string ModuleName => "knifeweekend";

    private OSBase? osbase;
    private Config? config;
    private GameStats? gameStats;
    private Database? db;

    private bool handlersLoaded = false;
    private bool isActive = false;

    private readonly HashSet<ulong> adminSteamIds = new();
    private DateTime nextWarmupMessageUtc = DateTime.MinValue;

    private const int EventTypeNormal = 0;
    private const int EventTypeTeamKill = 1;

    private string tablePrefix = "knivhelg";
    private string statsUrl = "https://oldswedes.se/knivhelg";
    private string chatPrefix = "[OSKnivHelg]";

    private bool adminPointsEnabled = true;
    private bool ignoreWarmup = true;
    private bool showWarmupMessage = false;
    private bool createTables = true;

    private int normalPoints = 5;
    private int adminPoints = 10;
    private int topLimit = 10;
    private int warmupMessageCooldownSeconds = 10;

    private List<string> weaponKeywords = new();

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;
        gameStats = osbase.GetGameStats();
        isActive = true;

        if (osbase == null || config == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] load failed (null deps).");
            isActive = false;
            return;
        }

        config.RegisterGlobalConfigValue(ModuleName, "1");

        if (config.GetGlobalConfigValue(ModuleName, "0") != "1") {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in config.");
            isActive = false;
            return;
        }

        CreateCustomConfigs();
        LoadConfig();

        db = new Database(osbase, config);

        if (createTables) {
            CreateTables();
        }

        LoadAdminSteamIds();
        LoadHandlers();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully. admins={adminSteamIds.Count}");
    }

    public void Unload() {
        isActive = false;

        if (osbase != null && handlersLoaded) {
            osbase.DeregisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            osbase.RemoveListener<Listeners.OnMapStart>(OnMapStart);
            osbase.RemoveCommand("css_ktop", OnKnifeTopCommand);
            handlersLoaded = false;
        }

        adminSteamIds.Clear();
        db = null;
        gameStats = null;
        config = null;
        osbase = null;

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] unloaded.");
    }

    public void ReloadConfig(Config inConfig) {
        config = inConfig;
        gameStats = osbase?.GetGameStats();

        CreateCustomConfigs();
        LoadConfig();

        if (createTables) {
            CreateTables();
        }

        LoadAdminSteamIds();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] config reloaded. admins={adminSteamIds.Count}");
    }

    private void LoadHandlers() {
        if (osbase == null || handlersLoaded) {
            return;
        }

        osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        osbase.AddCommand("css_ktop", "Shows KnifeWeekend top list", OnKnifeTopCommand);

        handlersLoaded = true;
    }

    private void CreateCustomConfigs() {
        config?.CreateCustomConfig(
            $"{ModuleName}.cfg",
            "// KnifeWeekend Configuration\n" +
            "// Uses the same database configured in database.cfg.\n" +
            "table_prefix knivhelg\n" +
            "create_tables 1\n" +
            "stats_url https://oldswedes.se/knivhelg\n" +
            "chat_prefix \"[OSKnivHelg]\"\n" +
            "admin_points_enabled 1\n" +
            "ignore_warmup 1\n" +
            "show_warmup_message 0\n" +
            "warmup_message_cooldown_seconds 10\n" +
            "normal_points 5\n" +
            "admin_points 10\n" +
            "top_limit 10\n" +
            "weapon_keywords knife,bayonet,karambit,butterfly,daggers,falchion,flip,gut,huntsman,navaja,nomad,paracord,skeleton,stiletto,survival,talon,ursus,kukri\n"
        );
    }

    private void LoadConfig() {
        tablePrefix = "knivhelg";
        statsUrl = "https://oldswedes.se/knivhelg";
        chatPrefix = "[OSKnivHelg]";
        adminPointsEnabled = true;
        ignoreWarmup = true;
        showWarmupMessage = false;
        createTables = true;
        normalPoints = 5;
        adminPoints = 10;
        topLimit = 10;
        warmupMessageCooldownSeconds = 10;
        weaponKeywords = new List<string> {
            "knife", "bayonet", "karambit", "butterfly", "daggers", "falchion", "flip", "gut",
            "huntsman", "navaja", "nomad", "paracord", "skeleton", "stiletto", "survival", "talon", "ursus", "kukri"
        };

        List<string> cfg = config?.FetchCustomConfig($"{ModuleName}.cfg") ?? new List<string>();

        foreach (var rawLine in cfg) {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) {
                continue;
            }

            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) {
                Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Invalid config line skipped: {line}");
                continue;
            }

            string key = parts[0].Trim();
            string value = Unquote(parts[1].Trim());

            switch (key.ToLowerInvariant()) {
                case "table_prefix":
                    tablePrefix = SanitizeIdentifier(value);
                    break;
                case "create_tables":
                    createTables = value == "1";
                    break;
                case "stats_url":
                    statsUrl = value;
                    break;
                case "chat_prefix":
                    chatPrefix = string.IsNullOrWhiteSpace(value) ? "[OSKnivHelg]" : value;
                    break;
                case "admin_points_enabled":
                    adminPointsEnabled = value == "1";
                    break;
                case "ignore_warmup":
                    ignoreWarmup = value == "1";
                    break;
                case "show_warmup_message":
                    showWarmupMessage = value == "1";
                    break;
                case "warmup_message_cooldown_seconds":
                    warmupMessageCooldownSeconds = ParseInt(value, 10, 0, 300);
                    break;
                case "normal_points":
                    normalPoints = ParseInt(value, 5, 0, 1000);
                    break;
                case "admin_points":
                    adminPoints = ParseInt(value, 10, 0, 1000);
                    break;
                case "top_limit":
                    topLimit = ParseInt(value, 10, 1, 50);
                    break;
                case "weapon_keywords":
                    weaponKeywords = value
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(v => v.ToLowerInvariant())
                        .Where(v => v.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    break;
                default:
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Unknown config key {key}:{value}");
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(tablePrefix)) {
            tablePrefix = "knivhelg";
        }

        if (weaponKeywords.Count == 0) {
            weaponKeywords.Add("knife");
        }

        Console.WriteLine(
            $"[DEBUG] OSBase[{ModuleName}] config loaded. prefix={tablePrefix}, adminPoints={adminPointsEnabled}, " +
            $"normal={normalPoints}, admin={adminPoints}, top={topLimit}, ignoreWarmup={ignoreWarmup}"
        );
    }

    private void CreateTables() {
        if (db == null) {
            return;
        }

        string adminTable = $"""
        TABLE IF NOT EXISTS {Table("admin")} (
            steamid64 BIGINT UNSIGNED NOT NULL,
            name VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
            PRIMARY KEY (steamid64)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        string userStatsTable = $"""
        TABLE IF NOT EXISTS {Table("userstats")} (
            steamid64 BIGINT UNSIGNED NOT NULL,
            name VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
            points INT NOT NULL DEFAULT 0,
            updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
            PRIMARY KEY (steamid64),
            KEY idx_points (points)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        string eventTable = $"""
        TABLE IF NOT EXISTS {Table("event")} (
            id INT NOT NULL AUTO_INCREMENT,
            stamp DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            attacker VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
            attackerid64 BIGINT UNSIGNED NOT NULL,
            victim VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
            victimid64 BIGINT UNSIGNED NOT NULL,
            points INT NOT NULL DEFAULT 0,
            type INT NOT NULL DEFAULT 0,
            PRIMARY KEY (id),
            KEY idx_attackerid64 (attackerid64),
            KEY idx_victimid64 (victimid64),
            KEY idx_stamp (stamp)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        try {
            db.create(adminTable);
            db.create(userStatsTable);
            db.create(eventTable);
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] tables ensured.");
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed creating tables: {e.Message}");
        }
    }

    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        if (ignoreWarmup && gameStats != null && gameStats.IsWarmup) {
            MaybePrintWarmupMessage();
            return HookResult.Continue;
        }

        var attacker = eventInfo.Attacker;
        var victim = eventInfo.Userid;

        if (!IsRealPlayer(attacker) || !IsRealPlayer(victim)) {
            return HookResult.Continue;
        }

        if (attacker!.UserId!.Value == victim!.UserId!.Value) {
            return HookResult.Continue;
        }

        string weapon = eventInfo.Weapon ?? string.Empty;
        if (!IsKnifeWeapon(weapon)) {
            return HookResult.Continue;
        }

        ulong attackerSteamId64 = attacker.SteamID;
        ulong victimSteamId64 = victim.SteamID;

        if (attackerSteamId64 == 0 || victimSteamId64 == 0) {
            return HookResult.Continue;
        }

        bool teamKill = attacker.TeamNum == victim.TeamNum;
        bool victimIsAdmin = adminPointsEnabled && adminSteamIds.Contains(victimSteamId64);
        bool attackerIsAdmin = adminSteamIds.Contains(attackerSteamId64);

        int points = victimIsAdmin ? adminPoints : normalPoints;
        string attackerName = CleanName(attacker.PlayerName);
        string victimName = CleanName(victim.PlayerName);

        AddKnifeEvent(attackerName, attackerSteamId64, victimName, victimSteamId64, points, teamKill);

        if (teamKill) {
            AddPoints(victimName, victimSteamId64, points);
            AddPoints(attackerName, attackerSteamId64, -points);
        } else {
            AddPoints(attackerName, attackerSteamId64, points);
        }

        PrintKnifeMessage(attackerName, attackerIsAdmin, victimName, victimIsAdmin, points, teamKill);

        return HookResult.Continue;
    }

    private void OnMapStart(string mapName) {
        if (!isActive) {
            return;
        }

        LoadAdminSteamIds();
    }

    private void OnKnifeTopCommand(CCSPlayerController? player, CommandInfo commandInfo) {
        if (!isActive || player == null || !player.IsValid) {
            return;
        }

        ShowTopList(player);
    }

    private void LoadAdminSteamIds() {
        adminSteamIds.Clear();

        if (db == null) {
            return;
        }

        try {
            DataTable table = db.select($"steamid64 FROM {Table("admin")} WHERE steamid64 > 0");
            foreach (DataRow row in table.Rows) {
                if (TryGetUInt64(row["steamid64"], out ulong steamId64) && steamId64 > 0) {
                    adminSteamIds.Add(steamId64);
                }
            }
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed loading admins: {e.Message}");
        }
    }

    private void AddKnifeEvent(string attackerName, ulong attackerSteamId64, string victimName, ulong victimSteamId64, int points, bool teamKill) {
        if (db == null) {
            return;
        }

        string query =
            $"INTO {Table("event")} " +
            "(stamp, attacker, attackerid64, victim, victimid64, points, type) " +
            "VALUES (NOW(), @attacker, @attackerid64, @victim, @victimid64, @points, @type)";

        var parameters = new MySqlParameter[] {
            new("@attacker", attackerName),
            new("@attackerid64", attackerSteamId64),
            new("@victim", victimName),
            new("@victimid64", victimSteamId64),
            new("@points", points),
            new("@type", teamKill ? EventTypeTeamKill : EventTypeNormal)
        };

        db.insert(query, parameters);
    }

    private void AddPoints(string name, ulong steamId64, int delta) {
        if (db == null || steamId64 == 0 || delta == 0) {
            return;
        }

        string query =
            $"INTO {Table("userstats")} (steamid64, name, points) " +
            "VALUES (@steamid64, @name, @points) " +
            "ON DUPLICATE KEY UPDATE name=@name, points=points+@delta";

        var parameters = new MySqlParameter[] {
            new("@steamid64", steamId64),
            new("@name", name),
            new("@points", delta),
            new("@delta", delta)
        };

        db.insert(query, parameters);
    }

    private void ShowTopList(CCSPlayerController player) {
        if (db == null) {
            player.PrintToChat($" {ChatColors.Red}{chatPrefix}: Database unavailable.{ChatColors.Default}");
            return;
        }

        try {
            DataTable table = db.select(
                $"name, steamid64, points FROM {Table("userstats")} ORDER BY points DESC LIMIT @limit",
                new MySqlParameter("@limit", topLimit)
            );

            player.PrintToChat($" {ChatColors.Green}{chatPrefix}: Leaderboard:{ChatColors.Default}");

            int rank = 1;
            ulong self = player.SteamID;

            foreach (DataRow row in table.Rows) {
                string name = row["name"]?.ToString() ?? "Unknown";
                int points = Convert.ToInt32(row["points"]);
                TryGetUInt64(row["steamid64"], out ulong steamId64);

                string color = steamId64 == self ? ChatColors.Green.ToString() : ChatColors.Grey.ToString();
                player.PrintToChat($"  {color}{rank}. {name}: {points}p{ChatColors.Default}");
                rank++;
            }

            if (!string.IsNullOrWhiteSpace(statsUrl)) {
                player.PrintToChat($" {ChatColors.Green}{chatPrefix}: Full stats: {statsUrl}{ChatColors.Default}");
            }
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed showing top list: {e.Message}");
            player.PrintToChat($" {ChatColors.Red}{chatPrefix}: Failed to load leaderboard.{ChatColors.Default}");
        }
    }

    private void PrintKnifeMessage(string attacker, bool attackerIsAdmin, string victim, bool victimIsAdmin, int points, bool teamKill) {
        string attackerStatus = attackerIsAdmin ? "admin" : "inte admin";
        string victimStatus = victimIsAdmin ? "admin" : "inte admin";

        if (teamKill) {
            string logMessage =
                $"{attacker} ({attackerStatus}) knife-teamkillade {victim} ({victimStatus}). " +
                $"{attacker} tappade -{points}p, {victim} fick +{points}p. {victim} är {victimStatus}.";

            Console.WriteLine($"[INFO] OSBase[{ModuleName}] {logMessage}");

            Server.PrintToChatAll(
                $" {ChatColors.Green}{chatPrefix}{ChatColors.Default}: " +
                $"{ChatColors.Red}{attacker}{ChatColors.Default} ({attackerStatus}) knife-teamkillade " +
                $"{ChatColors.Gold}{victim}{ChatColors.Default} ({victimStatus}). " +
                $"{ChatColors.Red}{attacker} tappade -{points}p{ChatColors.Default}, " +
                $"{ChatColors.Green}{victim} fick +{points}p{ChatColors.Default}. " +
                $"{victim} är {victimStatus}."
            );
            return;
        }

        string normalLogMessage =
            $"{attacker} ({attackerStatus}) knivade {victim} ({victimStatus}). " +
            $"{attacker} fick +{points}p. {victim} är {victimStatus}.";

        Console.WriteLine($"[INFO] OSBase[{ModuleName}] {normalLogMessage}");

        Server.PrintToChatAll(
            $" {ChatColors.Green}{chatPrefix}{ChatColors.Default}: " +
            $"{ChatColors.Gold}{attacker}{ChatColors.Default} ({attackerStatus}) knivade " +
            $"{ChatColors.Red}{victim}{ChatColors.Default} ({victimStatus}). " +
            $"{ChatColors.Green}{attacker} fick +{points}p{ChatColors.Default}. " +
            $"{victim} är {victimStatus}."
        );
    }

    private void MaybePrintWarmupMessage() {
        if (!showWarmupMessage) {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (now < nextWarmupMessageUtc) {
            return;
        }

        nextWarmupMessageUtc = now.AddSeconds(warmupMessageCooldownSeconds);
        Server.PrintToChatAll($" {ChatColors.Green}{chatPrefix}{ChatColors.Default}: Warmup, knife does not count!");
    }

    private bool IsKnifeWeapon(string weapon) {
        if (string.IsNullOrWhiteSpace(weapon)) {
            return false;
        }

        string normalized = weapon.Trim().ToLowerInvariant();
        return weaponKeywords.Any(keyword => normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRealPlayer(CCSPlayerController? player) {
        if (player == null || !player.IsValid || !player.UserId.HasValue || player.IsHLTV || player.IsBot) {
            return false;
        }

        return player.SteamID > 0;
    }

    private string Table(string suffix) {
        string safePrefix = SanitizeIdentifier(tablePrefix);
        string safeSuffix = SanitizeIdentifier(suffix);

        if (string.IsNullOrWhiteSpace(safePrefix)) {
            safePrefix = "knivhelg";
        }

        return $"`{safePrefix}_{safeSuffix}`";
    }

    private static string SanitizeIdentifier(string value) {
        if (string.IsNullOrWhiteSpace(value)) {
            return string.Empty;
        }

        return new string(value.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
    }

    private static string Unquote(string value) {
        if (string.IsNullOrWhiteSpace(value)) {
            return string.Empty;
        }

        if (value.Length >= 2 && value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal)) {
            return value.Substring(1, value.Length - 2);
        }

        return value;
    }

    private static int ParseInt(string value, int defaultValue, int min, int max) {
        if (!int.TryParse(value, out int parsed)) {
            return defaultValue;
        }

        return Math.Clamp(parsed, min, max);
    }

    private static bool TryGetUInt64(object? value, out ulong result) {
        result = 0;

        if (value == null || value == DBNull.Value) {
            return false;
        }

        return ulong.TryParse(value.ToString(), out result);
    }

    private static string CleanName(string? name) {
        string clean = name ?? "Unknown";
        clean = clean.Replace('\n', ' ').Replace('\r', ' ').Trim();

        if (clean.Length == 0) {
            clean = "Unknown";
        }

        if (clean.Length > 64) {
            clean = clean.Substring(0, 64);
        }

        return clean;
    }
}
