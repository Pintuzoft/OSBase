using System;
using System.Linq;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Config;

using CounterStrikeSharp.API.Core.Attributes.Registration;

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
        private const int TEAM_T = (int)CsTeam.Terrorist;      // Expected value: 2
        private const int TEAM_CT = (int)CsTeam.CounterTerrorist; // Expected value: 3

        private Dictionary<string, int> mapBombsites = new Dictionary<string, int>();
        private string mapConfigFile = "teambalancer_mapinfo.cfg";
        private int bombsites = 2;

        private const float delay = 6.5f;
        private const float warmupDelay = 0.0f;
        private bool warmup = false;

//        private int minPlayers = 4;
//        private int maxPlayers = 16;

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
            warmup = true;
            if (mapBombsites.ContainsKey(mapName)) {
                bombsites = mapBombsites[mapName];
                Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Map {mapName} started. Bombsites: {bombsites}");

            } else {
                bombsites = mapName.Contains("cs_") ? 1 : 2;
                config?.AddCustomConfigLine($"{mapConfigFile}", $"{mapName} {bombsites}");
                Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Map {mapName} started. Default bombsites: {bombsites}");
            }
        }

        [GameEventHandler(HookMode.Post)]
        private HookResult OnRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - OnRoundEnd triggered.");
            osbase?.AddTimer(delay, () => {
                BalanceTeams();
            });
            return HookResult.Continue;
        }

        [GameEventHandler(HookMode.Pre)]
        private HookResult OnWarmupEnd(EventWarmupEnd eventInfo, GameEventInfo gameEventInfo) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - OnWarmupEnd triggered.");
            if ( gameStats == null ) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - OnWarmupEnd: Game stats is null.");
                return HookResult.Continue;
            }
            gameStats?.loadPlayerData(0);
            BalanceTeams();
            warmup = false;
            return HookResult.Continue;
        }

        float GetDynamicThreshold ( ) {
            int round = gameStats?.roundNumber ?? 0;
            switch(round) {
                case 1:  return 5000f;
                case 2:  return 3000f;
                case 3:  return 2000f;
                case 4:  return 1000f;
                case 5:  return 1500f;
                case 6:  return 2000f;
                default: 
                    // For rounds beyond 6, you can keep increasing slowly or cap the threshold.
                    return 2000f + (round - 6) * 200f;
            }
        }

        private void BalanceTeams() {
            // Retrieve game stats and connected players.
            var gameStats = osbase?.GetGameStats();
            if (gameStats == null) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - BalanceTeams: Game stats is null.");
                return;
            }

            TeamStats tStats = gameStats.getTeam(TEAM_T);   
            TeamStats ctStats = gameStats.getTeam(TEAM_CT);

            if (tStats == null || ctStats == null) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - BalanceTeams: Team stats is null.");
                return; 
            }

            int winner = tStats.streak > 0 ? TEAM_T : TEAM_CT;
            int loser = winner == TEAM_T ? TEAM_CT : TEAM_T;
            int tCount = tStats.numPlayers();
            int ctCount = ctStats.numPlayers();
            int totalCount = tCount + ctCount;

            int idealT;
            int idealCT;

            switch (bombsites) {
                case 2:
                    idealT = totalCount / 2;
                    idealCT = totalCount / 2 + totalCount % 2;
                    break;
                default:
                    idealT = totalCount / 2 + totalCount % 2;
                    idealCT = totalCount / 2;
                    break;
            }

            bool sizesBalanced = tCount == idealT && ctCount == idealCT;

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - BalanceTeams: T: {tCount} CT: {ctCount} Ideal T: {idealT} Ideal CT: {idealCT}");

            float tSkill = tStats.getAverageSkill();
            float ctSkill = ctStats.getAverageSkill();
            int playersToMove = 0;

            // team sizes are balanced.
            if ( ! sizesBalanced ) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - BalanceTeams: Team sizes are not balanced.");
                bool moveFromT = tCount > idealT;
                bool moveFromCT = ctCount > idealCT;
                playersToMove = Math.Abs(moveFromT ? tCount - idealT : ctCount - idealCT);

                // T has more players than ideal
                evenTeamSizes(moveFromT, tSkill, ctSkill, playersToMove, tStats, ctStats);
            }

            if ( ! warmup ) {
                float skillDiff = Math.Abs(tSkill - ctSkill);
                if ( skillDiff > this.GetDynamicThreshold() ) {
                    doSkillBalance(tStats, ctStats);
                }
            }
        }

        // Skill balance algorithm. Swap 2 players based on skill difference.
        private void doSkillBalance(TeamStats tStats, TeamStats ctStats) {
            float tSkill = tStats.getAverageSkill();
            float ctSkill = ctStats.getAverageSkill();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - doSkillBalance: Skill difference: {Math.Abs(tSkill - ctSkill)}");
            float diff = Math.Abs(tSkill - ctSkill);
            if(diff < this.GetDynamicThreshold()) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - doSkillBalance: Skill difference is below threshold.");
                return;
            }

            // Determine which team is stronger.
            bool tIsStrong = tSkill > ctSkill;
            // For the strong team, target deviation = full difference; for the weak team, half that.
            float strongTarget = diff;
            float weakTarget = diff / 2;

            // Select candidate from the strong team (to remove) and from the weak team (to add)
            CCSPlayerController? candidateStrong = tIsStrong ? tStats.GetPlayerByDeviation(strongTarget, true) : ctStats.GetPlayerByDeviation(strongTarget, true);
            CCSPlayerController? candidateWeak   = tIsStrong ? ctStats.GetPlayerByDeviation(weakTarget, false) : tStats.GetPlayerByDeviation(weakTarget, false);

            if(candidateStrong == null || candidateWeak == null) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - doSkillBalance: Candidate swap not found.");
                return;
            }
            if(gameStats == null || !candidateStrong.UserId.HasValue || !candidateWeak.UserId.HasValue) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - doSkillBalance: Invalid candidate(s).");
                return;
            }

            // Now, candidateStrong should be from the strong team and candidateWeak from the weak team.
            float strongCandidateSkill = gameStats.GetPlayerStats(candidateStrong.UserId.Value).calcSkill();
            float weakCandidateSkill   = gameStats.GetPlayerStats(candidateWeak.UserId.Value).calcSkill();
            if (strongCandidateSkill < weakCandidateSkill) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - doSkillBalance: Candidate from strong team ({strongCandidateSkill}) is not stronger than candidate from weak team ({weakCandidateSkill}). Swap skipped.");
                return;
            }
            // Execute the swap
            movePlayer(candidateStrong, tIsStrong ? TEAM_CT : TEAM_T, tStats, ctStats);
            movePlayer(candidateWeak, tIsStrong ? TEAM_T : TEAM_CT, tStats, ctStats);
            Server.PrintToChatAll($"[TeamBalancer] Swapped: {candidateStrong.PlayerName}[{(candidateStrong.TeamNum == TEAM_T ? "CT" : "T")}] <-> [{(candidateWeak.TeamNum == TEAM_T ? "CT" : "T")}]{candidateWeak.PlayerName}");
        }

        private void evenTeamSizes ( bool moveFromT, float tSkill, float ctSkill, int playersToMove, TeamStats tStats, TeamStats ctStats ) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - evenTeamSizes: Try Moving {playersToMove} players to {(moveFromT ? "CT" : "T")}.");
            float targetSkill = (moveFromT ? tSkill - ctSkill : ctSkill - tSkill) / 2;
            float targetSkillPerPlayer = targetSkill / playersToMove;
            for (int i = 0; i < playersToMove; i++) {
                // Find the player nearest target skill
                TeamStats sourceTeamStats = moveFromT ? tStats : ctStats;
                CCSPlayerController? player = sourceTeamStats.getPlayerBySkillNonImmune(targetSkillPerPlayer);
                if (player == null) {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - evenTeamSizes: Failed to find player to move.");
                    break;
                }

                // Move the player to CT
                if (player.UserId.HasValue) {
                    movePlayer(player, moveFromT ? TEAM_CT : TEAM_T, tStats, ctStats);
                    Server.PrintToChatAll($"[TeamBalancer] Moved: {player.PlayerName} {(moveFromT ? "T -> CT" : "CT -> T")}.");
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - evenTeamSizes: Moved player {player.PlayerName} to {(moveFromT ? "CT" : "T")}.");
                } else {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - evenTeamSizes: Player {player.PlayerName} has null UserId.");
                }
            }
        }

        private void movePlayer (CCSPlayerController player, int targetTeam, TeamStats tStats, TeamStats ctStats) {
            bool isTargetT = targetTeam == TEAM_T;
            if (player == null || !player.UserId.HasValue) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - movePlayer: Failed to find player to move.");
                return;
            }

            gameStats?.clearDisconnected();

            // print teams
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - T:");
            tStats.printPlayers();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - CT:");
            ctStats.printPlayers();

            // Move the player
//            if ( warmup ) {
//                player.ChangeTeam((CsTeam)targetTeam);
//            } else {
                player.SwitchTeam((CsTeam)targetTeam);
//            }
            if (player.UserId.HasValue) {
                if (gameStats != null) {
                    gameStats.GetPlayerStats(player.UserId.Value).immune += 2;
                } else {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - movePlayer: gameStats is null.");
                }
                player.PrintToCenterAlert($"!! YOU HAVE BEEN MOVED TO {(isTargetT ? "T" : "CT")}!!");
                //Server.PrintToChatAll($"[TeamBalancer] {player.PlayerName} moved: {(isTargetT ? "CT -> T" : "T -> CT")}.");

            } else {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - movePlayer: Player {player.PlayerName} has null UserId.");
            }
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - movePlayer: Moved player {player.PlayerName} to {(isTargetT ? "T" : "CT")}.");
            
            // print teams
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - T:");
            tStats.printPlayers();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - CT:");
            ctStats.printPlayers();
        }

    }
}