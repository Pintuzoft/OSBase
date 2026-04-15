using System;
using System.Collections.Generic;
using System.Data;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using MySqlConnector;
using OSBase.Helpers;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace OSBase.Modules;

public class Faceit : IModule {
    public string ModuleName => "faceit";

    private const string ChatPrefix = " \x08[OSBase(Admin)]\x01 ";

    private OSBase? osbase;
    private Config? config;
    private Database? db;
    private HttpClient? httpClient;

    private bool handlersLoaded = false;
    private bool isActive = false;

    // cfg (faceit.cfg)
    private string apiKey = "";
    private string adminPermission = "@css/generic";
    private bool notifyAdminsOnConnect = true;
    private bool onlyNotifyOnBan = false;
    private int httpTimeoutSeconds = 5;
    private int cleanupAfterDays = 365;
    private bool debug = false;

    // cache levels
    private readonly int[] cacheDays = { 3, 6, 9, 12, 15, 18, 21, 24, 27, 30 };

    // in-memory queue
    private readonly Queue<ulong> lookupQueue = new();
    private readonly HashSet<ulong> queuedSteamIds = new();
    private bool workerBusy = false;
    private Timer? workerTimer;

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;
        isActive = true;

        if (osbase == null || config == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] load failed (null deps).");
            isActive = false;
            return;
        }

        config.RegisterGlobalConfigValue(ModuleName, "0");
        if (config.GetGlobalConfigValue(ModuleName, "0") != "1") {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in config.");
            isActive = false;
            return;
        }

        CreateModuleConfig();
        LoadModuleConfig();

        db = new Database(osbase, config);
        db.Initialize();

        httpClient = new HttpClient {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, httpTimeoutSeconds))
        };

        EnsureFaceitCacheTable();
        CleanupOldCacheRows();
        LoadHandlers();

        if (debug) {
            Console.WriteLine("[DEBUG] OSBase[faceit]: loaded successfully!");
            Console.WriteLine($"[DEBUG] OSBase[faceit]: api key loaded={!string.IsNullOrWhiteSpace(apiKey)}, len={apiKey.Length}");
            Console.WriteLine($"[DEBUG] OSBase[faceit]: admin_permission={adminPermission}, timeout={httpTimeoutSeconds}, cleanup_after_days={cleanupAfterDays}");
        }
    }

    public void Unload() {
        isActive = false;

        StopWorker();
        ClearQueue();

        if (osbase != null && handlersLoaded) {
            osbase.DeregisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
            osbase.DeregisterEventHandler<EventMapTransition>(OnMapTransition);
            handlersLoaded = false;
        }

        httpClient?.Dispose();
        httpClient = null;

        db = null;
        config = null;
        osbase = null;

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] unloaded.");
    }

    public void ReloadConfig(Config inConfig) {
        config = inConfig;

        CreateModuleConfig();
        LoadModuleConfig();

        if (httpClient != null) {
            httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, httpTimeoutSeconds));
        }

        CleanupOldCacheRows();

        if (debug) {
            Console.WriteLine($"[DEBUG] OSBase[faceit]: config reloaded. timeout={httpTimeoutSeconds}, cleanup_after_days={cleanupAfterDays}, notify_admins_on_connect={notifyAdminsOnConnect}, only_notify_on_ban={onlyNotifyOnBan}");
        }
    }

    private void LoadHandlers() {
        if (osbase == null || handlersLoaded) {
            return;
        }

        osbase.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        osbase.RegisterEventHandler<EventMapTransition>(OnMapTransition);

        handlersLoaded = true;
    }

    private HookResult OnMapTransition(EventMapTransition _, GameEventInfo __) {
        StopWorker();
        ClearQueue();
        return HookResult.Continue;
    }

    private void CreateModuleConfig() {
        config?.CreateCustomConfig(
            "faceit.cfg",
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
        apiKey = "";
        adminPermission = "@css/generic";
        notifyAdminsOnConnect = true;
        onlyNotifyOnBan = false;
        httpTimeoutSeconds = 5;
        cleanupAfterDays = 365;
        debug = false;

        foreach (var line in config?.FetchCustomConfig("faceit.cfg") ?? new List<string>()) {
            var s = line.Trim();
            if (s.Length == 0 || s.StartsWith("//")) {
                continue;
            }

            var kv = s.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) {
                continue;
            }

            switch (kv[0].ToLowerInvariant()) {
                case "api_key":
                    apiKey = kv[1].Trim();
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
                    if (int.TryParse(kv[1], out var timeout)) {
                        httpTimeoutSeconds = Math.Max(1, timeout);
                    }
                    break;
                case "cleanup_after_days":
                    if (int.TryParse(kv[1], out var cleanup)) {
                        cleanupAfterDays = Math.Max(30, cleanup);
                    }
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
            CREATE TABLE IF NOT EXISTS `faceit_cache` (
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
                `ban_reason` VARCHAR(128) DEFAULT NULL,
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

        if (debug) {
            Console.WriteLine($"[DEBUG] OSBase[faceit]: EnsureFaceitCacheTable result={result}");
        }
    }

    private void CleanupOldCacheRows() {
        if (db == null) {
            return;
        }

        string query = @"
            DELETE FROM `faceit_cache`
            WHERE `last_seen_at` IS NOT NULL
              AND `last_seen_at` < UTC_TIMESTAMP() - INTERVAL @days DAY";

        int result = db.delete(query, new MySqlParameter("@days", cleanupAfterDays));

        if (debug) {
            Console.WriteLine($"[DEBUG] OSBase[faceit]: CleanupOldCacheRows result={result}");
        }
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info) {
        if (!isActive) {
            return HookResult.Continue;
        }

        try {
            var player = @event.Userid;
            if (!IsValidHuman(player)) {
                return HookResult.Continue;
            }

            ulong steamId64 = player!.SteamID;
            if (steamId64 == 0) {
                return HookResult.Continue;
            }

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

            if (debug) {
                Console.WriteLine($"[DEBUG] OSBase[faceit]: inserted pending row for {steamId64}");
            }

            EnqueueLookup(steamId64);
            return;
        }

        UpdateLastSeen(steamId64);

        if (ShouldEnqueueLookup(row)) {
            if (debug) {
                Console.WriteLine($"[DEBUG] OSBase[faceit]: stale cache for {steamId64}, queueing lookup");
            }

            EnqueueLookup(steamId64);
            return;
        }

        if (debug) {
            Console.WriteLine($"[DEBUG] OSBase[faceit]: cache still fresh for {steamId64}");
        }

        if (notifyAdminsOnConnect) {
            NotifyAdminsFromCache(player, row);
        }
    }

    private DataRow? GetCacheRow(ulong steamId64) {
        if (db == null) {
            return null;
        }

        string query = @"
            SELECT * FROM `faceit_cache`
            WHERE `steamid64` = @steamid64
            LIMIT 1";

        var table = db.select(query, new MySqlParameter("@steamid64", steamId64));
        if (table.Rows.Count == 0) {
            return null;
        }

        return table.Rows[0];
    }

    private void InsertPendingRow(ulong steamId64) {
        if (db == null) {
            return;
        }

        string query = @"
            INSERT INTO `faceit_cache`
            (`steamid64`, `has_faceit_account`, `active_ban`, `cache_level`, `last_seen_at`, `next_check_at`, `status`)
            VALUES
            (@steamid64, 0, 0, 0, UTC_TIMESTAMP(), UTC_TIMESTAMP(), 'pending')
            ON DUPLICATE KEY UPDATE
                `last_seen_at` = UTC_TIMESTAMP()";

        db.insert(query, new MySqlParameter("@steamid64", steamId64));
    }

    private void UpdateLastSeen(ulong steamId64) {
        if (db == null) {
            return;
        }

        string query = @"
            UPDATE `faceit_cache`
            SET `last_seen_at` = UTC_TIMESTAMP()
            WHERE `steamid64` = @steamid64";

        db.update(query, new MySqlParameter("@steamid64", steamId64));
    }

    private bool ShouldEnqueueLookup(DataRow row) {
        if (row["next_check_at"] == DBNull.Value) {
            return true;
        }

        if (!DateTime.TryParse(row["next_check_at"].ToString(), out var nextCheckAt)) {
            return true;
        }

        return nextCheckAt <= DateTime.UtcNow;
    }

    private void EnqueueLookup(ulong steamId64) {
        if (!isActive) {
            return;
        }

        if (queuedSteamIds.Contains(steamId64)) {
            if (debug) {
                Console.WriteLine($"[DEBUG] OSBase[faceit]: already queued {steamId64}");
            }
            return;
        }

        lookupQueue.Enqueue(steamId64);
        queuedSteamIds.Add(steamId64);

        if (debug) {
            Console.WriteLine($"[DEBUG] OSBase[faceit]: queued {steamId64}, queueCount={lookupQueue.Count}");
        }

        if (workerTimer == null && !workerBusy) {
            if (debug) {
                Console.WriteLine("[DEBUG] OSBase[faceit]: worker not running, starting now");
            }

            StartWorker();
        }
    }

    private void StartWorker() {
        if (!isActive || osbase == null) {
            return;
        }

        if (debug) {
            Console.WriteLine("[DEBUG] OSBase[faceit]: StartWorker called");
        }

        StopWorker();

        workerTimer = osbase.AddTimer(
            2.0f,
            () => {
                if (!isActive) {
                    return;
                }

                if (debug) {
                    Console.WriteLine("[DEBUG] OSBase[faceit]: TIMER CALLBACK FIRED");
                }

                WorkerTick();
            },
            TimerFlags.STOP_ON_MAPCHANGE
        );

        if (debug) {
            Console.WriteLine($"[DEBUG] OSBase[faceit]: workerTimer null? {workerTimer == null}");
        }
    }

    private void StopWorker() {
        workerTimer?.Kill();
        workerTimer = null;
    }

    private void WorkerTick() {
        if (!isActive) {
            StopWorker();
            return;
        }

        if (debug) {
            Console.WriteLine($"[DEBUG] OSBase[faceit]: WorkerTick fired, busy={workerBusy}, queue={lookupQueue.Count}");
        }

        try {
            if (workerBusy || lookupQueue.Count == 0) {
                return;
            }

            ulong steamId64 = lookupQueue.Dequeue();
            queuedSteamIds.Remove(steamId64);

            if (debug) {
                Console.WriteLine($"[DEBUG] OSBase[faceit]: dequeued {steamId64}");
            }

            _ = ProcessLookupAsync(steamId64);
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[faceit]: WorkerTick failed: {ex.Message}");
        } finally {
            if (!isActive || osbase == null) {
                StopWorker();
            } else if (lookupQueue.Count > 0 || workerBusy) {
                workerTimer = osbase.AddTimer(
                    2.0f,
                    () => {
                        if (!isActive) {
                            return;
                        }

                        if (debug) {
                            Console.WriteLine("[DEBUG] OSBase[faceit]: TIMER CALLBACK FIRED");
                        }

                        WorkerTick();
                    },
                    TimerFlags.STOP_ON_MAPCHANGE
                );
            } else {
                if (debug) {
                    Console.WriteLine("[DEBUG] OSBase[faceit]: queue empty, stopping worker");
                }

                StopWorker();
            }
        }
    }

    private async Task ProcessLookupAsync(ulong steamId64) {
        if (!isActive) {
            return;
        }

        if (debug) {
            Console.WriteLine($"[DEBUG] OSBase[faceit]: ProcessLookupAsync started for {steamId64}");
        }

        workerBusy = true;

        try {
            if (debug) {
                Console.WriteLine($"[DEBUG] OSBase[faceit]: processing lookup for {steamId64}");
            }

            var result = await FetchFaceitDataAsync(steamId64);
            if (!isActive) {
                return;
            }

            ApplyLookupResult(steamId64, result);

            Server.NextWorldUpdate(() => {
                if (!isActive) {
                    return;
                }

                try {
                    NotifyAdminsIfRelevant(steamId64, result);
                } catch (Exception ex) {
                    Console.WriteLine($"[ERROR] OSBase[faceit]: NotifyAdminsIfRelevant(main thread) failed for {steamId64}: {ex.Message}");
                }
            });
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[faceit]: ProcessLookupAsync failed for {steamId64}: {ex.Message}");

            if (isActive) {
                ApplyTemporaryError(steamId64, ex.Message);
            }
        } finally {
            workerBusy = false;
        }
    }

    private async Task<FaceitLookupResult> FetchFaceitDataAsync(ulong steamId64) {
        if (httpClient == null) {
            throw new InvalidOperationException("HttpClient is null");
        }

        if (string.IsNullOrWhiteSpace(apiKey)) {
            throw new InvalidOperationException("FACEIT api_key is missing in faceit.cfg");
        }

        string url = $"https://open.faceit.com/data/v4/players?game=cs2&game_player_id={steamId64}";

        if (debug) {
            Console.WriteLine($"[DEBUG] OSBase[faceit]: lookup url={url}");
            Console.WriteLine($"[DEBUG] OSBase[faceit]: api key loaded={!string.IsNullOrWhiteSpace(apiKey)}, len={apiKey.Length}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("OSBase-Faceit/1.0");

        using var response = await httpClient.SendAsync(request);

        if (debug) {
            Console.WriteLine($"[DEBUG] OSBase[faceit]: FACEIT lookup status={(int)response.StatusCode} for steamid64={steamId64}");
        }

        if (response.StatusCode == HttpStatusCode.NotFound) {
            string notFoundBody = await response.Content.ReadAsStringAsync();

            if (debug) {
                Console.WriteLine($"[DEBUG] OSBase[faceit]: FACEIT returned 404 for steamid64={steamId64}");
                Console.WriteLine($"[DEBUG] OSBase[faceit]: FACEIT 404 body={notFoundBody}");
            }

            return new FaceitLookupResult {
                SteamId64 = steamId64,
                HasFaceitAccount = false,
                ActiveBan = false,
                BanReason = null,
                Status = "not_found"
            };
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden) {
            throw new Exception($"FACEIT auth failed: {(int)response.StatusCode}");
        }

        if ((int)response.StatusCode == 429) {
            throw new Exception("FACEIT rate limited");
        }

        if (!response.IsSuccessStatusCode) {
            throw new Exception($"FACEIT lookup failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        string body = await response.Content.ReadAsStringAsync();

        var options = new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true
        };

        var player = JsonSerializer.Deserialize<FaceitPlayerResponse>(body, options);
        if (player == null) {
            return new FaceitLookupResult {
                SteamId64 = steamId64,
                HasFaceitAccount = false,
                ActiveBan = false,
                BanReason = null,
                Status = "not_found"
            };
        }

        FaceitGameData? cs2 = null;
        if (player.Games != null) {
            player.Games.TryGetValue("cs2", out cs2);
        }

        if (cs2 == null) {
            return new FaceitLookupResult {
                SteamId64 = steamId64,
                HasFaceitAccount = false,
                ActiveBan = false,
                BanReason = null,
                Status = "not_found"
            };
        }

        var result = new FaceitLookupResult {
            SteamId64 = steamId64,
            FaceitPlayerId = player.PlayerId,
            FaceitNickname = player.Nickname,
            SkillLevel = cs2.SkillLevel,
            FaceitElo = cs2.FaceitElo,
            Region = cs2.Region,
            Country = player.Country,
            Verified = player.Verified,
            HasFaceitAccount = true,
            ActiveBan = false,
            BanReason = null,
            Status = "ok"
        };

        if (!string.IsNullOrWhiteSpace(player.PlayerId)) {
            try {
                await PopulateBanInfoAsync(result, player.PlayerId!);
            } catch (Exception ex) {
                if (debug) {
                    Console.WriteLine($"[DEBUG] OSBase[faceit]: bans lookup failed for {steamId64}: {ex.Message}");
                }
            }
        }

        return result;
    }

    private async Task PopulateBanInfoAsync(FaceitLookupResult result, string faceitPlayerId) {
        if (httpClient == null) {
            return;
        }

        string url = $"https://open.faceit.com/data/v4/players/{faceitPlayerId}/bans";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("OSBase-Faceit/1.0");

        using var response = await httpClient.SendAsync(request);

        if (debug) {
            Console.WriteLine($"[DEBUG] OSBase[faceit]: FACEIT bans status={(int)response.StatusCode} for player_id={faceitPlayerId}");
        }

        if (response.StatusCode == HttpStatusCode.NotFound) {
            return;
        }

        if (!response.IsSuccessStatusCode) {
            return;
        }

        string body = await response.Content.ReadAsStringAsync();

        var options = new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true
        };

        var bans = JsonSerializer.Deserialize<FaceitBansResponse>(body, options);
        if (bans?.Items == null || bans.Items.Count == 0) {
            return;
        }

        foreach (var ban in bans.Items) {
            if (!IsBanActive(ban)) {
                continue;
            }

            result.ActiveBan = true;
            result.BanReason =
                !string.IsNullOrWhiteSpace(ban.Reason) ? ban.Reason :
                !string.IsNullOrWhiteSpace(ban.EntityType) ? ban.EntityType :
                "unknown";

            return;
        }
    }

    private bool IsBanActive(FaceitBanItem? ban) {
        if (ban == null) {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ban.Status)) {
            string status = ban.Status.Trim().ToLowerInvariant();
            if (status == "active") {
                return true;
            }

            if (status == "expired" || status == "inactive") {
                return false;
            }
        }

        if (ban.ExpiresAt.HasValue) {
            return ban.ExpiresAt.Value > DateTime.UtcNow;
        }

        return true;
    }

    private void ApplyLookupResult(ulong steamId64, FaceitLookupResult result) {
        if (debug) {
            Console.WriteLine($"[DEBUG] OSBase[faceit]: ApplyLookupResult for {steamId64}, status={result.Status}");
        }

        if (db == null) {
            return;
        }

        var existingRow = GetCacheRow(steamId64);
        int currentLevel = 0;

        if (existingRow != null && existingRow["cache_level"] != DBNull.Value) {
            int.TryParse(existingRow["cache_level"].ToString(), out currentLevel);
        }

        bool changed = HasMeaningfulChanges(existingRow, result);

        int appliedLevel;
        int storedLevel;

        if (changed) {
            appliedLevel = 0;
            storedLevel = 0;
        } else {
            appliedLevel = currentLevel;
            storedLevel = GetNextCacheLevel(currentLevel);
        }

        DateTime nextCheckAt = GetNextCheckAt(appliedLevel);

        string query = @"
            UPDATE `faceit_cache`
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
                `ban_reason` = @ban_reason,
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
            new MySqlParameter("@ban_reason", (object?)result.BanReason ?? DBNull.Value),
            new MySqlParameter("@cache_level", storedLevel),
            new MySqlParameter("@next_check_at", nextCheckAt),
            new MySqlParameter("@status", result.Status),
            new MySqlParameter("@steamid64", steamId64)
        );

        if (debug) {
            Console.WriteLine($"[DEBUG] OSBase[faceit]: updated cache for {steamId64}, changed={changed}, appliedLevel={appliedLevel}, storedLevel={storedLevel}");
        }
    }

    private void ApplyTemporaryError(ulong steamId64, string errorMessage) {
        if (db == null) {
            return;
        }

        DateTime retryAt = DateTime.UtcNow.AddHours(1);

        string query = @"
            UPDATE `faceit_cache`
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
        if (row == null) {
            return true;
        }

        string oldPlayerId = row["faceit_player_id"] == DBNull.Value ? "" : row["faceit_player_id"].ToString() ?? "";
        string oldNickname = row["faceit_nickname"] == DBNull.Value ? "" : row["faceit_nickname"].ToString() ?? "";
        int oldSkill = row["skill_level"] == DBNull.Value ? -1 : Convert.ToInt32(row["skill_level"]);
        int oldElo = row["faceit_elo"] == DBNull.Value ? -1 : Convert.ToInt32(row["faceit_elo"]);
        bool oldBan = row["active_ban"] != DBNull.Value && Convert.ToInt32(row["active_ban"]) == 1;
        bool oldHasAccount = row["has_faceit_account"] != DBNull.Value && Convert.ToInt32(row["has_faceit_account"]) == 1;

        if (oldPlayerId != (result.FaceitPlayerId ?? "")) {
            return true;
        }

        if (oldNickname != (result.FaceitNickname ?? "")) {
            return true;
        }

        if (oldSkill != (result.SkillLevel ?? -1)) {
            return true;
        }

        if (oldElo != (result.FaceitElo ?? -1)) {
            return true;
        }

        if (oldBan != result.ActiveBan) {
            return true;
        }

        if (oldHasAccount != result.HasFaceitAccount) {
            return true;
        }

        return false;
    }

    private int GetNextCacheLevel(int currentLevel) {
        if (currentLevel < 0) {
            return 0;
        }

        if (currentLevel >= cacheDays.Length - 1) {
            return cacheDays.Length - 1;
        }

        return currentLevel + 1;
    }

    private DateTime GetNextCheckAt(int cacheLevel) {
        int safeLevel = Math.Clamp(cacheLevel, 0, cacheDays.Length - 1);
        return DateTime.UtcNow.AddDays(cacheDays[safeLevel]);
    }

    private void NotifyAdminsFromCache(CCSPlayerController player, DataRow row) {
        if (!notifyAdminsOnConnect) {
            return;
        }

        bool hasFaceit = row["has_faceit_account"] != DBNull.Value && Convert.ToInt32(row["has_faceit_account"]) == 1;
        bool activeBan = row["active_ban"] != DBNull.Value && Convert.ToInt32(row["active_ban"]) == 1;
        string banReason = row["ban_reason"] == DBNull.Value ? "unknown" : row["ban_reason"].ToString() ?? "unknown";

        if (!hasFaceit) {
            ChatHelper.PrintToAdmins(
                $"{player.PlayerName} | no FACEIT account found",
                adminPermission,
                ChatPrefix
            );
            return;
        }

        if (onlyNotifyOnBan && !activeBan) {
            return;
        }

        string nickname = row["faceit_nickname"] == DBNull.Value ? player.PlayerName ?? "unknown" : row["faceit_nickname"].ToString() ?? player.PlayerName ?? "unknown";
        string level = row["skill_level"] == DBNull.Value ? "?" : row["skill_level"].ToString() ?? "?";
        string elo = row["faceit_elo"] == DBNull.Value ? "?" : row["faceit_elo"].ToString() ?? "?";

        string message = activeBan
            ? $"{player.PlayerName} | FACEIT: {nickname} | lvl {level} | elo {elo} | ACTIVE BAN: {banReason}"
            : $"{player.PlayerName} | FACEIT: {nickname} | lvl {level} | elo {elo}";

        ChatHelper.PrintToAdmins(message, adminPermission, ChatPrefix);
    }

    private void NotifyAdminsIfRelevant(ulong steamId64, FaceitLookupResult result) {
        if (!notifyAdminsOnConnect) {
            return;
        }

        var player = FindOnlinePlayer(steamId64);
        if (!IsValidHuman(player)) {
            return;
        }

        if (!result.HasFaceitAccount) {
            ChatHelper.PrintToAdmins(
                $"{player!.PlayerName} | no FACEIT account found",
                adminPermission,
                ChatPrefix
            );
            return;
        }

        if (onlyNotifyOnBan && !result.ActiveBan) {
            return;
        }

        string message = result.ActiveBan
            ? $"{player!.PlayerName} | FACEIT: {result.FaceitNickname ?? "unknown"} | lvl {result.SkillLevel?.ToString() ?? "?"} | elo {result.FaceitElo?.ToString() ?? "?"} | ACTIVE BAN: {result.BanReason ?? "unknown"}"
            : $"{player!.PlayerName} | FACEIT: {result.FaceitNickname ?? "unknown"} | lvl {result.SkillLevel?.ToString() ?? "?"} | elo {result.FaceitElo?.ToString() ?? "?"}";

        ChatHelper.PrintToAdmins(message, adminPermission, ChatPrefix);
    }

    private CCSPlayerController? FindOnlinePlayer(ulong steamId64) {
        foreach (var player in Utilities.GetPlayers()) {
            if (!IsValidHuman(player)) {
                continue;
            }

            if (player!.SteamID == steamId64) {
                return player;
            }
        }

        return null;
    }

    private bool IsValidHuman(CCSPlayerController? player) {
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) {
            return false;
        }

        if (player.Connected != PlayerConnectedState.PlayerConnected) {
            return false;
        }

        return true;
    }

    private void ClearQueue() {
        lookupQueue.Clear();
        queuedSteamIds.Clear();
        workerBusy = false;
    }

    private class FaceitPlayerResponse {
        [JsonPropertyName("player_id")]
        public string? PlayerId { get; set; }

        [JsonPropertyName("nickname")]
        public string? Nickname { get; set; }

        [JsonPropertyName("country")]
        public string? Country { get; set; }

        [JsonPropertyName("verified")]
        public bool Verified { get; set; }

        [JsonPropertyName("steam_id_64")]
        public string? SteamId64 { get; set; }

        [JsonPropertyName("games")]
        public Dictionary<string, FaceitGameData>? Games { get; set; }
    }

    private class FaceitGameData {
        [JsonPropertyName("region")]
        public string? Region { get; set; }

        [JsonPropertyName("game_player_id")]
        public string? GamePlayerId { get; set; }

        [JsonPropertyName("skill_level")]
        public int? SkillLevel { get; set; }

        [JsonPropertyName("faceit_elo")]
        public int? FaceitElo { get; set; }

        [JsonPropertyName("game_player_name")]
        public string? GamePlayerName { get; set; }

        [JsonPropertyName("skill_level_label")]
        public string? SkillLevelLabel { get; set; }
    }

    private class FaceitBansResponse {
        [JsonPropertyName("items")]
        public List<FaceitBanItem> Items { get; set; } = new();
    }

    private class FaceitBanItem {
        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("entity_type")]
        public string? EntityType { get; set; }

        [JsonPropertyName("expires_at")]
        public DateTime? ExpiresAt { get; set; }
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
        public string? BanReason { get; set; }
        public string Status { get; set; } = "pending";
    }
}