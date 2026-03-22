using System;
using System.Collections.Generic;
using System.Data;
using System.Net.Http;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using MySqlConnector;
using OSBase.Helpers;

namespace OSBase.Modules;

public class Faceit : IModule {
    public string ModuleName => "faceit";

    private OSBase? osbase;
    private Config? config;
    private Database? db;
    private HttpClient? httpClient;

    // cfg (faceit.cfg)
    private string apiKey = "";
    private string adminPermission = "@css/generic";
    private bool notifyAdminsOnConnect = true;
    private bool onlyNotifyOnBan = false;
    private int httpTimeoutSeconds = 5;
    private int cleanupAfterDays = 365;
    private bool debug = false;

    // cache levels
    private readonly int[] cacheDays = { 3, 7, 14, 30, 60, 120 };

    // in-memory queue
    private readonly Queue<ulong> lookupQueue = new();
    private readonly HashSet<ulong> queuedSteamIds = new();
    private bool workerBusy = false;
    private CounterStrikeSharp.API.Modules.Timers.Timer? workerTimer;

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        config.RegisterGlobalConfigValue(ModuleName, "0");
        if (config.GetGlobalConfigValue(ModuleName, "0") != "1")
            return;

        CreateModuleConfig();
        LoadModuleConfig();

        db = new Database(osbase, config);
        db.Initialize();

        httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, httpTimeoutSeconds));

        EnsureFaceitCacheTable();
        CleanupOldCacheRows();
        StartWorker();

        osbase.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        osbase.RegisterEventHandler<EventMapTransition>((_, __) => {
            StopWorker();
            ClearQueue();
            return HookResult.Continue;
        });

        Console.WriteLine("[DEBUG] OSBase[faceit]: loaded successfully!");
    }

    private void CreateModuleConfig() {
        config?.CreateCustomConfig("faceit.cfg",
            "// Faceit module\n" +
            "api_key \n" +
            "admin_permission @css/generic\n" +
            "notify_admins_on_connect 1\n" +
            "only_notify_on_ban 0\n" +
            "http_timeout_seconds 5\n" +
            "cleanup_after_days 365\n" +
            "debug 0\n"
        );
    }

    private void LoadModuleConfig() {
        foreach (var line in config?.FetchCustomConfig("faceit.cfg") ?? new List<string>()) {
            var s = line.Trim();
            if (s.Length == 0 || s.StartsWith("//"))
                continue;

            var kv = s.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2)
                continue;

            switch (kv[0].ToLowerInvariant()) {
                case "api_key":
                    apiKey = kv[1];
                    break;
                case "admin_permission":
                    adminPermission = kv[1];
                    break;
                case "notify_admins_on_connect":
                    notifyAdminsOnConnect = kv[1] == "1";
                    break;
                case "only_notify_on_ban":
                    onlyNotifyOnBan = kv[1] == "1";
                    break;
                case "http_timeout_seconds":
                    if (int.TryParse(kv[1], out var timeout))
                        httpTimeoutSeconds = Math.Max(1, timeout);
                    break;
                case "cleanup_after_days":
                    if (int.TryParse(kv[1], out var cleanup))
                        cleanupAfterDays = Math.Max(30, cleanup);
                    break;
                case "debug":
                    debug = kv[1] == "1";
                    break;
            }
        }
    }

    private void EnsureFaceitCacheTable() {
        if (db == null) {
            Console.WriteLine("[ERROR] OSBase[faceit]: Database is null in EnsureFaceitCacheTable");
            return;
        }

        string query = @"
            TABLE IF NOT EXISTS `faceit_cache` (
                `steamid64` BIGINT UNSIGNED NOT NULL,
                `faceit_player_id` VARCHAR(64) DEFAULT NULL,
                `faceit_nickname` VARCHAR(64) DEFAULT NULL,
                `skill_level` INT DEFAULT NULL,
                `faceit_elo` INT DEFAULT NULL,
                `region` VARCHAR(32) DEFAULT NULL,
                `country` VARCHAR(8) DEFAULT NULL,
                `verified` TINYINT(1) NOT NULL DEFAULT 0,
                `has_faceit_account` TINYINT(1) NOT NULL DEFAULT 0,
                `active_ban` TINYINT(1) NOT NULL DEFAULT 0,
                `cache_level` INT NOT NULL DEFAULT 0,
                `last_seen_at` DATETIME DEFAULT NULL,
                `last_checked_at` DATETIME DEFAULT NULL,
                `next_check_at` DATETIME DEFAULT NULL,
                `status` VARCHAR(32) NOT NULL DEFAULT 'pending',
                `last_error` VARCHAR(255) DEFAULT NULL,
                `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `updated_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (`steamid64`),
                KEY `idx_next_check_at` (`next_check_at`),
                KEY `idx_last_seen_at` (`last_seen_at`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci";

        int result = db.create(query);

        if (debug)
            Console.WriteLine($"[DEBUG] OSBase[faceit]: EnsureFaceitCacheTable result={result}");
    }

    private void CleanupOldCacheRows() {
        if (db == null)
            return;

        string query = @"
            FROM `faceit_cache`
            WHERE `last_seen_at` IS NOT NULL
              AND `last_seen_at` < UTC_TIMESTAMP() - INTERVAL @days DAY";

        int result = db.delete(query, new MySqlParameter("@days", cleanupAfterDays));

        if (debug)
            Console.WriteLine($"[DEBUG] OSBase[faceit]: CleanupOldCacheRows result={result}");
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info) {
        try {
            var player = @event.Userid;
            if (!IsValidHuman(player))
                return HookResult.Continue;

            ulong steamId64 = player!.SteamID;
            if (steamId64 == 0)
                return HookResult.Continue;

            HandlePlayerConnect(player, steamId64);
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[faceit]: OnPlayerConnectFull failed: {ex.Message}");
        }

        return HookResult.Continue;
    }

    private void HandlePlayerConnect(CCSPlayerController player, ulong steamId64) {
        var row = GetCacheRow(steamId64);

        if (row == null) {
            InsertPendingRow(steamId64);

            if (debug)
                Console.WriteLine($"[DEBUG] OSBase[faceit]: inserted pending row for {steamId64}");

            EnqueueLookup(steamId64);
            return;
        }

        UpdateLastSeen(steamId64);

        if (ShouldEnqueueLookup(row)) {
            if (debug)
                Console.WriteLine($"[DEBUG] OSBase[faceit]: stale cache for {steamId64}, queueing lookup");

            EnqueueLookup(steamId64);
            return;
        }

        if (debug)
            Console.WriteLine($"[DEBUG] OSBase[faceit]: cache still fresh for {steamId64}");

        if (notifyAdminsOnConnect)
            NotifyAdminsFromCache(player, row);
    }

    private DataRow? GetCacheRow(ulong steamId64) {
        if (db == null)
            return null;

        string query = @"
            * FROM `faceit_cache`
            WHERE `steamid64` = @steamid64
            LIMIT 1";

        var table = db.select(query, new MySqlParameter("@steamid64", steamId64));
        if (table.Rows.Count == 0)
            return null;

        return table.Rows[0];
    }

    private void InsertPendingRow(ulong steamId64) {
        if (db == null)
            return;

        string query = @"
            INTO `faceit_cache`
            (`steamid64`, `has_faceit_account`, `active_ban`, `cache_level`, `last_seen_at`, `next_check_at`, `status`)
            VALUES
            (@steamid64, 0, 0, 0, UTC_TIMESTAMP(), UTC_TIMESTAMP(), 'pending')
            ON DUPLICATE KEY UPDATE
                `last_seen_at` = UTC_TIMESTAMP()";

        db.insert(query, new MySqlParameter("@steamid64", steamId64));
    }

    private void UpdateLastSeen(ulong steamId64) {
        if (db == null)
            return;

        string query = @"
            `faceit_cache`
            SET `last_seen_at` = UTC_TIMESTAMP()
            WHERE `steamid64` = @steamid64";

        db.update(query, new MySqlParameter("@steamid64", steamId64));
    }

    private bool ShouldEnqueueLookup(DataRow row) {
        if (row["next_check_at"] == DBNull.Value)
            return true;

        if (!DateTime.TryParse(row["next_check_at"].ToString(), out var nextCheckAt))
            return true;

        return nextCheckAt <= DateTime.UtcNow;
    }

    private void EnqueueLookup(ulong steamId64) {
        if (queuedSteamIds.Contains(steamId64)) {
            if (debug)
                Console.WriteLine($"[DEBUG] OSBase[faceit]: already queued {steamId64}");
            return;
        }

        lookupQueue.Enqueue(steamId64);
        queuedSteamIds.Add(steamId64);

        Console.WriteLine($"[DEBUG] OSBase[faceit]: queued {steamId64}, queueCount={lookupQueue.Count}");
    }

    private void StartWorker() {
        Console.WriteLine("[DEBUG] OSBase[faceit]: StartWorker called");
        StopWorker();

        workerTimer = osbase?.AddTimer(
            2.0f,
            WorkerTick,
            TimerFlags.STOP_ON_MAPCHANGE
        );
    }

    private void StopWorker() {
        workerTimer?.Kill();
        workerTimer = null;
    }

    private void WorkerTick() {
        Console.WriteLine($"[DEBUG] OSBase[faceit]: WorkerTick fired, busy={workerBusy}, queue={lookupQueue.Count}");

        try {
            if (!workerBusy && lookupQueue.Count > 0) {
                ulong steamId64 = lookupQueue.Dequeue();
                queuedSteamIds.Remove(steamId64);

                Console.WriteLine($"[DEBUG] OSBase[faceit]: dequeued {steamId64}");

                _ = ProcessLookupAsync(steamId64);
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[faceit]: WorkerTick failed: {ex.Message}");
        } finally {
            workerTimer = osbase?.AddTimer(
                2.0f,
                WorkerTick,
                TimerFlags.STOP_ON_MAPCHANGE
            );
        }
    }

    private async Task ProcessLookupAsync(ulong steamId64) {
        Console.WriteLine($"[DEBUG] OSBase[faceit]: ProcessLookupAsync started for {steamId64}");
        workerBusy = true;

        try {
            if (debug)
                Console.WriteLine($"[DEBUG] OSBase[faceit]: processing lookup for {steamId64}");

            var result = await FetchFaceitDataAsync(steamId64);
            ApplyLookupResult(steamId64, result);
            NotifyAdminsIfRelevant(steamId64, result);
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[faceit]: ProcessLookupAsync failed for {steamId64}: {ex.Message}");
            ApplyTemporaryError(steamId64, ex.Message);
        } finally {
            workerBusy = false;
        }
    }

    private async Task<FaceitLookupResult> FetchFaceitDataAsync(ulong steamId64) {
        await Task.Yield();

        // TODO:
        // 1. Lookup player by SteamID64 / game_player_id
        // 2. If found, lookup bans
        // 3. Map response to FaceitLookupResult
        // 4. Return not_found if no account exists

        return new FaceitLookupResult {
            SteamId64 = steamId64,
            HasFaceitAccount = false,
            ActiveBan = false,
            Status = "not_found"
        };
    }

    private void ApplyLookupResult(ulong steamId64, FaceitLookupResult result) {
        Console.WriteLine($"[DEBUG] OSBase[faceit]: ApplyLookupResult for {steamId64}, status={result.Status}");
        if (db == null)
            return;

        var existingRow = GetCacheRow(steamId64);
        int currentLevel = 0;

        if (existingRow != null && existingRow["cache_level"] != DBNull.Value)
            int.TryParse(existingRow["cache_level"].ToString(), out currentLevel);

        bool changed = HasMeaningfulChanges(existingRow, result);
        int nextLevel = changed ? 0 : GetNextCacheLevel(currentLevel);
        DateTime nextCheckAt = GetNextCheckAt(nextLevel);

        string query = @"
            `faceit_cache`
            SET
                `faceit_player_id` = @faceit_player_id,
                `faceit_nickname` = @faceit_nickname,
                `skill_level` = @skill_level,
                `faceit_elo` = @faceit_elo,
                `region` = @region,
                `country` = @country,
                `verified` = @verified,
                `has_faceit_account` = @has_faceit_account,
                `active_ban` = @active_ban,
                `cache_level` = @cache_level,
                `last_checked_at` = UTC_TIMESTAMP(),
                `next_check_at` = @next_check_at,
                `status` = @status,
                `last_error` = NULL
            WHERE `steamid64` = @steamid64";

        db.update(query,
            new MySqlParameter("@faceit_player_id", (object?)result.FaceitPlayerId ?? DBNull.Value),
            new MySqlParameter("@faceit_nickname", (object?)result.FaceitNickname ?? DBNull.Value),
            new MySqlParameter("@skill_level", (object?)result.SkillLevel ?? DBNull.Value),
            new MySqlParameter("@faceit_elo", (object?)result.FaceitElo ?? DBNull.Value),
            new MySqlParameter("@region", (object?)result.Region ?? DBNull.Value),
            new MySqlParameter("@country", (object?)result.Country ?? DBNull.Value),
            new MySqlParameter("@verified", result.Verified ? 1 : 0),
            new MySqlParameter("@has_faceit_account", result.HasFaceitAccount ? 1 : 0),
            new MySqlParameter("@active_ban", result.ActiveBan ? 1 : 0),
            new MySqlParameter("@cache_level", nextLevel),
            new MySqlParameter("@next_check_at", nextCheckAt),
            new MySqlParameter("@status", result.Status),
            new MySqlParameter("@steamid64", steamId64)
        );

        if (debug)
            Console.WriteLine($"[DEBUG] OSBase[faceit]: updated cache for {steamId64}, changed={changed}, nextLevel={nextLevel}");
    }

    private void ApplyTemporaryError(ulong steamId64, string errorMessage) {
        if (db == null)
            return;

        DateTime retryAt = DateTime.UtcNow.AddHours(1);

        string query = @"
            `faceit_cache`
            SET
                `last_checked_at` = UTC_TIMESTAMP(),
                `next_check_at` = @next_check_at,
                `status` = 'error',
                `last_error` = @last_error
            WHERE `steamid64` = @steamid64";

        db.update(query,
            new MySqlParameter("@next_check_at", retryAt),
            new MySqlParameter("@last_error", errorMessage),
            new MySqlParameter("@steamid64", steamId64)
        );
    }

    private bool HasMeaningfulChanges(DataRow? row, FaceitLookupResult result) {
        if (row == null)
            return true;

        string oldPlayerId = row["faceit_player_id"] == DBNull.Value ? "" : row["faceit_player_id"].ToString() ?? "";
        string oldNickname = row["faceit_nickname"] == DBNull.Value ? "" : row["faceit_nickname"].ToString() ?? "";
        int oldSkill = row["skill_level"] == DBNull.Value ? -1 : Convert.ToInt32(row["skill_level"]);
        int oldElo = row["faceit_elo"] == DBNull.Value ? -1 : Convert.ToInt32(row["faceit_elo"]);
        bool oldBan = row["active_ban"] != DBNull.Value && Convert.ToInt32(row["active_ban"]) == 1;
        bool oldHasAccount = row["has_faceit_account"] != DBNull.Value && Convert.ToInt32(row["has_faceit_account"]) == 1;

        if (oldPlayerId != (result.FaceitPlayerId ?? ""))
            return true;
        if (oldNickname != (result.FaceitNickname ?? ""))
            return true;
        if (oldSkill != (result.SkillLevel ?? -1))
            return true;
        if (oldElo != (result.FaceitElo ?? -1))
            return true;
        if (oldBan != result.ActiveBan)
            return true;
        if (oldHasAccount != result.HasFaceitAccount)
            return true;

        return false;
    }

    private int GetNextCacheLevel(int currentLevel) {
        if (currentLevel < 0)
            return 0;

        if (currentLevel >= cacheDays.Length - 1)
            return cacheDays.Length - 1;

        return currentLevel + 1;
    }

    private DateTime GetNextCheckAt(int cacheLevel) {
        int safeLevel = Math.Clamp(cacheLevel, 0, cacheDays.Length - 1);
        return DateTime.UtcNow.AddDays(cacheDays[safeLevel]);
    }

    private void NotifyAdminsFromCache(CCSPlayerController player, DataRow row) {
        if (!notifyAdminsOnConnect)
            return;

        bool hasFaceit = row["has_faceit_account"] != DBNull.Value && Convert.ToInt32(row["has_faceit_account"]) == 1;
        bool activeBan = row["active_ban"] != DBNull.Value && Convert.ToInt32(row["active_ban"]) == 1;

        if (!hasFaceit)
            return;

        if (onlyNotifyOnBan && !activeBan)
            return;

        string nickname = row["faceit_nickname"] == DBNull.Value ? player.PlayerName : row["faceit_nickname"].ToString() ?? player.PlayerName;
        string level = row["skill_level"] == DBNull.Value ? "?" : row["skill_level"].ToString() ?? "?";
        string elo = row["faceit_elo"] == DBNull.Value ? "?" : row["faceit_elo"].ToString() ?? "?";

        string message = activeBan
            ? $"{player.PlayerName} | FACEIT {nickname} | lvl {level} | elo {elo} | ACTIVE BAN"
            : $"{player.PlayerName} | FACEIT {nickname} | lvl {level} | elo {elo}";

        ChatHelper.PrintToAdmins(message, adminPermission, " \x08[FACEIT]\x01 ");
    }

    private void NotifyAdminsIfRelevant(ulong steamId64, FaceitLookupResult result) {
        if (!notifyAdminsOnConnect)
            return;

        if (!result.HasFaceitAccount)
            return;

        if (onlyNotifyOnBan && !result.ActiveBan)
            return;

        var player = FindOnlinePlayer(steamId64);
        if (!IsValidHuman(player))
            return;

        string message = result.ActiveBan
            ? $"{player!.PlayerName} | FACEIT {result.FaceitNickname ?? "unknown"} | lvl {result.SkillLevel?.ToString() ?? "?"} | elo {result.FaceitElo?.ToString() ?? "?"} | ACTIVE BAN"
            : $"{player!.PlayerName} | FACEIT {result.FaceitNickname ?? "unknown"} | lvl {result.SkillLevel?.ToString() ?? "?"} | elo {result.FaceitElo?.ToString() ?? "?"}";

        ChatHelper.PrintToAdmins(message, adminPermission, " \x08[FACEIT]\x01 ");
    }

    private CCSPlayerController? FindOnlinePlayer(ulong steamId64) {
        foreach (var player in Utilities.GetPlayers()) {
            if (!IsValidHuman(player))
                continue;

            if (player!.SteamID == steamId64)
                return player;
        }

        return null;
    }

    private bool IsValidHuman(CCSPlayerController? player) {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
            return false;

        if (player.Connected != PlayerConnectedState.PlayerConnected)
            return false;

        return true;
    }

    private void ClearQueue() {
        lookupQueue.Clear();
        queuedSteamIds.Clear();
        workerBusy = false;
    }

    private class FaceitLookupResult {
        public ulong SteamId64 { get; set; }
        public string? FaceitPlayerId { get; set; }
        public string? FaceitNickname { get; set; }
        public int? SkillLevel { get; set; }
        public int? FaceitElo { get; set; }
        public string? Region { get; set; }
        public string? Country { get; set; }
        public bool Verified { get; set; }
        public bool HasFaceitAccount { get; set; }
        public bool ActiveBan { get; set; }
        public string Status { get; set; } = "pending";
    }
}