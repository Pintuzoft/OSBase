using System;
using System.Linq;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules {

    public class TeamBalancer : IModule {
        public string ModuleName => "teambalancer";   
        public string ModuleNameNice => "TeamBalancer";
        private OSBase? osbase;
        private Config? config;
        // Reference to the GameStats module.
        private GameStats? gameStats;
        // Capture the main thread's SynchronizationContext.
        private System.Threading.SynchronizationContext? mainContext;

        // Immunity set for skill balancing; stores UserIDs that were swapped this round.
        private HashSet<int> immunePlayers = new HashSet<int>();

        private const int TEAM_T = (int)CsTeam.Terrorist;      // Expected value: 2
        private const int TEAM_CT = (int)CsTeam.CounterTerrorist; // Expected value: 3

        private Dictionary<string, int> mapBombsites = new Dictionary<string, int>();
        private string mapConfigFile = "teambalancer_mapinfo.cfg";
        private int bombsites = 2;

        // Win streak counters (updated on round end)
        private int winStreakT = 0;
        private int winStreakCT = 0;
       
        private const float delay = 5.0f;

        public void Load(OSBase inOsbase, Config inConfig) {
            this.osbase = inOsbase;
            this.config = inConfig;
            // Capture the main thread's SynchronizationContext.
            mainContext = System.Threading.SynchronizationContext.Current;

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
                //osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);
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

        // OnRoundEnd updates win streak counters then calls BalanceTeams immediately.
        private HookResult OnRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - OnRoundEnd triggered.");
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
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Win streaks updated: T: {winStreakT}, CT: {winStreakCT}");
            osbase?.AddTimer(delay, () => {
                var playerList = Utilities.GetPlayers();
                foreach (var p in playerList) {
                    if (p.UserId.HasValue) {
                        if ( p.PlayerName == "Pintuz" ) {
                            CsTeam to = p.TeamNum == TEAM_T ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
                            p.SwitchTeam(to);
                        }
                 
                    }
                }

                //BalanceTeams();
            });
            //BalanceTeams();
            return HookResult.Continue;
        }

        // OnWarmupEnd calls BalanceTeams.
        private HookResult OnWarmupEnd(EventWarmupEnd eventInfo, GameEventInfo gameEventInfo) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - OnWarmupEnd triggered.");
            osbase?.AddTimer(delay, () => {
                BalanceTeams();
            });    
            return HookResult.Continue;
        }

        // OnStartHalftime resets win streak counters.
        private HookResult OnStartHalftime(EventStartHalftime eventInfo, GameEventInfo gameEventInfo) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - OnStartHalftime triggered.");
            winStreakT = 0;
            winStreakCT = 0;
            return HookResult.Continue;
        }

        // OnRoundStart clears the immunity set.
