using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Core;

namespace OSBase.Modules {

    public class GameStats {
        public string ModuleName => "gamestats";
        private OSBase? osbase;
        private Config? config;
        private bool isWarmup = false;

        private const int TEAM_T = (int)CsTeam.Terrorist;
        private const int TEAM_CT = (int)CsTeam.CounterTerrorist;

        // Store player stats keyed by user id.
        private Dictionary<int, PlayerStats> playerStats = new Dictionary<int, PlayerStats>();
        private Dictionary<int, TeamStats> teamStats = new Dictionary<int, TeamStats>();

        public GameStats(OSBase inOsbase, Config inConfig) {
            this.osbase = inOsbase;
            this.config = inConfig;
            loadEventHandlers();
        }

        public void loadEventHandlers() {
            // Register relevant events.
            osbase?.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
            osbase?.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            osbase?.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
            osbase?.RegisterListener<Listeners.OnMapStart>(OnMapStart);
            osbase?.RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
            osbase?.RegisterEventHandler<EventStartHalftime>(OnStartHalftime);
            osbase?.RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
            osbase?.RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
            osbase?.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
            osbase?.RegisterEventHandler<EventPlayerConnect>(OnPlayerConnect);
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
        }

        private HookResult OnPlayerHurt(EventPlayerHurt eventInfo, GameEventInfo gameEventInfo) {
            if(isWarmup) return HookResult.Continue;
            if (eventInfo.Attacker != null && eventInfo.Attacker.UserId.HasValue) {
                int attackerId = eventInfo.Attacker.UserId.Value;
                if (!playerStats.ContainsKey(attackerId)) {
                    playerStats[attackerId] = new PlayerStats();
                }
                playerStats[attackerId].damage += eventInfo.DmgHealth;
                playerStats[attackerId].shotsHit++;
            }            
            return HookResult.Continue;
        }

        private HookResult OnPlayerTeam(EventPlayerTeam eventInfo, GameEventInfo gameEventInfo) {
            if ( eventInfo.Userid != null && eventInfo.Userid.UserId.HasValue ) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Player {eventInfo.Userid}:{eventInfo.Userid.PlayerName} switched to team {eventInfo.Userid.TeamNum}:{eventInfo.Team}");                        
            }
            return HookResult.Continue;
        }

