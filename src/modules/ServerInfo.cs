using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using MySqlConnector;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace OSBase.Modules;

public class ServerInfo : ModuleBase {
    public override string ModuleName => "serverinfo";

    private Database? db;

    private int port = 0;
    private string host = string.Empty;
    private string name = string.Empty;
    private string map = string.Empty;
    private string workshopCollection = string.Empty;

    // Set once when the plugin loads (~server start). Written on every SaveServerInfo so the
    // row reflects THIS run's start; uptime is derived by readers as now - started_at.
    private long serverStartedAt = 0;

    private Timer? pendingPruneTimer;
    private Timer? heartbeatTimer;

    // Debounced idle-heartbeat interval. The timer is reset (killed + rescheduled) on every
    // round-end and map start, so during live play it never fires. It only fires when the server
    // has been quiet longer than a full round could last (~1:45 round + freeze) - i.e. idle or
    // stuck, since bots don't start rounds until a human joins. Kept above the max round length so
    // normal play never trips it.
    private const float HeartbeatIntervalSeconds = 180f;

    // During a map change, players briefly disconnect and reconnect. We open a grace
    // window (in OnMapEnd, before the disconnect churn, extended in OnMapStart) during
    // which we neither delete rows on disconnect nor prune. This lets players "survive"
    // the map change so their connected_at keeps ticking instead of resetting.
    private const int MapChangeGraceSeconds = 30;
    private long mapChangeGraceUntil = 0;

    // True between round start and round end. A player who disconnects mid-round is kept
    // in the list until round end, when OnRoundEnd runs the reconcile prune.
    private bool inRound = false;

    // Remembers each human's original connect time per SteamID. A session is dropped on a
    // genuine disconnect (see OnPlayerDisconnect), but a map-change carry-over keeps it, so a
    // player who leaves only to load/download the new map resumes the same session instead of
    // getting a fresh connected_at. This window bounds how long we wait for such a return
    // (generous enough for a slow workshop download) before giving up and freeing the entry.
    // The row itself is still pruned while they're gone; this only restores continuity.
    private const int ReconnectMemorySeconds = 1800;

    // Rows untouched for this long are treated as crash ghosts (no clean shutdown pruned them)
    // and swept regardless of which server is online. Must exceed the normal upsert cadence.
    private const int StaleRowMaxAgeSeconds = 1800;
    private readonly Dictionary<ulong, (long connectedAt, long lastSeen)> sessions = new();

    private static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private bool InMapChangeGrace => NowUnix() < mapChangeGraceUntil;

    protected override void OnLoad() {
        CreateCustomConfigs();
        LoadConfig();

        db = new Database(osbase!, config!);
        db.SetAutoDrain(true);

        CreateTables();

        serverStartedAt = NowUnix();
        map = Server.MapName ?? osbase?.currentMap ?? string.Empty;
        SaveServerInfo();

        // Arm the idle-heartbeat. Round-end/map-start keep resetting it, so it only fires when the
        // server sits quiet (no rounds) - then it refreshes the player list and server heartbeat.
        ScheduleHeartbeat();
    }

    protected override void OnUnload() {
        pendingPruneTimer?.Kill();
        pendingPruneTimer = null;

        heartbeatTimer?.Kill();
        heartbeatTimer = null;

        db?.FlushPendingWrites(1500);
        db = null;

        sessions.Clear();
        inRound = false;
        mapChangeGraceUntil = 0;
        serverStartedAt = 0;

        port = 0;
        host = string.Empty;
        name = string.Empty;
        map = string.Empty;
    }

    protected override void OnReloadConfig() {
        CreateCustomConfigs();
        LoadConfig();

        if (osbase != null) {
            map = Server.MapName ?? osbase.currentMap ?? map;
        }

        SaveServerInfo();
        SchedulePruneUsers(0.2f);
    }

    protected override void RegisterHandlers() {
        // Use new EventBus system
        osbase?.SubscribeToEvent<EventPlayerConnectFull>(OnPlayerConnectFull);
        osbase?.SubscribeToEvent<EventPlayerDisconnect>(OnPlayerDisconnect);
        osbase?.SubscribeToEvent<EventPlayerTeam>(OnPlayerTeam);
        osbase?.SubscribeToEvent<EventRoundStart>(OnRoundStart);
        osbase?.SubscribeToEvent<EventRoundEnd>(OnRoundEnd);
        osbase?.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        osbase?.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
    }

