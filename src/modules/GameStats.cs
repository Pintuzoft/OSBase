using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Core;
using System.Diagnostics.Contracts;

namespace OSBase.Modules {

    public class GameStats {
        public string ModuleName => "gamestats";
        private OSBase? osbase;
        private Config? config;
        private bool isWarmup = false;

        public int roundNumber = 0;

        private const int TEAM_SPEC = (int)CsTeam.Spectator;
        private const int TEAM_T = (int)CsTeam.Terrorist;
        private const int TEAM_CT = (int)CsTeam.CounterTerrorist;

        // Store player stats keyed by user id.
        public Dictionary<int, PlayerStats> playerStats = new Dictionary<int, PlayerStats>();
        private Dictionary<int, TeamStats> teamStats = new Dictionary<int, TeamStats>();

        public GameStats(OSBase inOsbase, Config inConfig) {
            this.osbase = inOsbase;
            this.config = inConfig;
            loadEventHandlers();
            teamStats[TEAM_T] = new TeamStats();
            teamStats[TEAM_CT] = new TeamStats();            
        }

        public void loadEventHandlers() {
            // Register relevant events.
            osbase?.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
            osbase?.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            osbase?.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
            osbase?.RegisterEventHandler<EventRoundStart>(OnRoundStart);
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
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - OnPlayerTeam: Player {eventInfo.Userid.UserId.Value}:{eventInfo.Userid.PlayerName} switched to team {eventInfo.Userid.TeamNum}:{eventInfo.Team}");
                if ( teamStats[TEAM_T] == null ) {
                    teamStats[TEAM_T] = new TeamStats();
                }
                if ( teamStats[TEAM_CT] == null ) {
                    teamStats[TEAM_CT] = new TeamStats();
                }
                if ( eventInfo.Team == ( TEAM_T | TEAM_CT ) ) {
                    if ( playerStats.ContainsKey(eventInfo.Userid.UserId.Value) ) {
                        bool isTeamT = eventInfo.Team == TEAM_T;
                        teamStats[TEAM_T].removePlayer(eventInfo.Userid.UserId.Value);
                        teamStats[TEAM_CT].removePlayer(eventInfo.Userid.UserId.Value);
                        teamStats[isTeamT ? TEAM_T : TEAM_CT].addPlayer(eventInfo.Userid.UserId.Value, playerStats[eventInfo.Userid.UserId.Value]);
                    }
                } else {
                    teamStats[TEAM_T].removePlayer(eventInfo.Userid.UserId.Value);
                    teamStats[TEAM_CT].removePlayer(eventInfo.Userid.UserId.Value);
                }
            }
            return HookResult.Continue;
        }

