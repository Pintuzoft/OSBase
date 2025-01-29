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

using System.IO;
using CounterStrikeSharp.API.Modules.Utils;

public class TeamBalancer : IModule {
    public string ModuleName => "teambalancer";   
    private OSBase? osbase;
    private Config? config;
    private const int TEAM_T = (int)CsTeam.Terrorist; // TERRORIST team ID
    private const int TEAM_CT = (int)CsTeam.CounterTerrorist; // COUNTER-TERRORIST team ID
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
    }

    private void loadEventHandlers() {
        if(osbase == null) return;
        osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
    }

    private HookResult OnRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
        var playersList = Utilities.GetPlayers();
        List<int> playerIds = new List<int>();
        List<int> playerScores = new List<int>();
        List<int> playerTeams = new List<int>();

        // Track if Pintuz (you) is in the players list
        bool isPintuzFound = false;

        // Gather data for all connected players
        foreach (var player in playersList) {
            if (player.IsValid && player.Connected == PlayerConnectedState.PlayerConnected) {
                if (player.UserId.HasValue) {
                    playerIds.Add(player.UserId.Value);
                    playerScores.Add(player.Score); // Assuming `Score` is the player's score
                    playerTeams.Add(player.TeamNum); // Assuming `TeamNum` is the player's team

                }
            }
        }

        // If Pintuz is not found in the players list, check again after a slight delay
        if (!isPintuzFound) {
            Console.WriteLine("[ERROR] OSBase[{ModuleName}] - Pintuz (You) not found in the players list, checking player states...");

            // Explicitly add yourself to the list
            var pintuz = playersList.FirstOrDefault(p => p.PlayerName == "Pintuz");
            if (pintuz != null) {
                isPintuzFound = true;
                playerIds.Add(pintuz.UserId.Value);
                playerScores.Add(pintuz.Score); // Assuming `Score` is the player's score
                playerTeams.Add(pintuz.TeamNum); // Assuming `TeamNum` is the player's team
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Manually added Pintuz (You) to the team list.");
            }
        }

        // Add bots to the count and log their team data
        foreach (var player in playersList) {
            if (player.IsBot && player.Connected == PlayerConnectedState.PlayerConnected) {
                playerIds.Add(player.UserId.Value);
                playerScores.Add(player.Score); // Assuming `Score` is the bot's score
                playerTeams.Add(player.TeamNum); // Assuming `TeamNum` is the bot's team
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Bot {player.PlayerName} is on team {player.TeamNum}.");
            }
        }

        // Count players on each team
        int tCount = playerTeams.Count(t => t == TEAM_T);
        int ctCount = playerTeams.Count(t => t == TEAM_CT);
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - T team size: {tCount}, CT team size: {ctCount}");

        // Check if one team is 2+ players larger than the other
        if (Math.Abs(tCount - ctCount) >= 2) {
            int largerTeam = tCount > ctCount ? TEAM_T : TEAM_CT;
            int smallerTeam = tCount > ctCount ? TEAM_CT : TEAM_T;

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Balancing teams: larger team: {largerTeam}, smaller team: {smallerTeam}");

            // Get players on the larger team, sorted by score (ascending)
            var playersToMove = playerIds
                .Select((id, index) => new { Id = id, Score = playerScores[index], Team = playerTeams[index] })
                .Where(p => p.Team == largerTeam)
                .OrderBy(p => p.Score)
                .Take(Math.Abs(tCount - ctCount) - 1) // Adjust to even out the teams
                .ToList();

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Selected {playersToMove.Count} players to move.");

            // Mark players to switch teams on the next round
            foreach (var p in playersToMove) {
                CCSPlayerController? player = Utilities.GetPlayerFromUserid(p.Id);
                
                if (player != null) {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Marking player {player.PlayerName} ({p.Id}) to switch teams on next round.");
                    player.SwitchTeamsOnNextRoundReset = true;  // This will switch them to the other team at the start of the next round
                } else {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Player with ID {p.Id} not found.");
                }
            }
        } else {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - No team balancing needed.");
        }
        return HookResult.Continue;
    }

}