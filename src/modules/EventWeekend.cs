using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using MySqlConnector;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace OSBase.Modules;

// Weekend weapon event driven by the OSWeb site: the site writes the active rules
// (weapons + per-kill points, admins worth more) into weapon_event_rules and owns those
// rows. The module idles while the table has no active event, and while one is live it
// scores kills into a per-(event_id, steamid64) tally the site can read back.
public class EventWeekend : ModuleBase {
    public override string ModuleName => "eventweekend";

    private GameStats? gameStats;
    private Database? db;

    private const string RulesTable = "weapon_event_rules";
    private const string ScoresTable = "weapon_event_scores";
    private const string AdminTable = "eventweekend_admin";
    private const float RulesRefreshIntervalSeconds = 60.0f;

    private readonly HashSet<ulong> adminSteamIds = new();
    private readonly Dictionary<int, LiveEvent> liveEvents = new();
    private readonly Dictionary<(int EventId, ulong SteamId64), PendingScore> pendingScores = new();
    private bool flushInProgress;
    private Timer? rulesTimer;
    private DateTime nextWarmupMessageUtc = DateTime.MinValue;

    private string statsUrl = "https://oldswedes.se/eventweekend";
    private string chatPrefix = "[EventWeekend]";

    private bool ignoreWarmup = true;
    private bool showWarmupMessage = false;

    private int topLimit = 10;
    private int minimumPlayers = 4;
    private int warmupMessageCooldownSeconds = 10;

    // Kill logs report specific knife models (bayonet, karambit, ...); a "knife" rule
    // from the site should match all of them.
    private static readonly string[] KnifeKeywords = {
        "knife", "bayonet", "m9_bayonet", "karambit", "butterfly",
        "daggers", "shadow", "push", "falchion", "flip", "gut",
        "huntsman", "tactical", "navaja", "gypsy_jackknife", "nomad", "outdoor",
        "paracord", "cord", "skeleton", "stiletto", "survival", "canis",
        "talon", "widowmaker", "ursus", "kukri", "bowie", "survival_bowie",
        "classic", "css", "default"
    };

    private sealed class LiveEvent {
        public int EventId { get; init; }
        public string Name { get; init; } = "Event Weekend";
        // normalized kill-log weapon name -> points
        public Dictionary<string, (int Player, int Admin)> Weapons { get; } = new();
    }

    private sealed class PendingScore {
        public string Name { get; set; } = "Unknown";
        public int Points { get; set; }
        public int Kills { get; set; }
    }

    protected override void OnLoad() {
        gameStats = osbase?.GetGameStats();

        CreateCustomConfigs();
        LoadConfig();

        db = new Database(osbase!, config!);

        CreateTables();
        RefreshRules("Load");
        StartRulesTimer();
    }

    protected override void OnUnload() {
        StopRulesTimer();
        FlushPendingWrites("Unload");

        adminSteamIds.Clear();
        liveEvents.Clear();
        db = null;
        gameStats = null;
    }

    protected override void OnReloadConfig() {
        gameStats = osbase?.GetGameStats();

        CreateCustomConfigs();
        LoadConfig();

        CreateTables();
        RefreshRules("ReloadConfig");
    }

    protected override void RegisterHandlers() {
        osbase?.SubscribeToEvent<EventPlayerDeath>(OnPlayerDeath);
        osbase?.SubscribeToEvent<EventRoundEnd>(OnRoundEnd);
        osbase?.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        osbase?.AddCommand("css_etop", "Shows the EventWeekend leaderboard", OnEventTopCommand);
    }

    protected override void UnregisterHandlers() {
        osbase?.UnsubscribeFromEvent<EventPlayerDeath>(OnPlayerDeath);
        osbase?.UnsubscribeFromEvent<EventRoundEnd>(OnRoundEnd);
        osbase?.RemoveListener<Listeners.OnMapStart>(OnMapStart);
        osbase?.RemoveCommand("css_etop", OnEventTopCommand);
    }

    private void CreateCustomConfigs() {
        config?.CreateCustomConfig(
            $"{ModuleName}.cfg",
            "// EventWeekend Configuration\n" +
            "// Which weapons score and what they are worth is NOT configured here:\n" +
            "// the website writes the active rules into the weapon_event_rules table\n" +
            "// and this module reads them from there. Only operational knobs below.\n" +
            "stats_url https://oldswedes.se/eventweekend\n" +
            "chat_prefix \"[EventWeekend]\"\n" +
            "ignore_warmup 1\n" +
            "show_warmup_message 0\n" +
            "warmup_message_cooldown_seconds 10\n" +
            "minimum_players 4\n" +
            "top_limit 10\n"
        );
    }

