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
        private const int TEAM_T = (int)CsTeam.Terrorist;      // Expected value: 2
        private const int TEAM_CT = (int)CsTeam.CounterTerrorist; // Expected value: 3

        private Dictionary<string, int> mapBombsites = new Dictionary<string, int>();
        private string mapConfigFile = "teambalancer_mapinfo.cfg";
        private int bombsites = 2;

        private const float delay = 6.5f;
        private const float warmupDelay = 5.0f;

        private bool warmup = false;

        private float swapThreshold = 5000f;

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
            var gameStats = osbase?.GetGameStats();
            if (gameStats == null)
                return;

            TeamStats tStats = gameStats.getTeam(TEAM_T);   
            TeamStats ctStats = gameStats.getTeam(TEAM_CT);

            if (tStats == null || ctStats == null)
                return; 

            // Check if the round is in warmup.
            if (warmup) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - BalanceTeams: Skipping balance during warmup.");
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

            bool sizesBalanced = (tCount == idealT && ctCount == idealCT);

            float tSkill = tStats.getAverageSkill();
            float ctSkill = ctStats.getAverageSkill();
            int playersToMove = 0;

            // team sizes are balanced.
            if ( ! sizesBalanced ) {
                bool moveFromT = tCount > idealT;
                bool moveFromCT = ctCount > idealCT;
                playersToMove = Math.Abs(moveFromT ? tCount - idealT : ctCount - idealCT);

                // T has more players than ideal
                evenTeamSizes(moveFromT, tSkill, ctSkill, playersToMove, tStats, ctStats);

                // Do a skillbalance if the teams are still unbalanced.
                if ( ! warmup ) {
                    doSkillBalance(tStats, ctStats);
                }
            }

            if ( tStats.streak > 2 || ctStats.streak > 2 ) {
                if ( Math.Abs(tStats.streak - ctStats.streak) > 1 ) {
                    float skillDiff = Math.Abs(tSkill - ctSkill);
                    if ( skillDiff > swapThreshold ) {
                        doSkillBalance(tStats, ctStats);
                    }
                }
            }


        }

        // Skill balance algorithm. Swap 2 players based on skill difference.
        private void doSkillBalance(TeamStats tStats, TeamStats ctStats) {
            float tSkill = tStats.getAverageSkill();
            float ctSkill = ctStats.getAverageSkill();
            float diff = Math.Abs(tSkill - ctSkill);
            if(diff < swapThreshold) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - BalanceTeams: Skill difference is below threshold.");
                return;
            }

            // Determine which team is stronger.
            bool tIsStrong = tSkill > ctSkill;
            // For the strong team, target deviation = full difference; for the weak team, half that.
            float strongTarget = diff;
            float weakTarget = diff / 2;

            // Select candidate from the strong team (to remove) and from the weak team (to add)
            CCSPlayerController? candidateStrong = tIsStrong ? tStats.GetPlayerByDeviation(strongTarget, true) : ctStats.GetPlayerByDeviation(strongTarget, true); 
            CCSPlayerController? candidateWeak = tIsStrong ? ctStats.GetPlayerByDeviation(weakTarget, false) : tStats.GetPlayerByDeviation(weakTarget, false);

            if(candidateStrong == null || candidateWeak == null) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - BalanceTeams: Candidate swap not found.");
                return;
            }

            // Execute the swap
            movePlayer(candidateStrong, tIsStrong ? TEAM_CT : TEAM_T, tStats, ctStats);
            movePlayer(candidateWeak, tIsStrong ? TEAM_T : TEAM_CT, tStats, ctStats);
        }

        private void evenTeamSizes ( bool moveFromT, float tSkill, float ctSkill, int playersToMove, TeamStats tStats, TeamStats ctStats ) {
            float targetSkill = (moveFromT ? tSkill - ctSkill : ctSkill - tSkill) / 2;
            float targetSkillPerPlayer = targetSkill / playersToMove;
            for (int i = 0; i < playersToMove; i++) {
                // Find the player nearest target skill
                CCSPlayerController? player = tStats.getPlayerBySkill(targetSkillPerPlayer);
                if (player == null) {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - BalanceTeams: Failed to find player to move.");
                    break;
                }

                // Move the player to CT
                if (player.UserId.HasValue) {
                    movePlayer(player, moveFromT ? TEAM_CT : TEAM_T, tStats, ctStats);
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - BalanceTeams: Moved player {player.PlayerName} to {(moveFromT ? "CT" : "T")}.");
                } else {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - BalanceTeams: Player {player.PlayerName} has null UserId.");
                }
            }
        }


        private void movePlayer (CCSPlayerController player, int targetTeam, TeamStats tStats, TeamStats ctStats) {
            bool isTargetT = targetTeam == TEAM_T;
            if (player == null || !player.UserId.HasValue) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - BalanceTeams: Failed to find player to move.");
                return;
            }
            // Move the player
            if ( warmup ) {
                player.ChangeTeam((CsTeam)targetTeam);
            } else {
                player.SwitchTeam((CsTeam)targetTeam);
            }
            if (player.UserId.HasValue) {
                if (gameStats != null) {
                    ctStats.addPlayer(player.UserId.Value, gameStats.GetPlayerStats(player.UserId.Value));
                    gameStats.GetPlayerStats(player.UserId.Value).immune += 2;
                } else {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - movePlayer: gameStats is null.");
                }
                tStats.removePlayer(player.UserId.Value);
                player.PrintToCenterAlert($"!! YOU HAVE BEEN MOVED TO {(isTargetT ? "T" : "CT")}!!");
                Server.PrintToChatAll($"[TeamBalancer] {player.PlayerName} moved: {(isTargetT ? "CT -> T" : "T -> CT")}.");

            } else {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - movePlayer: Player {player.PlayerName} has null UserId.");
            }
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - BalanceTeams: Moved player {player.PlayerName} to {(isTargetT ? "T" : "CT")}.");
        }
    }
}