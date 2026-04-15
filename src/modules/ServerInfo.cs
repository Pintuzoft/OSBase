using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using MySqlConnector;

namespace OSBase.Modules;

public class ServerInfo : IModule {
    public string ModuleName => "serverinfo";

    private OSBase? osbase;
    private Config? config;
    private Database? db;

    private bool handlersLoaded = false;
    private bool isActive = false;

    private int port = 0;
    private string host = string.Empty;
    private string name = string.Empty;
    private string map = string.Empty;

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;
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

        CreateTables();

        map = Server.MapName ?? osbase.currentMap ?? string.Empty;
        SaveServerInfo();

        LoadHandlers();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    public void Unload() {
        isActive = false;

        if (osbase != null && handlersLoaded) {
            osbase.DeregisterEventHandler<EventPlayerConnect>(OnPlayerConnect);
            osbase.DeregisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
            osbase.DeregisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
            osbase.DeregisterEventHandler<EventRoundEnd>(OnRoundEnd);
            osbase.RemoveListener<Listeners.OnMapStart>(OnMapStart);

            handlersLoaded = false;
        }

        db = null;
        config = null;
        osbase = null;

        port = 0;
        host = string.Empty;
        name = string.Empty;
        map = string.Empty;

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] unloaded.");
    }

    public void ReloadConfig(Config inConfig) {
        config = inConfig;

        CreateCustomConfigs();
        LoadConfig();

        if (osbase != null) {
            map = Server.MapName ?? osbase.currentMap ?? map;
        }

        SaveServerInfo();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] config reloaded.");
    }

    private void LoadHandlers() {
        if (osbase == null || handlersLoaded) {
            return;
        }

        osbase.RegisterEventHandler<EventPlayerConnect>(OnPlayerConnect);
        osbase.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        osbase.RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);

        handlersLoaded = true;
    }

    private void CreateCustomConfigs() {
        config?.CreateCustomConfig(
            $"{ModuleName}.cfg",
            "// ServerInfo Configuration\n" +
            "name \"Server Name\"\n" +
            "host \"cs2.oldswedes.se\"\n" +
            "port 27015\n"
        );
    }

    private void LoadConfig() {
        name = string.Empty;
        host = string.Empty;
        port = 0;

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

                default:
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Unknown config key {key}:{value}");
                    break;
            }
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Config loaded. name={name}, host={host}, port={port}");
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

    private HookResult OnPlayerConnect(EventPlayerConnect eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        var player = eventInfo?.Userid;
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) {
            return HookResult.Continue;
        }

        UpsertUserRow(player, teamOverride: 0);
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        foreach (var player in Utilities.GetPlayers()) {
            if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) {
                continue;
            }

            UpsertUserRow(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        var player = eventInfo?.Userid;
        if (player == null) {
            return HookResult.Continue;
        }

        DeleteUserRow(player.PlayerName ?? string.Empty);
        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        var player = eventInfo?.Userid;
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) {
            return HookResult.Continue;
        }

        int team = eventInfo?.Team ?? 0;
        UpsertUserRow(player, teamOverride: team);
        return HookResult.Continue;
    }

    private void OnMapStart(string mapName) {
        if (!isActive) {
            return;
        }

        map = mapName;
        SaveServerInfo();
        ClearUsers();

        foreach (var player in Utilities.GetPlayers()) {
            if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) {
                continue;
            }

            UpsertUserRow(player);
        }
    }

    private void CreateTables() {
        if (db == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Database instance is null.");
            return;
        }

        string serverTable = """
        CREATE TABLE IF NOT EXISTS serverinfo_server (
            port int(11),
            host varchar(64),
            name varchar(64),
            map varchar(64),
            timestamp int(11) default 0,
            primary key (host, port)
        ) ENGINE=InnoDB DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;
        """;

        string userTable = """
        CREATE TABLE IF NOT EXISTS serverinfo_user (
            host varchar(64) not null,
            port int(11) not null,
            name varchar(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
            team int(11),
            kills int(11),
            assists int(11),
            deaths int(11),
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
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error creating tables: {e.Message}");
        }
    }

    private void SaveServerInfo() {
        if (db == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Database instance is null.");
            return;
        }

        const string query =
            "INSERT INTO serverinfo_server (host, port, name, map) " +
            "VALUES (@host, @port, @name, @map) " +
            "ON DUPLICATE KEY UPDATE name=@name, map=@map";

        var parameters = new MySqlParameter[] {
            new MySqlParameter("@host", host),
            new MySqlParameter("@port", port),
            new MySqlParameter("@name", name),
            new MySqlParameter("@map", map)
        };

        try {
            db.insert(query, parameters);
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error saving server info: {e.Message}");
        }
    }

    private void ClearUsers() {
        if (db == null) {
            return;
        }

        try {
            db.delete(
                "DELETE FROM serverinfo_user WHERE host=@host AND port=@port",
                new MySqlParameter[] {
                    new MySqlParameter("@host", host),
                    new MySqlParameter("@port", port)
                }
            );
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error clearing users: {e.Message}");
        }
    }

    private void DeleteUserRow(string playerName) {
        if (db == null || string.IsNullOrWhiteSpace(playerName)) {
            return;
        }

        try {
            db.delete(
                "DELETE FROM serverinfo_user WHERE host=@host AND port=@port AND name=@name",
                new MySqlParameter[] {
                    new MySqlParameter("@host", host),
                    new MySqlParameter("@port", port),
                    new MySqlParameter("@name", playerName)
                }
            );
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error deleting user row: {e.Message}");
        }
    }

    private void UpsertUserRow(CCSPlayerController player, int? teamOverride = null) {
        if (db == null || osbase == null || player == null || !player.IsValid) {
            return;
        }

        string playerName = player.PlayerName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(playerName)) {
            return;
        }

        int kills = 0;
        int assists = 0;
        int deaths = 0;

        if (player.UserId.HasValue) {
            PlayerStats? stats = osbase.GetGameStats()?.GetPlayerStats(player.UserId.Value);
            if (stats != null) {
                kills = stats.kills;
                assists = stats.assists;
                deaths = stats.deaths;
            }
        }

        int team = teamOverride ?? player.TeamNum;

        const string query =
            "INSERT INTO serverinfo_user (host, port, name, team, kills, assists, deaths) " +
            "VALUES (@host, @port, @name, @team, @kills, @assists, @deaths) " +
            "ON DUPLICATE KEY UPDATE team=@team, kills=@kills, assists=@assists, deaths=@deaths, name=@name";

        var parameters = new MySqlParameter[] {
            new MySqlParameter("@host", host),
            new MySqlParameter("@port", port),
            new MySqlParameter("@name", playerName),
            new MySqlParameter("@team", team),
            new MySqlParameter("@kills", kills),
            new MySqlParameter("@assists", assists),
            new MySqlParameter("@deaths", deaths)
        };

        try {
            db.insert(query, parameters);
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error upserting user row: {e.Message}");
        }
    }
}