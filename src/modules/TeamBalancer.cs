using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks; // For async/await and Task.Delay
using System.Threading;     // For SynchronizationContext
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules {

    public class TeamBalancer : IModule {
        public string ModuleName => "teambalancer";   
        private OSBase? osbase;
        private Config? config;
        // Reference to the GameStats module.
        private GameStats? gameStats;
        // Capture the main thread's SynchronizationContext.
        private SynchronizationContext? mainContext;

        private const int TEAM_T = (int)CsTeam.Terrorist;      // Expected value: 2
        private const int TEAM_CT = (int)CsTeam.CounterTerrorist; // Expected value: 3

        private Dictionary<string, int> mapBombsites = new Dictionary<string, int>();
        private string mapConfigFile = "teambalancer_mapinfo.cfg";
        private int bombsites = 2;

        // Win streak counters (updated on round end)
        private int winStreakT = 0;
        private int winStreakCT = 0;

        // Delay in milliseconds before running the entire balancing routine.
        private const float delay = 0.0f;

        public void Load(OSBase inOsbase, Config inConfig) {
            this.osbase = inOsbase;
            this.config = inConfig;
            // Capture the SynchronizationContext on the main thread.
            mainContext = SynchronizationContext.Current;

            config.RegisterGlobalConfigValue($"{ModuleName}", "1");

            // Retrieve the GameStats module.
            gameStats = osbase.GetGameStats();

            if (osbase == null) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] osbase is null. {ModuleName} failed to load.");
                return;
            }
            if (config == null) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] config is null. {ModuleName} failed to load.");
                return;
            }

            var globalConfig = config.GetGlobalConfigValue($"{ModuleName}", "0");
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Global config value: {globalConfig}");
            if (globalConfig == "1") {
                loadEventHandlers();
                LoadMapInfo();
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
            } else {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {ModuleName} is disabled in the global configuration.");
            }
        }

        private void loadEventHandlers() {
            if(osbase == null) return;
            try {
                osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
                osbase.RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
                osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);
                osbase.RegisterEventHandler<EventStartHalftime>(OnStartHalftime);
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Event handlers registered successfully.");
            } catch(Exception ex) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Failed to register event handlers: {ex.Message}");
            }
        }

        private void LoadMapInfo() {
            config?.CreateCustomConfig($"{mapConfigFile}", "// Map info\nde_dust2 2\n");
            List<string> maps = config?.FetchCustomConfig($"{mapConfigFile}") ?? new List<string>();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Loaded {maps.Count} line(s) from {mapConfigFile}.");

            foreach (var line in maps) {
                string trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//"))
                    continue;
                var parts = trimmedLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[1], out int bs)) {
                    mapBombsites[parts[0]] = bs;
                    Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Loaded map info: {parts[0]} = {bs}");
                } else {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: Failed to parse bombsites for map {parts[0]}");
                }
            }
        }

        private void OnMapStart(string mapName) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - OnMapStart triggered for map: {mapName}");
            if (mapBombsites.ContainsKey(mapName)) {
                bombsites = mapBombsites[mapName];
                Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Map {mapName} started. Bombsites: {bombsites}");
            } else {
                bombsites = mapName.Contains("cs_") ? 1 : 2;
                config?.AddCustomConfigLine($"{mapConfigFile}", $"{mapName} {bombsites}");
                Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Map {mapName} started. Default bombsites: {bombsites}");
            }
        }

        // OnRoundEnd updates win streak counters then delays the balancing routine.
        private HookResult OnRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
            Console.WriteLine("[DEBUG] OSBase[teambalancer] - OnRoundEnd triggered.");

            int winner = eventInfo.Winner;
            if (winner == TEAM_T) { 
                winStreakT++; 
                winStreakCT = 0; 
            } else if (winner == TEAM_CT) { 
                winStreakCT++; 
                winStreakT = 0; 
            } else {
                winStreakT = 0;
                winStreakCT = 0;
            }
            Console.WriteLine($"[DEBUG] OSBase[teambalancer] - Win streaks updated: T: {winStreakT}, CT: {winStreakCT}");

            delayedBalanceTeams();
            return HookResult.Continue;
        }

        // OnWarmupEnd delays the balancing routine.
        private HookResult OnWarmupEnd(EventWarmupEnd eventInfo, GameEventInfo gameEventInfo) {
            Console.WriteLine("[DEBUG] OSBase[teambalancer] - OnWarmupEnd triggered.");
            delayedBalanceTeams();
            return HookResult.Continue;
        }

        // OnStartHalftime resets win streak counters.
        private HookResult OnStartHalftime(EventStartHalftime eventInfo, GameEventInfo gameEventInfo) {
            Console.WriteLine("[DEBUG] OSBase[teambalancer] - OnStartHalftime triggered.");
            winStreakT = 0;
            winStreakCT = 0;
            return HookResult.Continue;
        }

        /// Delays the balancing routine by BalanceDelayMs, then schedules BalanceTeams() to run on the main thread.
        private void delayedBalanceTeams() {
            //osbase?.AddTimer(delay, () => {
                BalanceTeams();
            //});
        }

        private void BalanceTeams() {
            var playersList = Utilities.GetPlayers();
            // Filter out non-connected players, those missing UserId, and HLTV/demorecorder clients.
            var connectedPlayers = playersList
                .Where(player => player.Connected == PlayerConnectedState.PlayerConnected
                        && player.UserId.HasValue
                        && !player.IsHLTV)
                .ToList();

            int totalPlayers = connectedPlayers.Count;
            Console.WriteLine($"[DEBUG] OSBase[teambalancer] - Connected players count (excluding HLTV): {totalPlayers}");
            if (totalPlayers == 0) {
                Console.WriteLine("[DEBUG] OSBase[teambalancer] - No connected players found.");
                return;
            }

            // --- DEBUG: Print player list with kill counts from GameStats ---
            Console.WriteLine("[DEBUG] OSBase[teambalancer] - Player list with kill counts from GameStats:");
            foreach (var p in connectedPlayers) {
                int kills = gameStats!.GetPlayerStats(p.UserId!.Value).Kills;
                string teamName = (p.TeamNum == TEAM_T ? "Terrorists" : (p.TeamNum == TEAM_CT ? "CT" : p.TeamNum.ToString()));
                Console.WriteLine($"[DEBUG] OSBase[teambalancer] - Player: {p.PlayerName} (ID: {p.UserId}), Team: {teamName}, KillCount: {kills}");
            }
            // ----------------------------------------------------------

            int tCount = connectedPlayers.Count(p => p.TeamNum == TEAM_T);
            int ctCount = connectedPlayers.Count(p => p.TeamNum == TEAM_CT);

            // Calculate ideal team sizes based on bombsites.
            int idealT, idealCT;
            if (bombsites >= 2) {
                idealT = totalPlayers / 2;
                idealCT = totalPlayers / 2 + totalPlayers % 2;
            } else {
                idealT = totalPlayers / 2 + totalPlayers % 2;
                idealCT = totalPlayers / 2;
            }
            bool isTeamsBalanced = (tCount == idealT && ctCount == idealCT);
            Console.WriteLine($"[DEBUG] OSBase[teambalancer] - Total: {totalPlayers}, T: {tCount} (ideal: {idealT}), CT: {ctCount} (ideal: {idealCT})");

            if (!isTeamsBalanced) {
                // Count balancing: move low kill-count players from the team that is over the ideal count.
                int playersToMove = 0;
                bool moveFromT = false;
                if (tCount > idealT) {
                    playersToMove = tCount - idealT;
                    moveFromT = true;
                    Console.WriteLine($"[DEBUG] OSBase[teambalancer] - Bombsites = {bombsites}: Moving {playersToMove} player(s) from Terrorists to CT.");
                } else if (ctCount > idealCT) {
                    playersToMove = ctCount - idealCT;
                    moveFromT = false;
                    Console.WriteLine($"[DEBUG] OSBase[teambalancer] - Bombsites = {bombsites}: Moving {playersToMove} player(s) from CT to Terrorists.");
                }

                var playersToSwitch = connectedPlayers
                    .Where(p => moveFromT ? p.TeamNum == TEAM_T : p.TeamNum == TEAM_CT)
                    .Select(p => new { 
                        Id = p.UserId!.Value, 
                        KillCount = gameStats?.GetPlayerStats(p.UserId.Value).Kills, 
                        Name = p.PlayerName 
                    })
                    .OrderBy(p => p.KillCount) // lowest kill count first
                    .Take(playersToMove)
                    .ToList();

                Console.WriteLine($"[DEBUG] OSBase[teambalancer] - Selected {playersToSwitch.Count} candidate(s) for count balancing.");
                foreach (var candidate in playersToSwitch) {
                    var player = Utilities.GetPlayerFromUserid(candidate.Id);
                    if (player != null) {
                        int targetTeam = moveFromT ? TEAM_CT : TEAM_T;
                        string moveDirection = moveFromT ? "T->CT" : "CT->T";
                        Console.WriteLine($"[DEBUG] OSBase[teambalancer] - Switching player '{candidate.Name}' (ID: {candidate.Id}) from {(moveFromT ? "Terrorists" : "CT")} to {(moveFromT ? "CT" : "Terrorists")} immediately.");
                        player.SwitchTeam((CsTeam)targetTeam);
                        Server.PrintToChatAll($"[TeamBalancer]: Moved player {candidate.Name}: {moveDirection}");
                    } else {
                        Console.WriteLine($"[ERROR] OSBase[teambalancer] - Could not find player with ID {candidate.Id}.");
                    }
                }
                // Reset win streak counters since teams have been rebalanced.
                winStreakT = 0;
                winStreakCT = 0;
                return;
            } else {
                // Teams are balanced by size. Now check for skill balancing.
                Console.WriteLine("[DEBUG] OSBase[teambalancer] - Teams are balanced by size.");
                if (totalPlayers >= 4 && (winStreakT >= 3 || winStreakCT >= 3)) {
                    int winningTeam = winStreakT >= 3 ? TEAM_T : TEAM_CT;
                    int losingTeam = winningTeam == TEAM_T ? TEAM_CT : TEAM_T;
                    Console.WriteLine($"[DEBUG] OSBase[teambalancer] - Skill balancing: Winning team ({(winningTeam == TEAM_T ? "Terrorists" : "CT")}) has a win streak.");

                    var winningTeamPlayers = connectedPlayers
                        .Where(p => p.TeamNum == winningTeam)
                        .OrderByDescending(p => gameStats?.GetPlayerStats(p.UserId!.Value).Kills) // best (highest kill count) first
                        .ToList();
                    var losingTeamPlayers = connectedPlayers
                        .Where(p => p.TeamNum == losingTeam)
                        .OrderBy(p => gameStats?.GetPlayerStats(p.UserId!.Value).Kills) // worst (lowest kill count) first
                        .ToList();

                    if (winningTeamPlayers.Count >= 2 && losingTeamPlayers.Count >= 1) {
                        var playerFromWinning = winningTeamPlayers[1];
                        var playerFromLosing = losingTeamPlayers[0];

                        Console.WriteLine($"[DEBUG] OSBase[teambalancer] - Skill balancing swap scheduled: Switching second best '{playerFromWinning.PlayerName}' (ID: {playerFromWinning.UserId}) with worst '{playerFromLosing.PlayerName}' (ID: {playerFromLosing.UserId}) immediately.");
                        playerFromWinning.SwitchTeam((CsTeam)losingTeam);
                        playerFromLosing.SwitchTeam((CsTeam)winningTeam);
                        string winningTeamName = winningTeam == TEAM_T ? "Terrorists" : "CT";
                        string losingTeamName = losingTeam == TEAM_T ? "Terrorists" : "CT";
                        Server.PrintToChatAll($"[TeamBalancer]: Skill-balancing swap: Moved player {playerFromWinning.PlayerName}: {winningTeamName}->{losingTeamName}");
                        Server.PrintToChatAll($"[TeamBalancer]: Skill-balancing swap: Moved player {playerFromLosing.PlayerName}: {losingTeamName}->{winningTeamName}");
                        winStreakT = 0;
                        winStreakCT = 0;
                    } else {
                        Console.WriteLine("[DEBUG] OSBase[teambalancer] - Not enough players on one or both teams for a skill balancing swap.");
                    }
                } else {
                    Console.WriteLine("[DEBUG] OSBase[teambalancer] - Skill balancing conditions not met.");
                }
            }
        }
    }
}