        private HookResult OnPlayerConnect(EventPlayerConnect eventInfo, GameEventInfo gameEventInfo) {
            if ( eventInfo.Userid != null && eventInfo.Userid.UserId.HasValue ) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Player {eventInfo.Userid.UserId.Value}:{eventInfo.Userid.PlayerName} connected.");                        
            }
            return HookResult.Continue;
        }

        private HookResult OnPlayerDisconnect(EventPlayerDisconnect eventInfo, GameEventInfo gameEventInfo) {
            if ( eventInfo.Userid != null && eventInfo.Userid.UserId.HasValue ) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Player {eventInfo.Userid}:{eventInfo.Userid.PlayerName} disconnected.");
                if ( playerStats.ContainsKey(eventInfo.Userid.UserId.Value) ) {
                    playerStats[eventInfo.Userid.UserId.Value].disconnected = true;
                }
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

        private void printTeams() {
            TeamStats teamt = teamStats[TEAM_T];
            TeamStats teamct = teamStats[TEAM_CT];
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Team T: {teamt.wins}w, {teamt.losses}l, {teamt.streak}s, {teamt.getAverageSkill()}p");
            teamt.printPlayers();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Team CT: {teamct.wins}w, {teamct.losses}l, {teamct.streak}s, {teamct.getAverageSkill()}p");
            teamct.printPlayers();
        }


        // Update stats at the start of a new round.
        private HookResult OnRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {
            if(isWarmup) 
                return HookResult.Continue;

            roundNumber++;
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Round {roundNumber} started.");
            printTeams();
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

            loadPlayerData ( eventInfo.Winner );

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - [T]: {teamStats[TEAM_T].getAverageSkill()}, [CT]: {teamStats[TEAM_CT].getAverageSkill()}");
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - [T]: {teamStats[TEAM_T].numPlayers()}, [CT]: {teamStats[TEAM_CT].numPlayers()}");
            return HookResult.Continue;
        }

        public void loadPlayerData ( int winner ) {
            var playerList = Utilities.GetPlayers();
            PlayerStats pstats;
            teamStats[TEAM_T].resetPlayers();
            teamStats[TEAM_CT].resetPlayers();

            foreach (var player in playerList) {
                if (player != null && ! player.IsHLTV && player.UserId.HasValue) {
                    if (!playerStats.ContainsKey(player.UserId.Value)) {
                        playerStats[player.UserId.Value] = new PlayerStats();
                    }
                    pstats = playerStats[player.UserId.Value];
                    bool isTeamTWinner = winner == TEAM_T;

                    if ( winner == (TEAM_T|TEAM_CT) ) {
                        // Update round wins for the winning team.
                        foreach (var p in teamStats[winner].playerList) {
                            if ( p.Key == player.UserId.Value ) {
                                pstats.roundWins++;
                            }
                        }

                        // Update round losses for the losing team.
                        foreach (var p in teamStats[isTeamTWinner ? TEAM_CT : TEAM_T].playerList) {
                            if ( p.Key == player.UserId.Value ) {
                                pstats.roundLosses++;
                            }
                        }
                    }
                    // Add player to team stats.
                    teamStats[player.TeamNum].addPlayer(player.UserId.Value, pstats);
                    if ( playerStats[player.UserId.Value].immune > 0 ) {
                        playerStats[player.UserId.Value].immune--;
                    }
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Skillrating: {player.PlayerName}{(playerStats[player.UserId.Value].immune > 0 ? "(immune)" : "")}: {pstats.kills}k, {pstats.assists}a, {pstats.deaths} [{pstats.damage}] -> {pstats.calcSkill()}");
                }
            }

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
            this.roundNumber = 0;
            clearStats();
        }

        private HookResult OnWarmupEnd(EventWarmupEnd eventInfo, GameEventInfo gameEventInfo) {
            this.isWarmup = false;
            this.roundNumber = 0;
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
            // Reset team stats.
            teamStats.Clear();
            teamStats[TEAM_T] = new TeamStats();
            teamStats[TEAM_CT] = new TeamStats();
             
            // Reset stats for all players.
            playerStats.Clear();
            foreach (var player in Utilities.GetPlayers()) {
                if (player != null && player.UserId.HasValue && ! player.IsHLTV ) {
                    playerStats[player.UserId.Value] = new PlayerStats();
                    switch (player.TeamNum) {
                        case TEAM_T:
                            teamStats[TEAM_T].addPlayer(player.UserId.Value, playerStats[player.UserId.Value]);
                            break;
                        case TEAM_CT:
                            teamStats[TEAM_CT].addPlayer(player.UserId.Value, playerStats[player.UserId.Value]);
                            break;
                        default:
                            break;
                    }
                }
            }
        }
        public void clearDisconnected ( ) {
            foreach (var player in playerStats) {
                if ( player.Value.disconnected ) {
                    playerStats.Remove((int)player.Key);
                    teamStats[TEAM_T].removePlayer((int)player.Key);
                    teamStats[TEAM_CT].removePlayer((int)player.Key);
                }
            }
        }
        public TeamStats getTeam (int team) {
            if ( team == TEAM_T || team == TEAM_CT) {
                return teamStats[team];
            }
            return new TeamStats();     
        }

        public void movePlayer ( int userId, int team ) {
            if ( team != (TEAM_T | TEAM_CT) ) {
                return;
            }
            if ( playerStats.ContainsKey(userId) ) {
                PlayerStats pstats = playerStats[userId];
                if ( teamStats[TEAM_T] == null ) {
                    teamStats[TEAM_T] = new TeamStats();
                }
                if ( teamStats[TEAM_CT] == null ) {
                    teamStats[TEAM_CT] = new TeamStats();
                }

                teamStats[TEAM_T].removePlayer(userId);
                teamStats[TEAM_CT].removePlayer(userId);
                teamStats[team].addPlayer(userId, playerStats[userId]);
                teamStats[team == TEAM_T ? TEAM_CT : TEAM_T].removePlayer(userId);

            }
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
        public int immune { get; set; }
        public bool disconnected { get; set; }

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
            float killBonus = kills * 200f;
            float assistBonus = assists * 100f;
            float deathPenalty = deaths * 150f;
            float headshotBonus = headshotKills * 100f;

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
        public Dictionary<int, PlayerStats> playerList = new Dictionary<int, PlayerStats>();
        public int numPlayers() {
            return playerList.Count;
        }
        public void resetPlayers () {
            playerList.Clear();
        }
        public void addPlayer (int userId, PlayerStats stats) {
            if ( ! playerList.ContainsKey(userId) ) {
                playerList[userId] = stats;
            } 
        }
        public void removePlayer (int userId) {
            if ( playerList.ContainsKey(userId) ) {
                playerList.Remove(userId);
            }
        }

        public float getTotalSkill() {
            return playerList.Values.Sum(p => p.calcSkill());
        }

        public float getAverageSkill() {
            int count = numPlayers();
            float sum = count > 0 ? getTotalSkill() / count : 0f;
            sum += streak * 500f;
            return sum;
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
        public CCSPlayerController? getPlayerBySkillNonImmune(float targetSkill) {
            int bestPlayerId = -1;
            float bestDiff = float.MaxValue;
            foreach (var kvp in playerList) {
                var stats = kvp.Value;                
                float diff = Math.Abs(stats.calcSkill() - targetSkill);
                if (diff < bestDiff) {
                    bestDiff = diff;
                    bestPlayerId = kvp.Key;
                }
            }
            if (bestPlayerId == -1) {
                Console.WriteLine($"[DEBUG] OSBase[gamestats] - getPlayerBySkillNonImmune: No player found for skill {targetSkill}");
                return null;
            }
            return Utilities.GetPlayerFromUserid(bestPlayerId);
        }
        public CCSPlayerController? GetPlayerByDeviation(float targetDeviation, bool forStrongTeam) {
            if (float.IsInfinity(targetDeviation) || float.IsNaN(targetDeviation)) {
                // Fallback value – adjust this constant as appropriate.
                targetDeviation = 1000f;
            }

            // Find the leader (highest skilled player) in this team.
            int leaderId = -1;
            if (playerList.Count > 0) {
                leaderId = playerList.OrderByDescending(kvp => kvp.Value.calcSkill()).First().Key;
            }

            int bestPlayerId = -1;
            float bestDiff = float.MaxValue;
            float teamAvg = getAverageSkill();

            foreach (var kvp in playerList) {
                // Skip immune players and the team leader
                if (kvp.Value.immune > 0 || kvp.Key == leaderId)
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
        public void printPlayers() {
            foreach (var kvp in playerList) {
                Console.WriteLine($"[DEBUG] OSBase[gamestats] - Player {kvp.Key}: {kvp.Value.kills}k, {kvp.Value.assists}a, {kvp.Value.deaths}d, {kvp.Value.calcSkill()}p");
            }
        }

        public override string ToString() {
            return $"Wins: {wins}, Losses: {losses}, Streak: {streak}";
        }


    }
    
}