//        private HookResult OnRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {
//            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - OnRoundStart triggered. Balancing teams.");
//            osbase.AddTimer(delay, BalanceTeams);
//            BalanceTeams();
//            return HookResult.Continue;
//        }

        private void BalanceTeams() {
            return;
            // This method must run on the main thread.
            var playersList = Utilities.GetPlayers();
            // Filter out non-connected players, missing UserId, and HLTV/demorecorder clients.
            var connectedPlayers = playersList
                .Where(player => player.Connected == PlayerConnectedState.PlayerConnected &&
                                 player.UserId.HasValue &&
                                 !player.IsHLTV)
                .ToList();

            int totalPlayers = connectedPlayers.Count;
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Connected players count (excluding HLTV): {totalPlayers}");
            if (totalPlayers == 0) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - No connected players found.");
                return;
            }

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Player list with kill counts from GameStats:");
            foreach (var p in connectedPlayers) {
                int kills = gameStats!.GetPlayerStats(p.UserId!.Value).Kills;
                string teamName = (p.TeamNum == TEAM_T ? "T" : (p.TeamNum == TEAM_CT ? "CT" : p.TeamNum.ToString()));
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Player: {p.PlayerName} (ID: {p.UserId}), Team: {teamName}, KillCount: {kills}");
            }

            int tCount = connectedPlayers.Count(p => p.TeamNum == TEAM_T);
            int ctCount = connectedPlayers.Count(p => p.TeamNum == TEAM_CT);

            int idealT, idealCT;
            if (bombsites >= 2) {
                idealT = totalPlayers / 2;
                idealCT = totalPlayers / 2 + totalPlayers % 2;
            } else {
                idealT = totalPlayers / 2 + totalPlayers % 2;
                idealCT = totalPlayers / 2;
            }
            bool isTeamsBalanced = (tCount == idealT && ctCount == idealCT);
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Total: {totalPlayers}, T: {tCount} (ideal: {idealT}), CT: {ctCount} (ideal: {idealCT})");

            if ( ! isTeamsBalanced ) {
                // Count balancing: move low kill-count players from the team that is over the ideal count.
                int playersToMove = 0;
                bool moveFromT = false;
                if (tCount > idealT) {
                    playersToMove = tCount - idealT;
                    moveFromT = true;
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Bombsites = {bombsites}: Moving {playersToMove} player(s) from T to CT.");
                } else if (ctCount > idealCT) {
                    playersToMove = ctCount - idealCT;
                    moveFromT = false;
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Bombsites = {bombsites}: Moving {playersToMove} player(s) from CT to T.");
                }

                var playersToSwitch = connectedPlayers
                    .Where(p => moveFromT ? p.TeamNum == TEAM_T : p.TeamNum == TEAM_CT)
                    .Select(p => new { 
                        Id = p.UserId!.Value, 
                        KillCount = gameStats?.GetPlayerStats(p.UserId.Value).Kills, 
                        Name = p.PlayerName 
                    })
                    .OrderBy(p => p.KillCount)
                    .Take(playersToMove)
                    .ToList();

                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Selected {playersToSwitch.Count} candidate(s) for count balancing.");
                foreach (var candidate in playersToSwitch) {
                    var player = Utilities.GetPlayerFromUserid(candidate.Id);
                    if (player != null) {
                        int targetTeam = moveFromT ? TEAM_CT : TEAM_T;
                        string moveDirection = moveFromT ? "T->CT" : "CT->T";
                        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Switching player '{candidate.Name}' (ID: {candidate.Id}) from {(moveFromT ? "T" : "CT")} to {(moveFromT ? "CT" : "T")} immediately.");
                        player.SwitchTeam((CsTeam)targetTeam);
                        immunePlayers.Add(player.UserId!.Value);
                        Server.PrintToChatAll($"{ChatColors.DarkRed}[{ModuleNameNice}]: Moved player {candidate.Name}: {moveDirection}");
                    } else {
                        Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Could not find player with ID {candidate.Id}.");
                    }
                }
                winStreakT = 0;
                winStreakCT = 0;
                return;
            } else {
                Console.WriteLine("$[DEBUG] OSBase[{ModuleName}] - Teams are balanced by size.");
                // Skill balancing: only perform if win streak conditions are met.
                if (totalPlayers >= 4 && totalPlayers <= 12 && (winStreakT >= 3 || winStreakCT >= 3)) {
                    int winningTeam = winStreakT >= 3 ? TEAM_T : TEAM_CT;
                    int losingTeam = winningTeam == TEAM_T ? TEAM_CT : TEAM_T;
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Skill balancing: Winning team ({(winningTeam == TEAM_T ? "T" : "CT")}) has a win streak.");

                    // Filter out players that are immune.
                    var winningTeamCandidates = connectedPlayers
                        .Where(p => p.TeamNum == winningTeam && !immunePlayers.Contains(p.UserId!.Value))
                        .OrderByDescending(p => gameStats?.GetPlayerStats(p.UserId!.Value).Kills)
                        .ToList();
                    var losingTeamCandidates = connectedPlayers
                        .Where(p => p.TeamNum == losingTeam && !immunePlayers.Contains(p.UserId!.Value))
                        .OrderBy(p => gameStats?.GetPlayerStats(p.UserId!.Value).Kills)
                        .ToList();

                    // Require at least two non-immune players per team.
                    if (winningTeamCandidates.Count >= 2 && losingTeamCandidates.Count >= 2) {
                        var playerFromWinning = winningTeamCandidates[1]; // Second best from winning team.
                        var playerFromLosing = losingTeamCandidates[0];     // Worst from losing team.
                        
                        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Skill balancing swap scheduled: Switching second best '{playerFromWinning.PlayerName}' (ID: {playerFromWinning.UserId}) with worst '{playerFromLosing.PlayerName}' (ID: {playerFromLosing.UserId}) immediately.");
                        //playerFromWinning.SwitchTeam((CsTeam)losingTeam);
                        //playerFromLosing.SwitchTeam((CsTeam)winningTeam);
                        playerFromWinning.ChangeTeam((CsTeam)losingTeam);
                        playerFromLosing.ChangeTeam((CsTeam)winningTeam);
                        // Add these players to immunity so they won't be swapped again soon.
                        immunePlayers.Clear();
                        immunePlayers.Add(playerFromWinning.UserId!.Value);
                        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - {playerFromWinning.PlayerName} is now immune.");
                        immunePlayers.Add(playerFromLosing.UserId!.Value);
                        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - {playerFromLosing.PlayerName} is now immune.");
                        string winningTeamName = winningTeam == TEAM_T ? "T" : "CT";
                        string losingTeamName = losingTeam == TEAM_T ? "T" : "CT";
                        Server.PrintToChatAll($"{ChatColors.DarkRed}[{ModuleNameNice}]: Swapped players [{winningTeamName}] {playerFromWinning.PlayerName} <-> [{losingTeamName}] {playerFromLosing.PlayerName}");
                        winStreakT = 0;
                        winStreakCT = 0;
                    } else {
                        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Not enough non-immune players available for a skill balancing swap. Resetting immunity.");
                        immunePlayers.Clear();
                    }
                } else {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Skill balancing conditions not met.");
                }
            }
        }
    }
}