    private void LoadConfig() {
        statsUrl = "https://oldswedes.se/eventweekend";
        chatPrefix = "[EventWeekend]";
        ignoreWarmup = true;
        showWarmupMessage = false;
        minimumPlayers = 4;
        topLimit = 10;
        warmupMessageCooldownSeconds = 10;

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
                case "stats_url":
                    statsUrl = value;
                    break;
                case "chat_prefix":
                    chatPrefix = string.IsNullOrWhiteSpace(value) ? "[EventWeekend]" : value;
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
                // Legacy keys from the config-driven era; rules now live in the DB.
                case "event_name":
                case "table_prefix":
                case "create_tables":
                case "admin_points_enabled":
                case "weapon_rule":
                    break;
                default:
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Unknown config key {key}:{value}");
                    break;
            }
        }

        Console.WriteLine(
            $"[DEBUG] OSBase[{ModuleName}] config loaded. minPlayers={minimumPlayers}, top={topLimit}, ignoreWarmup={ignoreWarmup}"
        );
    }

    private void CreateTables() {
        if (db == null) {
            return;
        }

        // Contract table owned by the website; created here too so either side can go first.
        string rulesTable = $"""
        TABLE IF NOT EXISTS {RulesTable} (
            event_id      INT NOT NULL,
            event_name    VARCHAR(64) NOT NULL,
            starts_at     DATETIME NOT NULL,
            ends_at       DATETIME NOT NULL,
            weapon        VARCHAR(32) NOT NULL,
            player_points INT NOT NULL,
            admin_points  INT NOT NULL,
            PRIMARY KEY (event_id, weapon)
        ) ENGINE=InnoDB;
        """;

        string scoresTable = $"""
        TABLE IF NOT EXISTS {ScoresTable} (
            event_id INT NOT NULL,
            steamid64 BIGINT UNSIGNED NOT NULL,
            name VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
            points INT NOT NULL DEFAULT 0,
            kills INT NOT NULL DEFAULT 0,
            updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
            PRIMARY KEY (event_id, steamid64),
            KEY idx_event_points (event_id, points)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        string adminTable = $"""
        TABLE IF NOT EXISTS {AdminTable} (
            steamid64 BIGINT UNSIGNED NOT NULL,
            name VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
            PRIMARY KEY (steamid64)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        try {
            db.create(rulesTable);
            db.create(scoresTable);
            db.create(adminTable);
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] tables ensured.");
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed creating tables: {e.Message}");
        }
    }

    // ----- Rules refresh (DB -> memory) -----

    private void StartRulesTimer() {
        if (!isActive || osbase == null) {
            return;
        }

        StopRulesTimer();
        rulesTimer = osbase.AddTimer(RulesRefreshIntervalSeconds, () => RefreshRules("Timer"), TimerFlags.REPEAT);
    }

    private void StopRulesTimer() {
        rulesTimer?.Kill();
        rulesTimer = null;
    }

    // Reads active rules + admin list off-thread, then applies them on the game thread.
    private void RefreshRules(string source) {
        var database = db;
        if (!isActive || database == null) {
            return;
        }

        Task.Run(() => {
            try {
                // On DB failure keep the last known rules instead of mistaking the
                // outage for "event ended"; the next tick will catch up.
                if (!database.trySelect(
                        "event_id, event_name, weapon, player_points, admin_points " +
                        $"FROM {RulesTable} WHERE NOW() BETWEEN starts_at AND ends_at",
                        out DataTable rulesTable) ||
                    !database.trySelect($"steamid64 FROM {AdminTable} WHERE steamid64 > 0", out DataTable adminTable)) {
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}] rules refresh skipped ({source}): database unavailable, keeping current rules.");
                    return;
                }

                var events = ParseRules(rulesTable);
                var admins = ParseAdmins(adminTable);

                Server.NextFrame(() => ApplyRefresh(events, admins, source));
            } catch (Exception e) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] rules refresh failed: {e.Message}");
            }
        });
    }

    private static Dictionary<int, LiveEvent> ParseRules(DataTable table) {
        var events = new Dictionary<int, LiveEvent>();

        foreach (DataRow row in table.Rows) {
            string weapon = NormalizeWeapon(row["weapon"]?.ToString());
            if (weapon.Length == 0) {
                continue;
            }

            int eventId = Convert.ToInt32(row["event_id"]);
            if (!events.TryGetValue(eventId, out var ev)) {
                ev = new LiveEvent {
                    EventId = eventId,
                    Name = CleanName(row["event_name"]?.ToString())
                };
                events[eventId] = ev;
            }

            ev.Weapons[weapon] = (Convert.ToInt32(row["player_points"]), Convert.ToInt32(row["admin_points"]));
        }

        return events;
    }

    private static HashSet<ulong> ParseAdmins(DataTable table) {
        var admins = new HashSet<ulong>();

        foreach (DataRow row in table.Rows) {
            if (TryGetUInt64(row["steamid64"], out ulong steamId64) && steamId64 > 0) {
                admins.Add(steamId64);
            }
        }

        return admins;
    }

    private void ApplyRefresh(Dictionary<int, LiveEvent> events, HashSet<ulong> admins, string source) {
        if (!isActive) {
            return;
        }

        adminSteamIds.Clear();
        adminSteamIds.UnionWith(admins);

        foreach (var ev in events.Values.Where(ev => !liveEvents.ContainsKey(ev.EventId))) {
            Console.WriteLine($"[INFO] OSBase[{ModuleName}] event live: id={ev.EventId}, name={ev.Name}, weapons=[{string.Join(", ", ev.Weapons.Select(w => $"{w.Key}:{w.Value.Player}/{w.Value.Admin}"))}]");
            Server.PrintToChatAll(
                $" {ChatColors.Green}{chatPrefix}{ChatColors.Default}: " +
                $"{ChatColors.Green}{ev.Name}{ChatColors.Default} är igång! Skriv !etop för topplistan."
            );
        }

        foreach (var ev in liveEvents.Values.Where(ev => !events.ContainsKey(ev.EventId))) {
            Console.WriteLine($"[INFO] OSBase[{ModuleName}] event ended: id={ev.EventId}, name={ev.Name}");
            Server.PrintToChatAll(
                $" {ChatColors.Green}{chatPrefix}{ChatColors.Default}: " +
                $"{ChatColors.Green}{ev.Name}{ChatColors.Default} är avslutat. Tack för att ni deltog!"
            );
        }

        liveEvents.Clear();
        foreach (var ev in events.Values) {
            liveEvents[ev.EventId] = ev;
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] rules refreshed ({source}): events={liveEvents.Count}, admins={adminSteamIds.Count}");
    }

    // ----- Scoring -----

    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo) {
        if (!isActive || liveEvents.Count == 0) {
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

        string weapon = NormalizeWeapon(eventInfo.Weapon);
        if (weapon.Length == 0) {
            return HookResult.Continue;
        }

        ulong attackerSteamId64 = attacker.SteamID;
        ulong victimSteamId64 = victim.SteamID;

        if (attackerSteamId64 == 0 || victimSteamId64 == 0) {
            return HookResult.Continue;
        }

        bool victimIsAdmin = adminSteamIds.Contains(victimSteamId64);
        bool attackerIsAdmin = adminSteamIds.Contains(attackerSteamId64);

        // One kill can score in several concurrently active events; usually there is one.
        var matches = new List<(LiveEvent Event, string RuleWeapon, int Points)>();
        foreach (var ev in liveEvents.Values) {
            if (MatchWeapon(ev, weapon) is (string ruleWeapon, (int player, int admin))) {
                int rulePoints = victimIsAdmin ? admin : player;
                if (rulePoints != 0) {
                    matches.Add((ev, ruleWeapon, rulePoints));
                }
            }
        }

        if (matches.Count == 0) {
            return HookResult.Continue;
        }

        int activeHumans = CountActiveHumans();
        if (activeHumans < minimumPlayers) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Kill ignored: only {activeHumans}/{minimumPlayers} active human players in T/CT.");
            return HookResult.Continue;
        }

        bool teamKill = attacker.TeamNum == victim.TeamNum;
        string attackerName = CleanName(attacker.PlayerName);
        string victimName = CleanName(victim.PlayerName);

        int totalPoints = 0;
        foreach (var (ev, _, points) in matches) {
            totalPoints += points;

            if (teamKill) {
                // Teamkill penalty: the victim gets the points, the attacker loses them.
                AddScore(ev.EventId, victimSteamId64, victimName, points, 0);
                AddScore(ev.EventId, attackerSteamId64, attackerName, -points, 0);
            } else {
                AddScore(ev.EventId, attackerSteamId64, attackerName, points, 1);
            }
        }

        PrintScoreMessage(attackerName, attackerIsAdmin, victimName, victimIsAdmin, totalPoints, teamKill, matches[0].RuleWeapon);

        return HookResult.Continue;
    }

    private static (string RuleWeapon, (int Player, int Admin) Points)? MatchWeapon(LiveEvent ev, string weapon) {
        if (ev.Weapons.TryGetValue(weapon, out var points)) {
            return (weapon, points);
        }

        // Map specific knife models onto a plain "knife" rule.
        if (ev.Weapons.TryGetValue("knife", out var knifePoints) && IsKnife(weapon)) {
            return ("knife", knifePoints);
        }

        return null;
    }

    private static bool IsKnife(string weapon) {
        return KnifeKeywords.Any(keyword => weapon.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeWeapon(string? weapon) {
        string normalized = (weapon ?? string.Empty).Trim().ToLowerInvariant();

        if (normalized.StartsWith("weapon_", StringComparison.Ordinal)) {
            normalized = normalized.Substring("weapon_".Length);
        }

        return normalized;
    }

    private void AddScore(int eventId, ulong steamId64, string name, int points, int kills) {
        if (steamId64 == 0 || (points == 0 && kills == 0)) {
            return;
        }

        if (!pendingScores.TryGetValue((eventId, steamId64), out var pending)) {
            pending = new PendingScore();
            pendingScores[(eventId, steamId64)] = pending;
        }

        pending.Name = name;
        pending.Points += points;
        pending.Kills += kills;
    }

    // Hands the pending deltas to a background task; rows the database does not
    // confirm are merged back and retried on a later flush, so a temporary outage
    // only delays the points instead of dropping them.
    private void FlushPendingWrites(string source) {
        var database = db;
        if (database == null || flushInProgress || pendingScores.Count == 0) {
            return;
        }

        var batch = pendingScores.Where(kv => kv.Value.Points != 0 || kv.Value.Kills != 0).ToList();
        pendingScores.Clear();

        if (batch.Count == 0) {
            return;
        }

        flushInProgress = true;

        Task.Run(() => {
            var unwritten = new List<KeyValuePair<(int EventId, ulong SteamId64), PendingScore>>();
            bool dbDown = false;

            foreach (var kv in batch) {
                if (dbDown) {
                    unwritten.Add(kv);
                    continue;
                }

                var (eventId, steamId64) = kv.Key;
                var pending = kv.Value;

                // Every batched row has a nonzero delta, so a confirmed upsert always
                // affects at least one row; 0 means insert logged a failure.
                int affected = database.insert(
                    $"INTO {ScoresTable} (event_id, steamid64, name, points, kills) " +
                    "VALUES (@event_id, @steamid64, @name, @points, @kills) " +
                    "ON DUPLICATE KEY UPDATE name=@name, points=points+@points, kills=kills+@kills",
                    new MySqlParameter("@event_id", eventId),
                    new MySqlParameter("@steamid64", steamId64),
                    new MySqlParameter("@name", pending.Name),
                    new MySqlParameter("@points", pending.Points),
                    new MySqlParameter("@kills", pending.Kills)
                );

                if (affected == 0) {
                    // Assume the DB is down and keep the rest cached instead of
                    // stalling on a connect timeout per row.
                    dbDown = true;
                    unwritten.Add(kv);
                }
            }

            Server.NextFrame(() => {
                flushInProgress = false;

                foreach (var kv in unwritten) {
                    MergeScore(kv.Key, kv.Value);
                }

                if (unwritten.Count > 0) {
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}] database unavailable ({source}): kept {unwritten.Count} score rows cached for retry.");
                } else {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] flushed pending DB writes ({source}): rows={batch.Count}");
                }
            });
        });
    }

    // Merge a returned batch entry back into the live deltas; a newer entry for the
    // same player keeps its (fresher) name.
    private void MergeScore((int EventId, ulong SteamId64) key, PendingScore score) {
        if (pendingScores.TryGetValue(key, out var existing)) {
            existing.Points += score.Points;
            existing.Kills += score.Kills;
        } else {
            pendingScores[key] = score;
        }
    }

    // ----- Round / map hooks -----

    private void OnMapStart(string mapName) {
        if (!isActive) {
            return;
        }

        FlushPendingWrites("MapStart");
        RefreshRules("MapStart");
    }

    private HookResult OnRoundEnd(EventRoundEnd _) {
        if (!isActive) {
            return HookResult.Continue;
        }

        FlushPendingWrites("RoundEnd");
        return HookResult.Continue;
    }

    // ----- Leaderboard -----

    private void OnEventTopCommand(CCSPlayerController? player, CommandInfo commandInfo) {
        if (!isActive || player == null || !player.IsValid) {
            return;
        }

        if (liveEvents.Count == 0) {
            player.PrintToChat($" {ChatColors.Green}{chatPrefix}{ChatColors.Default}: Inget event pågår just nu.");
            return;
        }

        foreach (var ev in liveEvents.Values) {
            ShowTopList(player, ev);
        }
    }

    private void ShowTopList(CCSPlayerController player, LiveEvent ev) {
        if (db == null) {
            player.PrintToChat($" {ChatColors.Red}{chatPrefix}: Database unavailable.{ChatColors.Default}");
            return;
        }

        try {
            DataTable table = db.select(
                $"name, steamid64, points FROM {ScoresTable} WHERE event_id=@event_id ORDER BY points DESC LIMIT @limit",
                new MySqlParameter("@event_id", ev.EventId),
                new MySqlParameter("@limit", topLimit)
            );

            player.PrintToChat($" {ChatColors.Green}{chatPrefix}: {ev.Name} leaderboard:{ChatColors.Default}");

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

    // ----- Chat output -----

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

    // ----- Helpers -----

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
