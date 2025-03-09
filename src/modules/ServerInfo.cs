using System;
using System.IO;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Events;
using System.Diagnostics.Tracing;
using System.Reflection;
using MySqlConnector;

namespace OSBase.Modules;

using System.IO;
using CounterStrikeSharp.API.Modules.Utils;

public class ServerInfo : IModule {
    public string ModuleName => "serverinfo";   
    private OSBase? osbase;
    private Config? config;
    private Database? db;
    private int port;
    private string host = "";
    private string name = "";
    private string map = "";
    private const int TEAM_T = (int)CsTeam.Terrorist;
    private const int TEAM_CT = (int)CsTeam.CounterTerrorist;
    private const int TEAM_S = (int)CsTeam.Spectator;

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        // Register required global config values
        config.RegisterGlobalConfigValue($"{ModuleName}", "1");

        if (osbase == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] osbase is null. {ModuleName} failed to load.");
            return;
        } else if (config == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] config is null. {ModuleName} failed to load.");
            return;
        }

        if (config?.GetGlobalConfigValue($"{ModuleName}", "0") == "1") {
            createCustomConfigs();
            LoadConfig();
            loadEventHandlers();
            this.db = new Database(this.osbase, this.config);
            createTables();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
        } else {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {ModuleName} is disabled in the global configuration.");
            return;
        }
    }

    private void loadEventHandlers() {
        if(osbase == null) return;
        osbase?.RegisterEventHandler<EventPlayerConnect>(onPlayerConnect);
        osbase?.RegisterEventHandler<EventPlayerDisconnect>(onPlayerDisconnect);
        osbase?.RegisterEventHandler<EventPlayerTeam>(onPlayerTeam);
        osbase?.RegisterEventHandler<EventRoundEnd>(onRoundEnd);
        osbase?.RegisterListener<Listeners.OnMapStart>(onMapStart);
    }

    private void LoadConfig() {
        List<string> dbcfg = config?.FetchCustomConfig($"{ModuleName}.cfg") ?? new List<string>();

        foreach (var line in dbcfg) {
            string trimmedLine = line.Trim();
            if ( string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//") )
                continue;
            var parts = trimmedLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if ( parts.Length == 2 ) {
                switch (parts[0]) {
                    case "name":
                        name = parts[1];
                        break;
                    case "host":
                        host = parts[1];
                        break;
                    case "port":
                        port = int.Parse(parts[1]);
                        break;
                    default:
                        Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: Failed to parse config for {parts[0]}:{parts[1]}");
                        break;
                }

            } else {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: Failed to parse config for {parts[0]}");
            }
        }
    }

    private HookResult onPlayerConnect (EventPlayerConnect eventInfo, GameEventInfo gameEventInfo) {
        if (eventInfo == null || eventInfo.Userid == null) 
            return HookResult.Continue;

        var player = eventInfo.Userid;

        if (player == null) 
            return HookResult.Continue;

        string query = $"INTO serverinfo_user (host, port, name, team, kills, assists, deaths) VALUES (@host, @port, @name, 0, 0, 0, 0) on duplicate key update name=@name";
        var parameters = new MySqlParameter[] {
            new MySqlParameter("@host", host),
            new MySqlParameter("@port", port),
            new MySqlParameter("@name", player.PlayerName),
        };
        try {
            if (this.db != null) {
                this.db.insert(query, parameters);
            } else {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Database instance is null.");
            }
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error inserting into table: {e.Message}");
        }
        return HookResult.Continue;
    }

    // on round end update player stats
    private HookResult onRoundEnd (EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
        if (eventInfo == null) 
            return HookResult.Continue;

        var pList = Utilities.GetPlayers ( );

        foreach (var player in pList) {
            if (player == null) 
                continue;

            string name = player.PlayerName;
            int kills = 0;
            int assists = 0;
            int deaths = 0;
            PlayerStats? stats = player.UserId.HasValue ? osbase?.GetGameStats()?.GetPlayerStats(player.UserId.Value) : null;
            if (stats != null) {
                kills = stats.kills;
                assists = stats.assists;
                deaths = stats.deaths;
            }

            string query = $"INTO serverinfo_user (host, port, name, team, kills, assists, deaths) VALUES (@host, @port, @name, @team, @kills, @assists, @deaths) on duplicate key update team=@team, kills=@kills, assists=@assists, deaths=@deaths";
            var parameters = new MySqlParameter[] {
                new MySqlParameter("@host", host),
                new MySqlParameter("@port", port),
                new MySqlParameter("@name", player.PlayerName),
                new MySqlParameter("@team", player.TeamNum),
                new MySqlParameter("@kills", kills),
                new MySqlParameter("@assists", assists),
                new MySqlParameter("@deaths", deaths)
            };
            try {
                if (this.db != null) {
                    this.db.insert(query, parameters);
                } else {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Database instance is null.");
                }
            } catch (Exception e) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error updating table: {e.Message}");
            }
        }
        return HookResult.Continue;
    }


    private HookResult onPlayerDisconnect (EventPlayerDisconnect eventInfo, GameEventInfo gameEventInfo) {
        if (eventInfo == null || eventInfo.Userid == null) 
            return HookResult.Continue;

        var player = eventInfo.Userid;

        if (player == null) 
            return HookResult.Continue;

        string query = $"FROM serverinfo_user WHERE host=@host AND port=@port AND name=@name";
        var parameters = new MySqlParameter[] {
            new MySqlParameter("@host", host),
            new MySqlParameter("@port", port),
            new MySqlParameter("@name", eventInfo.Userid.PlayerName)
        };
        try {
            if (this.db != null) {
                this.db.delete(query, parameters);
            } else {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Database instance is null.");
            }
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error deleting from table: {e.Message}");
        }
        return HookResult.Continue;
    }

    private HookResult onPlayerTeam (EventPlayerTeam eventInfo, GameEventInfo gameEventInfo) {
        if (eventInfo == null || eventInfo.Userid == null) 
            return HookResult.Continue;

        var player = eventInfo.Userid;

        if (player == null) 
            return HookResult.Continue;            

        if (player.UserId == null) 
            return HookResult.Continue;

        string name = player.PlayerName;
        int kills = 0;
        int assists = 0;
        int deaths = 0;
        PlayerStats? stats = osbase?.GetGameStats()?.GetPlayerStats(player.UserId.Value);
        if (stats != null) {
            kills = stats.kills;
            assists = stats.assists;
            deaths = stats.deaths;
        }

        string query = $"INTO serverinfo_user (host, port, name, team, kills, assists, deaths) VALUES (@host, @port, @name, @team, @kills, @assists, @deaths) on duplicate key update team=@team, kills=@kills, assists=@assists, deaths=@deaths";
        var parameters = new MySqlParameter[] {
            new MySqlParameter("@host", host),
            new MySqlParameter("@port", port),
            new MySqlParameter("@name", player.PlayerName),
            new MySqlParameter("@team", eventInfo.Team),
            new MySqlParameter("@kills", kills),
            new MySqlParameter("@assists", assists),
            new MySqlParameter("@deaths", deaths)
        };
        try {
            if (this.db != null) {
                this.db.insert(query, parameters);
            } else {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Database instance is null.");
            }
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error updating table: {e.Message}");
        }
        return HookResult.Continue;
    }

    private void onMapStart ( string mapName ) {
        this.map = mapName;
        saveServerinfo ( );
        clearUsers ( );
    }

    private void createCustomConfigs() {
        if (config == null) 
            return;
        config.CreateCustomConfig($"{ModuleName}.cfg", "// ServerInfo Configuration\nname \"Server Name\"\nhost \"cs2.oldswedes.se\"\nport 27015\n");
    }


    private void createTables ( ) {
        /* | server | CREATE TABLE `server` (
            `port` int(11) NOT NULL,
            `host` varchar(64) NOT NULL,
            `name` varchar(64) DEFAULT NULL,
            `maptype` varchar(12) DEFAULT NULL,
            `map` varchar(64) DEFAULT NULL,
            `workshop` varchar(64) DEFAULT NULL,
            `timestamp` int(11) DEFAULT 0,
            PRIMARY KEY (`host`,`port`)
            ) ENGINE=InnoDB DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci | */

        /* | user  | CREATE TABLE `user` (
            `host` varchar(64) NOT NULL,
            `port` int(11) NOT NULL,
            `name` varchar(128) NOT NULL,
            PRIMARY KEY (`host`,`port`,`name`),
            CONSTRAINT `user_ibfk_1` FOREIGN KEY (`host`, `port`) REFERENCES `server` (`host`, `port`) ON DELETE CASCADE
            ) ENGINE=InnoDB DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci | */ 

        string server = """
        TABLE IF NOT EXISTS 
        serverinfo_server (
            port int(11),
            host varchar(64),
            name varchar(64), 
            map varchar(64), 
            timestamp int(11) default 0, 
            primary key (host,port)
        ) engine=InnoDB DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;
        """;
        string user = """
        TABLE IF NOT EXISTS
        serverinfo_user (
            host varchar(64) not null, 
            port int(11) not null, 
            name varchar(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
            team int(11), 
            kills int(11),
            assists int(11),
            deaths int(11),
            primary key (host,port,name), 
            constraint foreign key (host,port) 
                references serverinfo_server (host,port) 
                on delete cascade
        ) engine=InnoDB DEFAULT CHARSET=latin1 COLLATE=latin1_swedish_ci;";
        """;
        try {
            if (this.db != null) {
                this.db.create(server);
                this.db.create(user);
            } else {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Database instance is null.");
            }
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error creating tables: {e.Message}");
        }
    }



    private void saveServerinfo() {
        string query = $"INTO serverinfo_server (host, port, name, map) VALUES (@host, @port, @name, @map) on duplicate key update name=@name, map=@map";
        var parameters = new MySqlParameter[] {
            new MySqlParameter("@host", host),
            new MySqlParameter("@port", port),
            new MySqlParameter("@name", name),
            new MySqlParameter("@map", map)
        };

        try {
            if (this.db != null) {
                this.db.insert(query, parameters);
            } else {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Database instance is null.");
            }
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error inserting into table: {e.Message}");
        }

    }

    private void clearUsers() {
        if (this.db == null) 
            return;
        this.db.delete("from serverinfo_user where host=@host and port=@port", new MySqlParameter[] {
            new MySqlParameter("@host", host),
            new MySqlParameter("@port", port)
        });
    }
}