        private HookResult OnPlayerConnect(EventPlayerConnect eventInfo, GameEventInfo gameEventInfo) {
            if ( eventInfo.Userid != null && eventInfo.Userid.UserId.HasValue ) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Player {eventInfo.Userid}:{eventInfo.Userid.PlayerName} connected.");                        
            }
            return HookResult.Continue;
        }

        private HookResult OnPlayerDisconnect(EventPlayerDisconnect eventInfo, GameEventInfo gameEventInfo) {
            if ( eventInfo.Userid != null && eventInfo.Userid.UserId.HasValue ) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Player {eventInfo.Userid}:{eventInfo.Userid.PlayerName} disconnected.");                        
            }
            return HookResult.Continue;
        }

        private HookResult OnWeaponFire(EventWeaponFire eventInfo, GameEventInfo gameEventInfo) {
            if(isWarmup) return HookResult.Continue;
            if (eventInfo.Userid != null && eventInfo.Userid.UserId.HasValue) {
                int shooterId = eventInfo.Userid.UserId.Value;
                if (!playerStats.ContainsKey(shooterId)) {
                    playerStats[shooterId] = new PlayerStats();
                }
                playerStats[shooterId].shotsFired++;
            }
            return HookResult.Continue;
        }

        // Update stats when a player dies.
        private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
            if(isWarmup) return HookResult.Continue;
            if (eventInfo.Attacker != null && eventInfo.Attacker.UserId.HasValue) {
                int attackerId = eventInfo.Attacker.UserId.Value;
                if (!playerStats.ContainsKey(attackerId)) {
                    playerStats[attackerId] = new PlayerStats();
                }
                playerStats[attackerId].kills++;
                if ( eventInfo.Hitgroup == 1 ) {
                    playerStats[attackerId].headshotKills++;
                }
            }
            if (eventInfo.Userid != null && eventInfo.Userid.UserId.HasValue) {
                int victimId = eventInfo.Userid.UserId.Value;
                if (!playerStats.ContainsKey(victimId)) {
                    playerStats[victimId] = new PlayerStats();
                }
                playerStats[victimId].deaths++;
            }
            // Optionally update assists if available.
            if (eventInfo.Assister != null && eventInfo.Assister.UserId.HasValue) {
                int assistId = eventInfo.Assister.UserId.Value;
                if (!playerStats.ContainsKey(assistId)) {
                    playerStats[assistId] = new PlayerStats();
                }
                playerStats[assistId].assists++;
            }
            return HookResult.Continue;
        }

        // Print current stats at the end of a round.
        private HookResult OnRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
            if(isWarmup) 
                return HookResult.Continue;

            // Update team stats.
            if (eventInfo.Winner == TEAM_T) {
                teamStats[TEAM_T].wins++;
                teamStats[TEAM_CT].losses++;
                teamStats[TEAM_T].streak++;
                teamStats[TEAM_CT].streak = 0;
            } else if (eventInfo.Winner == TEAM_CT) {
                teamStats[TEAM_CT].wins++;
                teamStats[TEAM_T].losses++;
                teamStats[TEAM_CT].streak++;
                teamStats[TEAM_T].streak = 0;
            }

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Round ended. Current player stats:");
            foreach (var entry in playerStats) {
                entry.Value.rounds++;
            }

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Current team stats:");
            foreach (var entry in teamStats) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Team [{(entry.Key == TEAM_T ? "T" : "CT")}]: {entry.ToString()}");
            } 

            var playerList = Utilities.GetPlayers();
            PlayerStats pstats;
            teamStats[TEAM_T].skill = 0;
            teamStats[TEAM_CT].skill = 0;
            teamStats[TEAM_T].resetPlayers();
            foreach (var player in playerList) {
                if (player != null && player.UserId.HasValue) {
                    pstats = playerStats[player.UserId.Value];
                    pstats.team = (int)player.TeamNum;
                    if ( player.TeamNum == eventInfo.Winner ) {
                        pstats.roundWins++;
                    } else {
                        pstats.roundLosses++;
                    }
                    teamStats[pstats.team].skill += pstats.calcSkill();
                    teamStats[pstats.team].addPlayer(player.UserId.Value, pstats);
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Skillrating: {player.PlayerName}: {pstats.kills}k, {pstats.assists}a, {pstats.deaths} [{pstats.damage}] -> {pstats.calcSkill()}");
                }
            }
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - [T]: {teamStats[TEAM_T].skill}, [CT]: {teamStats[TEAM_CT].skill}");
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - [T]: {teamStats[TEAM_T].numPlayers()}, [CT]: {teamStats[TEAM_CT].numPlayers()}");
            return HookResult.Continue;
        }


        private HookResult OnStartHalftime(EventStartHalftime eventInfo, GameEventInfo gameEventInfo) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - OnStartHalftime triggered.");
            TeamStats buf = teamStats[TEAM_T];
            teamStats[TEAM_T] = teamStats[TEAM_CT];
            teamStats[TEAM_CT] = buf;
            return HookResult.Continue;
        }

        // Reset stats at the start of a new map.
         private void OnMapStart(string mapName) {
            this.isWarmup = true;
            clearStats();
        }

        private HookResult OnWarmupEnd(EventWarmupEnd eventInfo, GameEventInfo gameEventInfo) {
            this.isWarmup = false;
            clearStats();
            return HookResult.Continue;
        }

        // Public method to access a player's stats.
        public PlayerStats GetPlayerStats(int userId) {
            if (playerStats.ContainsKey(userId)) {
                return playerStats[userId];
            }
            return new PlayerStats(); // Return an empty stats object if none exists.
        }

        private void clearStats() {
            // Reset stats for all players.
            playerStats.Clear();
            foreach (var player in Utilities.GetPlayers()) {
                if (player != null && player.UserId.HasValue) {
                    playerStats[player.UserId.Value] = new PlayerStats();
                }
            }
            // Reset team stats.
            teamStats.Clear();
            teamStats[TEAM_T] = new TeamStats();
            teamStats[TEAM_CT] = new TeamStats();
        }

        public TeamStats getTeam (int team) {
            if ( team == TEAM_T || team == TEAM_CT) {
                return teamStats[team];
            }
            return new TeamStats();     
        }
        public float GetTeamTotalSkill(int team) {
            return teamStats[team].skill;
        }

        public float GetTeamAverageSkill(int team) {
            return teamStats[team].skill / teamStats[team].numPlayers();
        }
    }


    // Data container for game statistics.
    public class PlayerStats {
        public int team { get; set; }
        public int rounds { get; set; }
        public int roundWins { get; set; }
        public int roundLosses { get; set; }
        public int kills { get; set; }
        public int deaths { get; set; }
        public int assists { get; set; }
        public int damage { get; set; }
        public int shotsFired { get; set; }
        public int shotsHit { get; set; }
        public int headshotKills { get; set; }
        public int immune { get; set; }

        // Calculates a skill rating that uses:
        // - Average damage per round for a base score:
        //     baseDamageScore = 4000 + (avgDamage * 60)
        //   (So 0 dmg/round → 4000p, 100 dmg/round → 10000p, etc.)
        // - Bonus points: 250 per kill, 100 per assist.
        // - Penalty: 150 per death.
        // - Extra bonus: 100 per headshot kill.
        // - Accuracy bonus: For every point above 30% accuracy, add (accuracyDifference * 2000).
        // If no rounds are played, default to 10000.
        public float calcSkill() {
            if (rounds == 0)
                return 10000f;

            // Base damage score (average damage per round)
            float avgDamage = (float)damage / rounds;
            float baseDamageScore = 4000f + (avgDamage * 60f);

            // Standard bonus/penalties.
            float killBonus = kills * 150f;
            float assistBonus = assists * 50f;
            float deathPenalty = deaths * 100f;
            float headshotBonus = headshotKills * 50f;

            // Accuracy bonus: use a baseline of 30% accuracy.
            float accuracy = shotsFired > 0 ? (float)shotsHit / shotsFired : 0;
            float baselineAccuracy = 0.3f;
            float accuracyBonus = accuracy > baselineAccuracy ? (accuracy - baselineAccuracy) * 2000f : 0f;

            // Total rating is the sum of the above.
            float totalRating = baseDamageScore + killBonus + assistBonus - deathPenalty + headshotBonus + accuracyBonus;
            return totalRating;
        }
    }

    public class TeamStats {
        public int wins { get; set; }
        public int losses { get; set; }
        public int streak { get; set; }
        public float skill { get; set; }
        private Dictionary<int, PlayerStats> playerList = new Dictionary<int, PlayerStats>();
        public int numPlayers() {
            return playerList.Count;
        }
        public void resetPlayers () {
            playerList.Clear();
        }
        public void addPlayer (int userId, PlayerStats stats) {
            playerList[userId] = stats;
        }
        public void removePlayer (int userId) {
            playerList.Remove(userId);
        }
        public float getAverageSkill() {
            return skill / numPlayers();
        }
        public CCSPlayerController? getPlayerBySkill(float targetSkill) {
            int bestPlayerId = -1;
            float bestDiff = float.MaxValue;
            foreach (var kvp in playerList) {
                var stats = kvp.Value;
                if (stats.immune > 0)
                    continue;
                float diff = Math.Abs(stats.calcSkill() - targetSkill);
                if (diff < bestDiff) {
                    bestDiff = diff;
                    bestPlayerId = kvp.Key;
                }
            }
            if (bestPlayerId == -1) {
                Console.WriteLine($"[DEBUG] OSBase[gamestats] - getPlayerBySkill: No player found for skill {targetSkill}");
                return null;
            }
            return Utilities.GetPlayerFromUserid(bestPlayerId);
        }
        public CCSPlayerController? GetPlayerByDeviation(float targetDeviation, bool forStrongTeam) {
            int bestPlayerId = -1;
            float bestDiff = float.MaxValue;
            float teamAvg = getAverageSkill();

            foreach (var kvp in playerList) {
                // Skip immune players.
                if (kvp.Value.immune > 0)
                    continue;
                float playerSkill = kvp.Value.calcSkill();
                // Compute deviation relative to team average.
                float deviation = forStrongTeam ? (playerSkill - teamAvg) : (teamAvg - playerSkill);
                float diff = Math.Abs(deviation - targetDeviation);
                if (diff < bestDiff) {
                    bestDiff = diff;
                    bestPlayerId = kvp.Key;
                }
            }
            if (bestPlayerId == -1) {
                Console.WriteLine($"[DEBUG] OSBase[gamestats] - GetPlayerByDeviation: No player found for target deviation {targetDeviation}");
                return null;
            }
            return Utilities.GetPlayerFromUserid(bestPlayerId);
        }
        public override string ToString() {
            return $"Wins: {wins}, Losses: {losses}, Streak: {streak}";
        }
    }
    
}