    protected override void UnregisterHandlers() {
        // Use new EventBus system
        osbase?.UnsubscribeFromEvent<EventPlayerConnectFull>(OnPlayerConnectFull);
        osbase?.UnsubscribeFromEvent<EventPlayerDisconnect>(OnPlayerDisconnect);
        osbase?.UnsubscribeFromEvent<EventPlayerTeam>(OnPlayerTeam);
        osbase?.UnsubscribeFromEvent<EventRoundStart>(OnRoundStart);
        osbase?.UnsubscribeFromEvent<EventRoundEnd>(OnRoundEnd);
        osbase?.RemoveListener<Listeners.OnMapStart>(OnMapStart);
        osbase?.RemoveListener<Listeners.OnMapEnd>(OnMapEnd);
    }

    private void CreateCustomConfigs() {
        config?.CreateCustomConfig(
            $"{ModuleName}.cfg",
            "// ServerInfo Configuration\n" +
            "name \"Server Name\"\n" +
            "host \"cs2.oldswedes.se\"\n" +
            "port 27015\n" +
            "// Steam Workshop collection ID this server rotates (leave empty for standard/official maps).\n" +
            "// If empty, it is auto-detected from the +host_workshop_collection launch argument.\n" +
            "workshop_collection \"\"\n"
        );
    }

    private void LoadConfig() {
        name = string.Empty;
        host = string.Empty;
        port = 0;
        workshopCollection = string.Empty;

        List<string> cfg = config?.FetchCustomConfig($"{ModuleName}.cfg") ?? new List<string>();

        foreach (var rawLine in cfg) {
            string trimmedLine = rawLine.Trim();

            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//")) {
                continue;
            }

            var parts = trimmedLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) {
                Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Invalid config line skipped: {trimmedLine}");
                continue;
            }

            string key = parts[0].Trim();
            string value = Unquote(parts[1].Trim());

            switch (key) {
                case "name":
                    name = value;
                    break;

                case "host":
                    host = value;
                    break;

                case "port":
                    if (!int.TryParse(value, out port)) {
                        Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: Invalid port value: {value}");
                        port = 0;
                    }
                    break;

                case "workshop_collection":
                    workshopCollection = value;
                    break;

                default:
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Unknown config key {key}:{value}");
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(workshopCollection)) {
            workshopCollection = DetectWorkshopCollectionFromLaunchArgs();
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Config loaded. name={name}, host={host}, port={port}, workshop_collection={workshopCollection}");
    }

    // Best-effort read of "+host_workshop_collection <id>" from the server launch arguments.
    // Config takes priority; this only runs when the config value is empty.
    private static string DetectWorkshopCollectionFromLaunchArgs() {
        string[] args = Environment.GetCommandLineArgs();

        for (int i = 0; i < args.Length - 1; i++) {
            string token = args[i].TrimStart('+', '-');
            if (string.Equals(token, "host_workshop_collection", StringComparison.OrdinalIgnoreCase)) {
                string candidate = args[i + 1].Trim();
                if (candidate.Length > 0 && candidate.All(char.IsDigit)) {
                    return candidate;
                }
            }
        }

        return string.Empty;
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

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        var player = eventInfo?.Userid;
        if (!IsTrackablePlayer(player)) {
            return HookResult.Continue;
        }

        UpsertUserRow(player!);
        SchedulePruneUsers();

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        var player = eventInfo?.Userid;

        // A disconnect during the map-change grace is a carry-over: the map is changing and
        // the player is only stepping out to load/download it. Everyone present at a map
        // change lands here, so we keep their session and their connect time is restored
        // whenever they return (even after a long workshop download). Any disconnect OUTSIDE
        // the grace is a genuine leave, so we forget the session and a later reconnect starts
        // fresh times.
        if (!InMapChangeGrace && player != null && !player.IsBot && player.SteamID != 0) {
            sessions.Remove(player.SteamID);
        }

        // Don't remove the row the instant they drop:
        //  - during a map change they're only transitioning and will reconnect;
        //  - mid-round we keep them listed until round end.
        // In both cases the reconcile prune (after grace / at round end) removes anyone who
        // genuinely left. Between rounds we delete right away as before.
        if (!InMapChangeGrace && !inRound && player != null) {
            string playerName = player.PlayerName ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(playerName)) {
                DeleteUserRow(playerName);
            }
        }

