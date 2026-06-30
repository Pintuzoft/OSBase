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

// Configurable "weekend event": pick which weapons score points (knife, taser, mp9, ...),
// each with its own normal/admin point values. Generalizes the old knife-only weekend.
public class EventWeekend : ModuleBase {
    public override string ModuleName => "eventweekend";

    private GameStats? gameStats;
    private Database? db;

    private readonly HashSet<ulong> adminSteamIds = new();
    private readonly List<PendingScoreEvent> pendingScoreEvents = new();
    private readonly Dictionary<ulong, PendingPointDelta> pendingPointDeltas = new();
    private DateTime nextWarmupMessageUtc = DateTime.MinValue;

    private const int EventTypeNormal = 0;
    private const int EventTypeTeamKill = 1;

    private string tablePrefix = "eventweekend";
    private string statsUrl = "https://oldswedes.se/eventweekend";
    private string chatPrefix = "[EventWeekend]";
    private string eventName = "Event Weekend";

    private bool adminPointsEnabled = true;
    private bool ignoreWarmup = true;
    private bool showWarmupMessage = false;
    private bool createTables = true;

    private int topLimit = 10;
    private int minimumPlayers = 4;
    private int warmupMessageCooldownSeconds = 10;

    private readonly List<WeaponRule> weaponRules = new();

    private static readonly string[] DefaultKnifeKeywords = {
        "knife", "bayonet", "m9_bayonet", "karambit", "butterfly",
        "daggers", "shadow", "push", "falchion", "flip", "gut",
        "huntsman", "tactical", "navaja", "gypsy_jackknife", "nomad", "outdoor",
        "paracord", "cord", "skeleton", "stiletto", "survival", "canis",
        "talon", "widowmaker", "ursus", "kukri", "bowie", "survival_bowie",
        "classic", "css", "default"
    };

    private sealed class WeaponRule {
        public string Label { get; init; } = "weapon";
        public List<string> Keywords { get; init; } = new();
        public int NormalPoints { get; init; }
        public int AdminPoints { get; init; }
    }

    private sealed class PendingScoreEvent {
        public string AttackerName { get; init; } = "Unknown";
        public ulong AttackerSteamId64 { get; init; }
        public string VictimName { get; init; } = "Unknown";
        public ulong VictimSteamId64 { get; init; }
        public int Points { get; init; }
        public bool TeamKill { get; init; }
        public string Weapon { get; init; } = "weapon";
    }

    private sealed class PendingPointDelta {
        public string Name { get; set; } = "Unknown";
        public int Delta { get; set; }
    }

