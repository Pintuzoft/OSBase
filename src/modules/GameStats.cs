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

            Console.WriteLine("[DEBUG] OSBase[gamedata] - Round ended. Current player stats:");
            foreach (var entry in playerStats) {
                entry.Value.rounds++;
                Console.WriteLine($"[DEBUG] OSBase[gamedata] - Player ID {entry.Key}: {entry.Value}");
            }

            Console.WriteLine("[DEBUG] OSBase[gamedata] - Current team stats:");
            foreach (var entry in teamStats) {
                Console.WriteLine($"[DEBUG] OSBase[gamedata] - Team [{(entry.Key == TEAM_T ? "T" : "CT")}]: {entry.ToString()}");
            } 

            var playerList = Utilities.GetPlayers();
            foreach (var player in playerList) {
                if (player != null && player.UserId.HasValue) {
                    if ( player.TeamNum == eventInfo.Winner ) {
                        playerStats[player.UserId.Value].roundWins++;
                    } else {
                        playerStats[player.UserId.Value].roundLosses++;
                    }
                    Console.WriteLine($"[DEBUG] OSBase[gamedata] - Skillrating: {player.PlayerName}: {playerStats[player.UserId.Value].calcSkill()}");
                }
            }

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
    }

    // Data container for game statistics.
    public class PlayerStats {
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
        public float calcSkill() {                        
            // Final rating = baseline + (defaultPerformance + adjustments).
            // With baseline = 1000 and defaultPerformance = 10000, a neutral player gets 11000.
            const float baseline = 1000f;
            const float defaultPerformance = 10000f;
            
            // Neutral per-round defaults.
            const float defaultKPR = 1f;    // 1 kill per round
            const float defaultDPR = 1f;    // 1 death per round
            const float defaultADR = 100f;  // 100 damage per round
            const float defaultAPR = 0.5f;  // 0.5 assists per round
            
            // Basic metric multipliers.
            const float killFactor = 1500f;
            const float deathFactor = 1000f;
            const float damageFactor = 1f;     // 1 point per extra damage per round above default
            const float assistFactor = 1000f;
            
            // --- KD Adjustment ---
            // Neutral KD is 1.0.
            // For KD below 1.0, we use a steeper multiplier.
            // For KD ≥ 1.0, we use a milder multiplier.
            float kd = deaths > 0 ? (float)kills / deaths : kills;
            float kdAdjustment = 0f;
            if (kd < 1f)
            {
                kdAdjustment = (kd - 1f) * 10000f;  // e.g. kd = 0.4 -> (0.4-1.0)*10000 = -6000.
            }
            else
            {
                kdAdjustment = (kd - 1f) * 5000f;    // e.g. kd = 1.6 -> (1.6-1.0)*5000 = +3000.
            }
            
            // --- Win Ratio Adjustment ---
            float winRatio = rounds > 0 ? (float)roundWins / rounds : 0.5f;
            float winAdjustment = (winRatio - 0.5f) * 5000f;
            
            // --- Headshot Bonus ---
            float headshotRatio = kills > 0 ? (float)headshotKills / kills : 0;
            float headshotBonus = Math.Max(headshotRatio - 0.2f, 0) * 1500f;
            
            // --- Accuracy Bonus ---
            float accuracy = shotsFired > 0 ? (float)shotsHit / shotsFired : 0;
            float accuracyBonus = Math.Max(accuracy - 0.3f, 0) * 1000f;
            
            // --- Per-Round Averages for Basic Metrics ---
            float kprAvg = rounds > 0 ? (float)kills / rounds : 0;
            float dprAvg = rounds > 0 ? (float)deaths / rounds : 0;
            float adrAvg = rounds > 0 ? (float)damage / rounds : 0;
            float aprAvg = rounds > 0 ? (float)assists / rounds : 0;
            
            float basicAdjustment =
                (kprAvg - defaultKPR) * killFactor
                + (defaultDPR - dprAvg) * deathFactor
                + (adrAvg - defaultADR) * damageFactor
                + (aprAvg - defaultAPR) * assistFactor;
            
            // --- Combine All Adjustments ---
            float totalAdjustment = basicAdjustment + kdAdjustment + winAdjustment + headshotBonus + accuracyBonus;
            float calculatedValue = defaultPerformance + totalAdjustment;
            if (calculatedValue < 0)
                calculatedValue = 0;
            
            float finalSkill = baseline + calculatedValue;
            return Math.Clamp(finalSkill, 1000f, 30000f);
        }
    }

    public class TeamStats {
        public int wins { get; set; }
        public int losses { get; set; }
        public int streak { get; set; }   
        public override string ToString() {
            return $"Wins: {wins}, Losses: {losses}, Streak: {streak}";
        }
    }
    
}