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

        private const float delay = 6.5f;
        private const float warmupDelay = 5.0f;

        private bool warmup = false;

        private int minPlayers = 4;
        private int maxPlayers = 16;

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
            warmup = false;
            osbase?.AddTimer(delay, () => {
                BalanceTeams();
            });    
            return HookResult.Continue;
        }

        // OnWarmupEnd calls BalanceTeams.
        private HookResult OnWarmupEnd(EventWarmupEnd eventInfo, GameEventInfo gameEventInfo) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - OnWarmupEnd triggered.");
            warmup = true;
            osbase?.AddTimer(warmupDelay, () => {
                BalanceTeams();
            });
            return HookResult.Continue;
        }

        private void BalanceTeams() {
            // Retrieve game stats and connected players.
            var playersList = Utilities.GetPlayers();
            var gameStats = osbase?.GetGameStats();
            if (gameStats == null)
                return;

            int winStreakT = gameStats.getTeam(TEAM_T).streak;
            int winStreakCT = gameStats.getTeam(TEAM_CT).streak;
            int winsT = gameStats.getTeam(TEAM_T).wins;
            int winsCT = gameStats.getTeam(TEAM_CT).wins;

            // Filter connected players (excluding HLTV/demorecorder clients)
            var connectedPlayers = playersList.Where(p =>
                p.Connected == PlayerConnectedState.PlayerConnected &&
                p.UserId.HasValue &&
                !p.IsHLTV).ToList();

            int totalPlayers = connectedPlayers.Count;
            if (totalPlayers == 0)
                return;

            // Determine ideal team sizes.
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
            bool sizesBalanced = (tCount == idealT && ctCount == idealCT);

            // Compute team average skills using GameStats helper methods.
            float avgSkillT = gameStats.GetTeamAverageSkill(TEAM_T);
            float avgSkillCT = gameStats.GetTeamAverageSkill(TEAM_CT);

            // ---------------- COUNT BALANCING (if team sizes are imbalanced) ----------------
            if (!sizesBalanced) {
                bool moveFromT = (tCount > idealT);
                int playersToMove = Math.Abs(moveFromT ? tCount - idealT : ctCount - idealCT);

                // Optionally, if only one player is to be moved and a win streak protects the underdog team, skip.
                if (playersToMove == 1 && ((moveFromT && winsCT - winsT >= 3) || (!moveFromT && winsT - winsCT >= 3)))
                    return;

                for (int i = 0; i < playersToMove; i++) {
                    // Build candidate lists from the overpopulated and underpopulated teams.
                    var overTeamPlayers = connectedPlayers
                        .Where(p => p.TeamNum == (moveFromT ? TEAM_T : TEAM_CT) &&
                                    p.UserId.HasValue && !immunePlayers.Contains(p.UserId.Value))
                        .ToList();
                    var underTeamPlayers = connectedPlayers
                        .Where(p => p.TeamNum == (moveFromT ? TEAM_CT : TEAM_T) &&
                                    p.UserId.HasValue && !immunePlayers.Contains(p.UserId.Value))
                        .ToList();
                    if (!overTeamPlayers.Any() || !underTeamPlayers.Any())
                        break;

                    // Current difference in average skill between teams (using GameStats methods)
                    float currentDiff = Math.Abs(
                        gameStats.GetTeamAverageSkill(moveFromT ? TEAM_T : TEAM_CT) -
                        gameStats.GetTeamAverageSkill(moveFromT ? TEAM_CT : TEAM_T));

                    float bestNewDiff = currentDiff;
                    CCSPlayerController? candidateOver = null;
                    CCSPlayerController? candidateUnder = null;

                    // Simulate swapping each candidate pair to see which pair minimizes the difference.
                    foreach (var over in overTeamPlayers) {
                        float skillOver = over.UserId.HasValue ? gameStats.GetPlayerStats(over.UserId.Value).calcSkill() : 0;
                        foreach (var under in underTeamPlayers) {
                            float skillUnder = under.UserId.HasValue ? gameStats.GetPlayerStats(under.UserId.Value).calcSkill() : 0;
                            int countOver = moveFromT ? tCount : ctCount;
                            int countUnder = moveFromT ? ctCount : tCount;

                            float newTotalOver = gameStats.GetTeamTotalSkill(moveFromT ? TEAM_T : TEAM_CT) - skillOver + skillUnder;
                            float newTotalUnder = gameStats.GetTeamTotalSkill(moveFromT ? TEAM_CT : TEAM_T) - skillUnder + skillOver;
                            float newAvgOver = newTotalOver / countOver;
                            float newAvgUnder = newTotalUnder / countUnder;
                            float newDiff = Math.Abs(newAvgOver - newAvgUnder);

                            if (newDiff < bestNewDiff) {
                                bestNewDiff = newDiff;
                                candidateOver = over;
                                candidateUnder = under;
                            }
                        }
                    }
                    // If a candidate pair is found that reduces the skill gap, perform the swap.
                    if (candidateOver != null && candidateUnder != null) {
                        int targetTeam = moveFromT ? TEAM_CT : TEAM_T;
                        if (warmup) {
                            candidateOver.ChangeTeam((CsTeam)targetTeam);
                            candidateUnder.ChangeTeam((CsTeam)(moveFromT ? TEAM_T : TEAM_CT));
                        } else {
                            candidateOver.SwitchTeam((CsTeam)targetTeam);
                            candidateUnder.SwitchTeam((CsTeam)(moveFromT ? TEAM_T : TEAM_CT));
                        }
                        candidateOver.PrintToCenterAlert($"!! YOU HAVE BEEN MOVED TO {(candidateOver.TeamNum == TEAM_T ? "T" : "CT")}!!");
                        candidateUnder.PrintToCenterAlert($"!! YOU HAVE BEEN MOVED TO {(candidateUnder.TeamNum == TEAM_T ? "T" : "CT")}!!");
                        if (candidateOver.UserId.HasValue) {
                            immunePlayers.Add(candidateOver.UserId.Value);
                        }
                        if (candidateUnder.UserId.HasValue) {
                            immunePlayers.Add(candidateUnder.UserId.Value);
                        }
                    }
                }
                return;
            }
            // ---------------- SKILL BALANCING (if team sizes are equal) ----------------
            else {
                // Only perform skill balancing if win streak conditions are met.
                if (totalPlayers >= minPlayers && totalPlayers <= maxPlayers && (winStreakT >= 3 || winStreakCT >= 3)) {
                    int winningTeam = (winStreakT >= 3 ? TEAM_T : TEAM_CT);
                    int losingTeam = (winningTeam == TEAM_T ? TEAM_CT : TEAM_T);
                    var winCandidates = connectedPlayers
                        .Where(p => p.TeamNum == winningTeam && p.UserId.HasValue && !immunePlayers.Contains(p.UserId.Value))
                        .ToList();
                    var loseCandidates = connectedPlayers
                        .Where(p => p.TeamNum == losingTeam && p.UserId.HasValue && !immunePlayers.Contains(p.UserId.Value))
                        .ToList();
                    if (winCandidates.Count < 2 || loseCandidates.Count < 2) {
                        immunePlayers.Clear();
                        return;
                    }

                    float currentDiff = Math.Abs(avgSkillT - avgSkillCT);
                    float bestNewDiff = currentDiff;
                    CCSPlayerController? candidateWin = null;
                    CCSPlayerController? candidateLose = null;

                    // Test candidate swaps between winning and losing teams.
                    foreach (var pWin in winCandidates) {
                        foreach (var pLose in loseCandidates) {
                            if (!pWin.UserId.HasValue) continue;
                            float winSkill = gameStats.GetPlayerStats(pWin.UserId.Value).calcSkill();
                            if (!pLose.UserId.HasValue) continue;
                            float loseSkill = gameStats.GetPlayerStats(pLose.UserId.Value).calcSkill();
                            float newTotalWin = gameStats.GetTeamTotalSkill(winningTeam) - winSkill + loseSkill;
                            float newTotalLose = gameStats.GetTeamTotalSkill(losingTeam) - loseSkill + winSkill;
                            float newAvgWin = newTotalWin / winCandidates.Count;
                            float newAvgLose = newTotalLose / loseCandidates.Count;
                            float newDiff = Math.Abs(newAvgWin - newAvgLose);

                            if (newDiff < bestNewDiff) {
                                bestNewDiff = newDiff;
                                candidateWin = pWin;
                                candidateLose = pLose;
                            }
                        }
                    }
                    // Only swap if the gap is meaningfully reduced.
                    if (candidateWin != null && candidateLose != null && bestNewDiff < currentDiff && currentDiff > 1000f) {
                        if (warmup) {
                            candidateWin.ChangeTeam((CsTeam)losingTeam);
                            candidateLose.ChangeTeam((CsTeam)winningTeam);
                        } else {
                            candidateWin.SwitchTeam((CsTeam)losingTeam);
                            candidateLose.SwitchTeam((CsTeam)winningTeam);
                        }
                        candidateWin.PrintToCenterAlert($"!! YOU HAVE BEEN MOVED TO {(candidateWin.TeamNum == TEAM_T ? "T" : "CT")}!!");
                        candidateLose.PrintToCenterAlert($"!! YOU HAVE BEEN MOVED TO {(candidateLose.TeamNum == TEAM_T ? "T" : "CT")}!!");
                        immunePlayers.Clear();
                        if (!warmup) {
                            if (candidateWin.UserId.HasValue) {
                                immunePlayers.Add(candidateWin.UserId.Value);
                            }
                            if (candidateLose.UserId.HasValue) {
                                immunePlayers.Add(candidateLose.UserId.Value);
                            }
                        }
                        Server.PrintToChatAll($"{ChatColors.DarkRed}[{ModuleNameNice}]: Skill balanced swap: {candidateWin.PlayerName} <-> {candidateLose.PlayerName}");
                        winStreakT = 0;
                        winStreakCT = 0;
                    } else {
                        immunePlayers.Clear();
                    }
                } else {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Skill balancing conditions not met.");
                }
            }
        }
    }
}