        SchedulePruneUsers();
        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        var player = eventInfo?.Userid;
        if (!IsTrackablePlayer(player)) {
            return HookResult.Continue;
        }

        int team = eventInfo?.Team ?? player!.TeamNum;
        UpsertUserRow(player!, teamOverride: team);
        SchedulePruneUsers();

        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        inRound = true;

        // Keep writes queued during live round to avoid DB drain on critical ticks.
        db?.SetAutoDrain(false);
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        inRound = false;

        // Round-end is our safe window for draining queued writes, and the point where we
        // reconcile players who disconnected during the round out of the list.
        db?.SetAutoDrain(true);

        foreach (var player in Utilities.GetPlayers()) {
            if (!IsTrackablePlayer(player)) {
                continue;
            }

            UpsertUserRow(player!);
        }

        TouchServerHeartbeat();
        ScheduleHeartbeat();
        SchedulePruneUsers(0.2f);
        db?.FlushPendingWrites(1000);
        return HookResult.Continue;
    }

    // Opens the grace window before the map-change disconnect churn so those disconnects
    // (which can fire before OnMapStart) don't delete rows and reset connected_at.
    private void OnMapEnd() {
        if (!isActive) {
            return;
        }

        mapChangeGraceUntil = NowUnix() + MapChangeGraceSeconds;
    }

    private void OnMapStart(string mapName) {
        if (!isActive) {
            return;
        }

        // Extend the grace window to cover reconnects settling on the new map.
        mapChangeGraceUntil = NowUnix() + MapChangeGraceSeconds;

        // No live round on a fresh map; clear the flag so a missed round-end can't stall pruning.
        inRound = false;

        db?.SetAutoDrain(true);

        map = mapName;
        SaveServerInfo();

        foreach (var player in Utilities.GetPlayers()) {
            if (!IsTrackablePlayer(player)) {
                continue;
            }

            UpsertUserRow(player!);
        }

        // SchedulePruneUsers is grace-aware and defers itself until after the window,
        // by which time carried-over players have reconnected and appear online.
        ScheduleHeartbeat();
        SchedulePruneUsers();
        db?.FlushPendingWrites(1000);
    }

    private void SchedulePruneUsers(float delay = 0.5f) {
        if (!isActive || osbase == null) {
            return;
        }

        // Hold off pruning during a live round; OnRoundEnd runs the reconcile prune once the
        // round is over, so a mid-round disconnect stays listed until then.
        if (inRound) {
            return;
        }

        // While a map change is in flight, defer pruning until the grace window closes so
        // players mid-reconnect aren't wrongly removed.
        if (InMapChangeGrace) {
            float graceDelay = (mapChangeGraceUntil - NowUnix()) + 1f;
            if (graceDelay > delay) {
                delay = graceDelay;
            }
        }

        pendingPruneTimer?.Kill();
        pendingPruneTimer = osbase.AddTimer(delay, () => {
            pendingPruneTimer = null;
            PruneStaleUsers();
        });
    }

    private void PruneStaleUsers() {
        if (!isActive || db == null) {
            return;
        }

        try {
            var onlineNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var player in Utilities.GetPlayers()) {
                if (!IsTrackablePlayer(player)) {
                    continue;
                }

                string playerName = player!.PlayerName ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(playerName)) {
                    onlineNames.Add(playerName);
                }
            }

            DataTable table = db.select(
                "name FROM serverinfo_user WHERE host=@host AND port=@port",
                new MySqlParameter("@host", host),
                new MySqlParameter("@port", port)
            );

            foreach (DataRow row in table.Rows) {
                string dbName = row["name"]?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(dbName)) {
                    continue;
                }

                if (!onlineNames.Contains(dbName)) {
                    // Sync delete: prune must take effect immediately, bypassing the round-gated drain.
                    db.delete(
                        "FROM serverinfo_user WHERE host=@host AND port=@port AND name=@name",
                        new MySqlParameter("@host", host),
                        new MySqlParameter("@port", port),
                        new MySqlParameter("@name", dbName)
                    );
                }
            }

            // Safety net for crash ghosts: rows no one has touched in a long time belong to a
            // server that died without a clean shutdown (so its live reconcile never ran). Swept
            // by whichever server is online, across all rows sharing this DB. last_seen>0 guards
            // against any legacy/unbackfilled row being wrongly aged out.
            db.delete(
                "FROM serverinfo_user WHERE last_seen > 0 AND UNIX_TIMESTAMP() - last_seen > @maxAge",
                new MySqlParameter("@maxAge", StaleRowMaxAgeSeconds)
            );

            // Drop session memory for players gone longer than the reconnect window; they'll
            // start a fresh session if they ever return.
            long nowUnix = NowUnix();
            var expired = sessions.Where(kv => nowUnix - kv.Value.lastSeen > ReconnectMemorySeconds)
                                  .Select(kv => kv.Key)
                                  .ToList();
            foreach (var steamid in expired) {
                sessions.Remove(steamid);
            }

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] stale prune complete.");
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error pruning stale users: {e.Message}");
        }
    }

    private void CreateTables() {
        if (db == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Database instance is null.");
            return;
        }

        string serverTable = """
        TABLE IF NOT EXISTS serverinfo_server (
            port int(11),
            host varchar(64),
            name varchar(64),
            map varchar(64),
            workshop_collection varchar(32) default null,
            timestamp int(11) default 0,
            started_at int(11) default 0,
            last_seen int(11) default 0,
            primary key (host, port)
        ) ENGINE=InnoDB DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;
        """;

        string userTable = """
        TABLE IF NOT EXISTS serverinfo_user (
            host varchar(64) not null,
            port int(11) not null,
            name varchar(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
            steamid varchar(32) default null,
            team int(11),
            kills int(11),
            assists int(11),
            deaths int(11),
            connected_at int(11) default 0,
            last_seen int(11) default 0,
            primary key (host, port, name),
            constraint serverinfo_user_fk_server
                foreign key (host, port)
                references serverinfo_server (host, port)
                on delete cascade
        ) ENGINE=InnoDB DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;
        """;

        try {
            db.create(serverTable);
            db.create(userTable);
            EnsureColumn("serverinfo_server", "workshop_collection", "varchar(32) default null");
            bool serverStartedAtAdded = EnsureColumn("serverinfo_server", "started_at", "int(11) default 0");
            bool serverLastSeenAdded = EnsureColumn("serverinfo_server", "last_seen", "int(11) default 0");
            EnsureColumn("serverinfo_user", "steamid", "varchar(32) default null");
            bool connectedAtAdded = EnsureColumn("serverinfo_user", "connected_at", "int(11) default 0");
            bool lastSeenAdded = EnsureColumn("serverinfo_user", "last_seen", "int(11) default 0");

            if (connectedAtAdded) {
                // Backfill legacy rows with the DB's own clock so pre-existing players don't
                // read as "connected since 1970" until their next reconnect.
                int backfilled = db.update(
                    "serverinfo_user SET connected_at = UNIX_TIMESTAMP() WHERE connected_at = 0 OR connected_at IS NULL");
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Backfilled connected_at for {backfilled} legacy row(s).");
            }

            if (lastSeenAdded) {
                // Same for last_seen so legacy rows aren't instantly treated as stale ghosts.
                int backfilled = db.update(
                    "serverinfo_user SET last_seen = UNIX_TIMESTAMP() WHERE last_seen = 0 OR last_seen IS NULL");
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Backfilled last_seen for {backfilled} legacy row(s).");
            }

            if (serverStartedAtAdded) {
                // Give pre-existing server rows the DB clock so uptime doesn't read from 1970
                // until the server writes its real start on next load.
                int backfilled = db.update(
                    "serverinfo_server SET started_at = UNIX_TIMESTAMP() WHERE started_at = 0 OR started_at IS NULL");
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Backfilled server started_at for {backfilled} row(s).");
            }

            if (serverLastSeenAdded) {
                int backfilled = db.update(
                    "serverinfo_server SET last_seen = UNIX_TIMESTAMP() WHERE last_seen = 0 OR last_seen IS NULL");
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Backfilled server last_seen for {backfilled} row(s).");
            }
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error creating tables: {e.Message}");
        }
    }

    // Adds a column to an existing table if it's missing (migration for pre-existing installs).
    // Returns true if the column was just added, so callers can run one-time backfills.
    private bool EnsureColumn(string table, string column, string definition) {
        if (db == null) {
            return false;
        }

        try {
            DataTable existing = db.select(
                "column_name FROM information_schema.columns " +
                "WHERE table_schema = DATABASE() AND table_name = @table AND column_name = @column",
                new MySqlParameter("@table", table),
                new MySqlParameter("@column", column)
            );

            if (existing.Rows.Count == 0) {
                db.alter($"TABLE {table} ADD COLUMN {column} {definition}");
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Added missing column {table}.{column}.");
                return true;
            }
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error ensuring column {table}.{column}: {e.Message}");
        }

        return false;
    }

    private void SaveServerInfo() {
        if (db == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Database instance is null.");
            return;
        }

        // started_at carries this run's start time (stable across map changes, refreshed on a
        // plugin reload). last_seen is a heartbeat so readers can tell a live server from a dead
        // one; it's also refreshed periodically by the heartbeat timer (TouchServerHeartbeat).
        string query =
            "INTO serverinfo_server (host, port, name, map, workshop_collection, started_at, last_seen) " +
            "VALUES (@host, @port, @name, @map, @workshop_collection, @started_at, @now) " +
            "ON DUPLICATE KEY UPDATE name=@name, map=@map, workshop_collection=@workshop_collection, started_at=@started_at, last_seen=@now";

        var parameters = new MySqlParameter[] {
            new MySqlParameter("@host", host),
            new MySqlParameter("@port", port),
            new MySqlParameter("@name", name),
            new MySqlParameter("@map", map),
            new MySqlParameter("@workshop_collection",
                string.IsNullOrWhiteSpace(workshopCollection) ? (object)DBNull.Value : workshopCollection),
            new MySqlParameter("@started_at", serverStartedAt),
            new MySqlParameter("@now", NowUnix())
        };

        try {
            db.insertAsync(query, parameters);
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error saving server info: {e.Message}");
        }
    }

    // (Re)arms the debounced idle-heartbeat. Called on load and reset on every round-end / map
    // start, so it only fires after a full quiet stretch (no rounds) - an idle or stuck server.
    private void ScheduleHeartbeat() {
        if (!isActive || osbase == null) {
            return;
        }

        heartbeatTimer?.Kill();
        heartbeatTimer = osbase.AddTimer(HeartbeatIntervalSeconds, OnHeartbeat);
    }

    private void OnHeartbeat() {
        heartbeatTimer = null;

        if (!isActive || db == null) {
            return;
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - idle heartbeat fired (refreshing server + players).");

        // Nothing has refreshed us for a full round's worth of time, so the server is idle/stuck.
        // A stray round_start (e.g. warmup) may have left auto-drain off with no round_end to turn
        // it back on, which would leave our writes queued forever. Reaching here means there's no
        // live round to protect, so force draining on and flush at the end so the heartbeat lands.
        db.SetAutoDrain(true);

        // Refresh the server heartbeat and every connected player (incl. bots, which double as an
        // at-a-glance "is this server alive" signal) so the list stays fresh and isn't age-pruned,
        // then re-arm to keep watching while idle.
        TouchServerHeartbeat();

        foreach (var player in Utilities.GetPlayers()) {
            if (!IsTrackablePlayer(player)) {
                continue;
            }

            UpsertUserRow(player!);
        }

        db.FlushPendingWrites(1000);
        SchedulePruneUsers();
        ScheduleHeartbeat();
    }

    // Refreshes only the server's heartbeat (last_seen) without rewriting the whole row, so a
    // reader can tell a running server from a crashed/stopped one by its age.
    private void TouchServerHeartbeat() {
        if (db == null) {
            return;
        }

        try {
            db.updateAsync(
                "serverinfo_server SET last_seen=@now WHERE host=@host AND port=@port",
                new MySqlParameter("@now", NowUnix()),
                new MySqlParameter("@host", host),
                new MySqlParameter("@port", port)
            );
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error updating server heartbeat: {e.Message}");
        }
    }

    private void DeleteUserRow(string playerName) {
        if (db == null || string.IsNullOrWhiteSpace(playerName)) {
            return;
        }

        try {
            db.deleteAsync(
                "FROM serverinfo_user WHERE host=@host AND port=@port AND name=@name",
                new MySqlParameter("@host", host),
                new MySqlParameter("@port", port),
                new MySqlParameter("@name", playerName)
            );
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error deleting user row: {e.Message}");
        }
    }

    private void UpsertUserRow(CCSPlayerController player, int? teamOverride = null) {
        if (db == null || osbase == null || !IsTrackablePlayer(player)) {
            return;
        }

        string playerName = player!.PlayerName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(playerName)) {
            playerName = player.IsBot ? $"Bot-{player.Index}" : "Unknown";
        }

        int kills = 0;
        int assists = 0;
        int deaths = 0;

        if (!player.IsBot && player.UserId.HasValue) {
            PlayerStats? stats = osbase.GetGameStats()?.GetPlayerStats(player.UserId.Value);
            if (stats != null) {
                kills = stats.kills;
                assists = stats.assists;
                deaths = stats.deaths;
            }
        }

        int team = teamOverride ?? player.TeamNum;
        string steamId = player.SteamID.ToString();
        long now = NowUnix();
        long connectedAt = ResolveConnectedAt(player, now);

        // connected_at comes from the session memory (the player's original join time,
        // restored across reconnects) and is deliberately left out of the UPDATE clause so a
        // still-present row keeps whatever it already stored - important if the session memory
        // was lost to a plugin reload. Readers derive elapsed time live as NOW() - connected_at.
        // last_seen is refreshed on every upsert so a stale row (e.g. after a server crash)
        // can be detected/pruned by age.
        string query =
            "INTO serverinfo_user (host, port, name, steamid, team, kills, assists, deaths, connected_at, last_seen) " +
            "VALUES (@host, @port, @name, @steamid, @team, @kills, @assists, @deaths, @connectedAt, @now) " +
            "ON DUPLICATE KEY UPDATE steamid=@steamid, team=@team, kills=@kills, assists=@assists, deaths=@deaths, name=@name, last_seen=@now";

        var parameters = new MySqlParameter[] {
            new MySqlParameter("@host", host),
            new MySqlParameter("@port", port),
            new MySqlParameter("@name", playerName),
            new MySqlParameter("@steamid", steamId),
            new MySqlParameter("@team", team),
            new MySqlParameter("@kills", kills),
            new MySqlParameter("@assists", assists),
            new MySqlParameter("@deaths", deaths),
            new MySqlParameter("@connectedAt", connectedAt),
            new MySqlParameter("@now", now)
        };

        try {
            db.insertAsync(query, parameters);
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error upserting user row: {e.Message}");
        }
    }

    // Returns the player's session start time. Reuses the remembered value when they
    // reconnect within ReconnectMemorySeconds (e.g. after a workshop map download), otherwise
    // starts a fresh session. Bots are transient and all share SteamID 0, so they never use
    // the memory.
    private long ResolveConnectedAt(CCSPlayerController player, long now) {
        if (player.IsBot) {
            return now;
        }

        ulong steamid = player.SteamID;
        if (steamid == 0) {
            return now;
        }

        if (sessions.TryGetValue(steamid, out var s) && (now - s.lastSeen) <= ReconnectMemorySeconds) {
            sessions[steamid] = (s.connectedAt, now);
            return s.connectedAt;
        }

        sessions[steamid] = (now, now);
        return now;
    }

    private static bool IsTrackablePlayer(CCSPlayerController? player) {
        if (player == null || !player.IsValid || player.IsHLTV) {
            return false;
        }

        return true;
    }
}