    protected override void OnLoad() {
        gameStats = osbase?.GetGameStats();

        CreateCustomConfigs();
        LoadConfig();

        db = new Database(osbase!, config!);

        if (createTables) {
            CreateTables();
        }

        LoadAdminSteamIds();
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] admins={adminSteamIds.Count}, rules={weaponRules.Count}");
    }

    protected override void OnUnload() {
        FlushPendingWrites("Unload");

        adminSteamIds.Clear();
        weaponRules.Clear();
        db = null;
        gameStats = null;
    }

    protected override void OnReloadConfig() {
        gameStats = osbase?.GetGameStats();

        CreateCustomConfigs();
        LoadConfig();

        if (createTables) {
            CreateTables();
        }

        LoadAdminSteamIds();
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] admins={adminSteamIds.Count}, rules={weaponRules.Count}");
    }

    protected override void RegisterHandlers() {
        osbase?.SubscribeToEvent<EventPlayerDeath>(OnPlayerDeath);
        osbase?.SubscribeToEvent<EventRoundStart>(OnRoundStart);
        osbase?.SubscribeToEvent<EventRoundEnd>(OnRoundEnd);
        osbase?.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        osbase?.AddCommand("css_etop", "Shows the EventWeekend leaderboard", OnEventTopCommand);
    }

    protected override void UnregisterHandlers() {
        osbase?.UnsubscribeFromEvent<EventPlayerDeath>(OnPlayerDeath);
        osbase?.UnsubscribeFromEvent<EventRoundStart>(OnRoundStart);
        osbase?.UnsubscribeFromEvent<EventRoundEnd>(OnRoundEnd);
        osbase?.RemoveListener<Listeners.OnMapStart>(OnMapStart);
        osbase?.RemoveCommand("css_etop", OnEventTopCommand);
    }

    private void CreateCustomConfigs() {
        config?.CreateCustomConfig(
            $"{ModuleName}.cfg",
            "// EventWeekend Configuration\n" +
            "// A configurable weekend event: pick which weapons score points.\n" +
            "// Uses the same database configured in database.cfg.\n" +
            "event_name \"Knife Weekend\"\n" +
            "table_prefix eventweekend\n" +
            "create_tables 1\n" +
            "stats_url https://oldswedes.se/eventweekend\n" +
            "chat_prefix \"[EventWeekend]\"\n" +
            "admin_points_enabled 1\n" +
            "ignore_warmup 1\n" +
            "show_warmup_message 0\n" +
            "warmup_message_cooldown_seconds 10\n" +
            "minimum_players 4\n" +
            "top_limit 10\n" +
            "\n" +
            "// One line per weapon/group that scores points. Format:\n" +
            "//   weapon_rule <label> <normal_points> <admin_points> <keyword[,keyword,...]>\n" +
            "// <label> shows in kill messages. Keywords are matched (case-insensitive\n" +
            "// substring) against the kill weapon name. admin_points apply only when\n" +
            "// admin_points_enabled is 1 and the victim is an admin.\n" +
            "// Examples:\n" +
            "//   weapon_rule taser 1 3 taser,zeus   (admin victims worth more)\n" +
            "//   weapon_rule mp9 1 1 mp9            (same points for everyone)\n" +
            "weapon_rule knife 5 10 " + string.Join(',', DefaultKnifeKeywords) + "\n"
        );
    }

    private void LoadConfig() {
        tablePrefix = "eventweekend";
        statsUrl = "https://oldswedes.se/eventweekend";
        chatPrefix = "[EventWeekend]";
        eventName = "Event Weekend";
        adminPointsEnabled = true;
        ignoreWarmup = true;
        showWarmupMessage = false;
        createTables = true;
        minimumPlayers = 4;
        topLimit = 10;
        warmupMessageCooldownSeconds = 10;
        weaponRules.Clear();

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
                case "event_name":
                    eventName = string.IsNullOrWhiteSpace(value) ? "Event Weekend" : value;
                    break;
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
                    chatPrefix = string.IsNullOrWhiteSpace(value) ? "[EventWeekend]" : value;
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
                case "minimum_players":
                    minimumPlayers = ParseInt(value, 4, 0, 64);
                    break;
                case "top_limit":
                    topLimit = ParseInt(value, 10, 1, 50);
                    break;
                case "weapon_rule":
                    AddWeaponRule(value);
                    break;
                default:
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Unknown config key {key}:{value}");
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(tablePrefix)) {
            tablePrefix = "eventweekend";
        }

        // Never run silently dead: fall back to the classic knife rule if nothing was configured.
        if (weaponRules.Count == 0) {
            Console.WriteLine($"[WARN] OSBase[{ModuleName}]: No weapon_rule configured, using default knife rule.");
            weaponRules.Add(new WeaponRule {
                Label = "knife",
                Keywords = DefaultKnifeKeywords.ToList(),
                NormalPoints = 5,
                AdminPoints = 10
            });
        }

        Console.WriteLine(
            $"[DEBUG] OSBase[{ModuleName}] config loaded. event={eventName}, prefix={tablePrefix}, " +
            $"adminPoints={adminPointsEnabled}, minPlayers={minimumPlayers}, top={topLimit}, ignoreWarmup={ignoreWarmup}, " +
            $"rules=[{string.Join(", ", weaponRules.Select(r => $"{r.Label}:{r.NormalPoints}/{r.AdminPoints}"))}]"
        );
    }

    // Parse: "<label> <normal_points> <admin_points> <keyword[,keyword,...]>"
    private void AddWeaponRule(string value) {
        var parts = value.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 4) {
            Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Invalid weapon_rule skipped: {value}");
            return;
        }

        var keywords = parts[3]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v.ToLowerInvariant())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (keywords.Count == 0) {
            Console.WriteLine($"[WARN] OSBase[{ModuleName}]: weapon_rule has no keywords, skipped: {value}");
            return;
        }

        weaponRules.Add(new WeaponRule {
            Label = parts[0].Trim(),
            NormalPoints = ParseInt(parts[1], 0, -1000, 1000),
            AdminPoints = ParseInt(parts[2], 0, -1000, 1000),
            Keywords = keywords
        });
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
            weapon VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
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

    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo) {
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
        WeaponRule? rule = MatchWeaponRule(weapon);
        if (rule == null) {
            return HookResult.Continue;
        }

        int activeHumans = CountActiveHumans();
        if (activeHumans < minimumPlayers) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Kill ignored: only {activeHumans}/{minimumPlayers} active human players in T/CT.");
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

        int points = victimIsAdmin ? rule.AdminPoints : rule.NormalPoints;
        if (points == 0) {
            return HookResult.Continue;
        }

        string attackerName = CleanName(attacker.PlayerName);
        string victimName = CleanName(victim.PlayerName);

        AddScoreEvent(attackerName, attackerSteamId64, victimName, victimSteamId64, points, teamKill, rule.Label);

        if (teamKill) {
            AddPoints(victimName, victimSteamId64, points);
            AddPoints(attackerName, attackerSteamId64, -points);
        } else {
            AddPoints(attackerName, attackerSteamId64, points);
        }

        PrintScoreMessage(attackerName, attackerIsAdmin, victimName, victimIsAdmin, points, teamKill, rule.Label);

        return HookResult.Continue;
    }

    private void OnMapStart(string mapName) {
        if (!isActive) {
            return;
        }

        FlushPendingWrites("MapStart");
        db?.SetAutoDrain(true);

        LoadAdminSteamIds();
    }

    private HookResult OnRoundStart(EventRoundStart _) {
        if (!isActive) {
            return HookResult.Continue;
        }

        // Queue writes during live round to avoid DB interference.
        db?.SetAutoDrain(false);
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd _) {
        if (!isActive) {
            return HookResult.Continue;
        }

        db?.SetAutoDrain(true);
        FlushPendingWrites("RoundEnd");
        return HookResult.Continue;
    }

    private void OnEventTopCommand(CCSPlayerController? player, CommandInfo commandInfo) {
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

    private WeaponRule? MatchWeaponRule(string weapon) {
        if (string.IsNullOrWhiteSpace(weapon)) {
            return null;
        }

        string normalized = weapon.Trim().ToLowerInvariant();
        return weaponRules.FirstOrDefault(rule =>
            rule.Keywords.Any(keyword => normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        );
    }

    private void AddScoreEvent(string attackerName, ulong attackerSteamId64, string victimName, ulong victimSteamId64, int points, bool teamKill, string weapon) {
        if (attackerSteamId64 == 0 || victimSteamId64 == 0) {
            return;
        }

        pendingScoreEvents.Add(new PendingScoreEvent {
            AttackerName = attackerName,
            AttackerSteamId64 = attackerSteamId64,
            VictimName = victimName,
            VictimSteamId64 = victimSteamId64,
            Points = points,
            TeamKill = teamKill,
            Weapon = weapon
        });
    }

    private void AddPoints(string name, ulong steamId64, int delta) {
        if (steamId64 == 0 || delta == 0) {
            return;
        }

        if (!pendingPointDeltas.TryGetValue(steamId64, out var pending)) {
            pending = new PendingPointDelta {
                Name = name,
                Delta = 0
            };
            pendingPointDeltas[steamId64] = pending;
        }

        pending.Name = name;
        pending.Delta += delta;
    }

    private void FlushPendingWrites(string source) {
        if (db == null) {
            return;
        }

        if (pendingScoreEvents.Count == 0 && pendingPointDeltas.Count == 0) {
            return;
        }

        int eventsWritten = 0;
        int pointRowsWritten = 0;

        foreach (var ev in pendingScoreEvents) {
            string query =
                $"INTO {Table("event")} " +
                "(stamp, attacker, attackerid64, victim, victimid64, weapon, points, type) " +
                "VALUES (NOW(), @attacker, @attackerid64, @victim, @victimid64, @weapon, @points, @type)";

            var parameters = new MySqlParameter[] {
                new("@attacker", ev.AttackerName),
                new("@attackerid64", ev.AttackerSteamId64),
                new("@victim", ev.VictimName),
                new("@victimid64", ev.VictimSteamId64),
                new("@weapon", ev.Weapon),
                new("@points", ev.Points),
                new("@type", ev.TeamKill ? EventTypeTeamKill : EventTypeNormal)
            };

            db.insertAsync(query, parameters);
            eventsWritten++;
        }

        foreach (var kv in pendingPointDeltas) {
            ulong steamId64 = kv.Key;
            var pending = kv.Value;

            if (pending.Delta == 0) {
                continue;
            }

            string query =
                $"INTO {Table("userstats")} (steamid64, name, points) " +
                "VALUES (@steamid64, @name, @points) " +
                "ON DUPLICATE KEY UPDATE name=@name, points=points+@delta";

            var parameters = new MySqlParameter[] {
                new("@steamid64", steamId64),
                new("@name", pending.Name),
                new("@points", pending.Delta),
                new("@delta", pending.Delta)
            };

            db.insertAsync(query, parameters);
            pointRowsWritten++;
        }

        // We are in a controlled timing window (round end/map start/unload),
        // so block briefly to ensure queued writes are drained.
        db.FlushPendingWrites(1000);

        pendingScoreEvents.Clear();
        pendingPointDeltas.Clear();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] flushed pending DB writes ({source}): events={eventsWritten}, pointRows={pointRowsWritten}");
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

            player.PrintToChat($" {ChatColors.Green}{chatPrefix}: {eventName} leaderboard:{ChatColors.Default}");

            int rank = 1;
            ulong self = player.SteamID;

            foreach (DataRow row in table.Rows) {
                string name = row["name"]?.ToString() ?? "Unknown";
                int points = Convert.ToInt32(row["points"]);
                TryGetUInt64(row["steamid64"], out ulong steamId64);

                string color = steamId64 == self ? ChatColors.Green.ToString() : ChatColors.Default.ToString();
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

    private void PrintScoreMessage(string attacker, bool attackerIsAdmin, string victim, bool victimIsAdmin, int points, bool teamKill, string weapon) {
        string attackerDisplay = attacker + AdminSuffix(attackerIsAdmin);
        string victimDisplay = victim + AdminSuffix(victimIsAdmin);

        if (teamKill) {
            Console.WriteLine($"[INFO] OSBase[{ModuleName}] {attackerDisplay} teamkillade {victimDisplay} med {weapon}. {attacker} -{points}p, {victim} +{points}p.");

            Server.PrintToChatAll(
                $" {ChatColors.Green}{chatPrefix}{ChatColors.Default}: " +
                $"{ChatColors.Red}{attackerDisplay}{ChatColors.Default} teamkillade " +
                $"{victimDisplay} med {weapon}. " +
                $"{ChatColors.Red}{attacker} -{points}p{ChatColors.Default}, " +
                $"{ChatColors.Green}{victim} +{points}p{ChatColors.Default}."
            );
            return;
        }

        Console.WriteLine($"[INFO] OSBase[{ModuleName}] {attackerDisplay} dödade {victimDisplay} med {weapon} och fick +{points}p.");

        Server.PrintToChatAll(
            $" {ChatColors.Green}{chatPrefix}{ChatColors.Default}: " +
            $"{ChatColors.Green}{attackerDisplay}{ChatColors.Default} dödade " +
            $"{ChatColors.Red}{victimDisplay}{ChatColors.Default} med {weapon} och fick " +
            $"{ChatColors.Green}+{points}p{ChatColors.Default}."
        );
    }

    private static string AdminSuffix(bool isAdmin) {
        return isAdmin ? " (admin)" : string.Empty;
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
        Server.PrintToChatAll($" {ChatColors.Green}{chatPrefix}{ChatColors.Default}: Warmup, kills do not count!");
    }

    private int CountActiveHumans() {
        return Utilities.GetPlayers().Count(player =>
            IsRealPlayer(player) &&
            (player.TeamNum == (int)CsTeam.Terrorist || player.TeamNum == (int)CsTeam.CounterTerrorist)
        );
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
            safePrefix = "eventweekend";
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
