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


namespace OSBase.Modules;

using System.Data;
using System.IO;
using System.Reflection.Metadata;
using System.Xml;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;

public class TeamBalancer : IModule {
    public string ModuleName => "teambalancer";   
    private OSBase? osbase;
    private Config? config;
    private const int TEAM_T = (int)CsTeam.Terrorist; // TERRORIST team ID
    private const int TEAM_CT = (int)CsTeam.CounterTerrorist; // COUNTER-TERRORIST team ID
    private Dictionary<string, int> mapBombsites = new Dictionary<string, int>();
    private string mapConfigFile = "teambalancer_mapinfo.cfg";

    private int bombsites = 2;

    //float delay = 5.0f;

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
            loadEventHandlers();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
        } else {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {ModuleName} is disabled in the global configuration.");
        }

        // Load map info
        LoadMapInfo();

    }

    private void loadEventHandlers() {
        if(osbase == null) return;
        osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);
    }

    private void LoadMapInfo() {
        config?.CreateCustomConfig($"{mapConfigFile}", "// Map info\nde_dust2 2\n");

        List<string> maps = config?.FetchCustomConfig($"{mapConfigFile}") ?? new List<string>();

        foreach (var line in maps) {
            string trimmedLine = line.Trim();
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//")) continue;

            var parts = trimmedLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) {
                mapBombsites[parts[0]] = int.Parse(parts[1]);
                Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Loaded map info: {parts[0]} = {parts[1]}");
            }
        }
    }

    private void OnMapStart(string mapName) {
        if (mapBombsites.ContainsKey(mapName)) {
            bombsites = mapBombsites[mapName];
            Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Map {mapName} started. Bombsites: {bombsites}");
        } else {
            bombsites = 2;
            if ( mapName.Contains("cs_") ) {
                bombsites = 1;
            }
            config?.AddCustomConfigLine($"{mapConfigFile}", $"{mapName} {bombsites}");
            Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Map {mapName} started. Default bombsites: {bombsites}");
        }
    }

    private HookResult OnRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
        var playersList = Utilities.GetPlayers();
        List<int> playerIds = new List<int>();
        List<int> playerScores = new List<int>();
        List<int> playerTeams = new List<int>();

        // Gather data for all connected players (including bots)
        Console.WriteLine("[DEBUG] OSBase[{ModuleName}] - Gathering player data...");
        foreach (var player in playersList) {
            if (player.Connected == PlayerConnectedState.PlayerConnected) {
                if (player.UserId.HasValue) {
                    playerIds.Add(player.UserId.Value);
                    playerScores.Add(player.Score); // Assuming `Score` is the player's score
                    playerTeams.Add(player.TeamNum); // Assuming `TeamNum` is the player's team

                    // Log the actual team number for debugging
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Player {player.PlayerName} ({player.UserId.Value}) is on team {player.TeamNum}. " +
                                    $"Expected TEAM_T: {TEAM_T}, TEAM_CT: {TEAM_CT}");
                }
            }
        }

        // Count players on each team
        int tCount = playerTeams.Count(t => t == TEAM_T);  // TEAM_T is for TERRORIST team
        int ctCount = playerTeams.Count(t => t == TEAM_CT); // TEAM_CT is for COUNTER-TERRORIST team
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - T team size: {tCount}, CT team size: {ctCount}");

        // Calculate imbalance (absolute difference between teams)
        int imbalance = Math.Abs(tCount - ctCount);
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Calculated imbalance: {imbalance}");

        int playersToMove = 0;

        // Determine how many players to move based on bombsite configuration
        if (bombsites == 2) {
            if (tCount > ctCount) {
                playersToMove = tCount - ctCount - 1; // Move enough players from T to CT to balance
                Console.WriteLine("[DEBUG] OSBase[{ModuleName}] - Bombsites = 2, CT should have 1 more player than T.");
            }
        } else if (bombsites == 1 || bombsites == 0) {
            if (ctCount > tCount) {
                playersToMove = ctCount - tCount - 1; // Move enough players from CT to T to balance
                Console.WriteLine("[DEBUG] OSBase[{ModuleName}] - Bombsites = 1 or 0, T should have 1 more player than CT.");
            }
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Players to move: {playersToMove}");

        // Only move players if there is a true imbalance
        if (imbalance > 0 && playersToMove > 0) {
            // Ensure we don't move more than necessary
            playersToMove = Math.Min(playersToMove, imbalance);  // Limit the number of players to the imbalance
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Moving {playersToMove} players to balance the teams.");

            // Select players from the larger team (T or CT) based on the imbalance
            var playersToMoveList = playerIds
                .Select((id, index) => new { Id = id, Score = playerScores[index], Team = playerTeams[index] })
                .Where(p => p.Team == (tCount > ctCount ? TEAM_T : TEAM_CT))  // Select the larger team
                .OrderBy(p => p.Score)  // Sort by score (ascending) to move the lowest-scoring bots first
                .Take(playersToMove)  // Only move the required number of players
                .ToList();

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Selected {playersToMoveList.Count} players to move to balance the teams.");

            // Log details about players selected for movement
            foreach (var p in playersToMoveList) {
                CCSPlayerController? player = Utilities.GetPlayerFromUserid(p.Id);

                if (player != null) {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Moving player {player.PlayerName} ({p.Id}) from team {player.TeamNum} to the other team.");
                    player.SwitchTeamsOnNextRoundReset = true;  // This will switch them to the other team at the start of the next round
                } else {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Player with ID {p.Id} not found.");
                }
            }
        } else {
            Console.WriteLine("[DEBUG] OSBase[{ModuleName}] - No team balancing needed. Teams are sufficiently balanced.");
        }
        return HookResult.Continue;
    }

}