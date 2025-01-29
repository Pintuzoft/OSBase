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

        // Gather data for all connected players
        foreach (var player in playersList) {
            if (player.IsValid && !player.IsBot && player.Connected == PlayerConnectedState.PlayerConnected) {
                if (player.UserId.HasValue) {
                    playerIds.Add(player.UserId.Value);
                }
                playerScores.Add(player.Score); // Assuming `Score` is the player's score
                playerTeams.Add(player.TeamNum); // Assuming `TeamNum` is the player's team
            }
        }

        // Count players on each team
        int tCount = playerTeams.Count(t => t == TEAM_T);
        int ctCount = playerTeams.Count(t => t == TEAM_CT);

        // Check if one team is 2+ players larger than the other
        if (Math.Abs(tCount - ctCount) >= 2) {
            int largerTeam = tCount > ctCount ? TEAM_T : TEAM_CT;
            int smallerTeam = tCount > ctCount ? TEAM_CT : TEAM_T;

            // Get players on the larger team, sorted by score (ascending)
            var playersToMove = playerIds
                .Select((id, index) => new { Id = id, Score = playerScores[index], Team = playerTeams[index] })
                .Where(p => p.Team == largerTeam)
                .OrderBy(p => p.Score)
                .Take(tCount - ctCount - 1) // Adjust to even out the teams
                .ToList();

            // Mark players to switch teams on the next round
            foreach (var p in playersToMove) {
                CCSPlayerController? player = Utilities.GetPlayerFromUserid(p.Id);
                
                if (player != null) {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Marking player {player.PlayerName} to switch teams on next round.");
                    player.SwitchTeamsOnNextRoundReset = true;  // This will switch them to the other team at the start of the next round
                } else {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Player with ID {p.Id} not found.");
                }
            }
        }
        return HookResult.Continue;
    }


}