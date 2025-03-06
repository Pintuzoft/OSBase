using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Core;
using System.Diagnostics.Contracts;
using MySqlConnector;

namespace OSBase.Modules {

    public class GameStats {
        public string ModuleName => "gamestats";
        private OSBase? osbase;
        private Config? config;
        private bool isWarmup = false;
        public int roundNumber = 0;
        private const int TEAM_S = (int)CsTeam.Spectator;
        private const int TEAM_T = (int)CsTeam.Terrorist;
        private const int TEAM_CT = (int)CsTeam.CounterTerrorist;

        // Store player stats keyed by user id.
        public Dictionary<int, PlayerStats> playerList = new Dictionary<int, PlayerStats>();
        private Dictionary<int, TeamStats> teamList = new Dictionary<int, TeamStats>();

        private Database db;

        public GameStats(OSBase inOsbase, Config inConfig) {
            this.osbase = inOsbase;
            this.config = inConfig;
            this.db = new Database(this.osbase, this.config);
            createTables();
            loadEventHandlers();
            teamList[TEAM_S] = new TeamStats();
            teamList[TEAM_T] = new TeamStats();
            teamList[TEAM_CT] = new TeamStats();            
        }

        private void createTables ( ) {
            string query = "TABLE IF NOT EXISTS skill_log (steamid varchar(32),name varchar(64),skill int(11), datestr datetime);";            
            try {
                this.db.create(query);
            } catch (Exception e) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error creating table: {e.Message}");
            }
        }

        public void loadEventHandlers ( ) {
            // Register relevant events.
            osbase?.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
            osbase?.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            osbase?.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
            osbase?.RegisterEventHandler<EventRoundStart>(OnRoundStart);
            osbase?.RegisterListener<Listeners.OnMapStart>(OnMapStart);
            osbase?.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
           // osbase?.RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
            osbase?.RegisterEventHandler<EventStartHalftime>(OnStartHalftime);
            osbase?.RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
        }

        private void OnMapEnd ( ) {
            // MariaDB [osbase]> describe skill_log;
            // +---------+-------------+------+-----+---------+-------+
            // | Field   | Type        | Null | Key | Default | Extra |
            // +---------+-------------+------+-----+---------+-------+
            // | steamid | varchar(32) | YES  |     | NULL    |       |
            // | name    | varchar(64) | YES  |     | NULL    |       |
            // | skill   | int(11)     | YES  |     | NULL    |       |
            // | datestr | datetime    | YES  |     | NULL    |       |
            // +---------+-------------+------+-----+---------+-------+
            // 4 rows in set (0.002 sec)
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Map ended. Writing player stats to database.");
            foreach (var entry in playerList ) {
                if ( entry.Value.steamid.Equals("0") || entry.Value.name.Length == 0 || entry.Value.rounds < 10 ) {
                    continue;
                }
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Writing stats for player {entry.Value.name} ({entry.Value.steamid})");
                string query = "INTO skill_log (steamid, name, skill, datestr) VALUES (@steamid, @name, @skill, NOW());";
                var parameters = new MySqlParameter[] {
                    new MySqlParameter("@steamid", entry.Value.steamid),
                    new MySqlParameter("@name", entry.Value.name),
                    new MySqlParameter("@skill", entry.Value.calcSkill())
                };
                try {
                    this.db.insert(query, parameters);
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Inserted stats for player {entry.Value.name} ({entry.Value.steamid})");
                } catch (Exception e) {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error inserting into table: {e.Message}");
                }
            }
        }

        private HookResult OnPlayerHurt(EventPlayerHurt eventInfo, GameEventInfo gameEventInfo) {
            if(isWarmup) return HookResult.Continue;
            if (eventInfo.Attacker != null && eventInfo.Attacker.UserId.HasValue) {
                int attackerId = eventInfo.Attacker.UserId.Value;
                if ( ! playerList.ContainsKey(attackerId) ) {
                    playerList[attackerId] = new PlayerStats();
                }
                playerList[attackerId].damage += eventInfo.DmgHealth;
                playerList[attackerId].shotsHit++;
            }            
            return HookResult.Continue;
        }

        private HookResult OnWeaponFire(EventWeaponFire eventInfo, GameEventInfo gameEventInfo) {
            if(isWarmup) return HookResult.Continue;
            if ( eventInfo.Userid == null || eventInfo.Userid.UserId == null || ! eventInfo.Userid.UserId.HasValue ) {
                return HookResult.Continue;
            }
            int shooterId = eventInfo.Userid.UserId.Value;
            if ( ! playerList.ContainsKey(shooterId) ) {
                playerList[shooterId] = new PlayerStats();
            }
            playerList[shooterId].shotsFired++;
            return HookResult.Continue;
        }

        // Update stats when a player dies.
        private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
            if(isWarmup) return HookResult.Continue;
            if (eventInfo.Attacker != null && eventInfo.Attacker.UserId.HasValue) {
                int attackerId = eventInfo.Attacker.UserId.Value;
                if ( ! playerList.ContainsKey(attackerId) ) {
                    playerList[attackerId] = new PlayerStats();
                }
                playerList[attackerId].kills++;
                if ( eventInfo.Hitgroup == 1 ) {
                    playerList[attackerId].headshotKills++;
                }
            }
            if (eventInfo.Userid != null && eventInfo.Userid.UserId.HasValue) {
                int victimId = eventInfo.Userid.UserId.Value;
                if ( ! playerList.ContainsKey(victimId) ) {
                    playerList[victimId] = new PlayerStats();
                }
                playerList[victimId].deaths++;
            }
            // Optionally update assists if available.
            if (eventInfo.Assister != null && eventInfo.Assister.UserId.HasValue) {
                int assistId = eventInfo.Assister.UserId.Value;
                if ( ! playerList.ContainsKey(assistId) ) {
                    playerList[assistId] = new PlayerStats();
                }
                playerList[assistId].assists++;
            }
            return HookResult.Continue;
        }

        private void printTeams() {
            TeamStats teamt = teamList[TEAM_T];
            TeamStats teamct = teamList[TEAM_CT];
            TeamStats teamspec = teamList[TEAM_S];
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Team T: {teamt.wins}w, {teamt.losses}l, {teamt.streak}s, {teamt.getAverageSkill()}p");
            teamt.printPlayers();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Team CT: {teamct.wins}w, {teamct.losses}l, {teamct.streak}s, {teamct.getAverageSkill()}p");
            teamct.printPlayers();  
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Team SPEC: {teamspec.getAverageSkill()}p");
            teamspec.printPlayers();
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
            if(isWarmup) {
                isWarmup = false;
                clearStats();
                return HookResult.Continue;
            }
            // Update team player lists
            teamList[TEAM_T].resetPlayers();
            teamList[TEAM_CT].resetPlayers();
            teamList[TEAM_S].resetPlayers();
            foreach (var player in Utilities.GetPlayers()) {
                if (player != null && player.UserId.HasValue && ! player.IsHLTV ) {
                    if ( ! playerList.ContainsKey(player.UserId.Value) ) {
                        playerList[player.UserId.Value] = new PlayerStats();
                    }
                    if ( playerList[player.UserId.Value].name.Length == 0 ) {
                        playerList[player.UserId.Value].name = player.PlayerName ?? "";
                    }
                    if ( playerList[player.UserId.Value].steamid.Length == 0 ) {
                        playerList[player.UserId.Value].steamid = player.SteamID+"";
                    }
                    
                    switch (player.TeamNum) {
                        case TEAM_S:
                            teamList[TEAM_S].addPlayer(player.UserId.Value, playerList[player.UserId.Value]);
                            break;
                        case TEAM_T:
                            teamList[TEAM_T].addPlayer(player.UserId.Value, playerList[player.UserId.Value]);
                            break;
                        case TEAM_CT:
                            teamList[TEAM_CT].addPlayer(player.UserId.Value, playerList[player.UserId.Value]);
                            break;
                        default:
                            break;
                    }
                }
            }

            // Update team stats.
            if (eventInfo.Winner == TEAM_T) {
                teamList[TEAM_T].streak++;
                teamList[TEAM_CT].streak = 0;
                teamList[TEAM_T].incWins();
                teamList[TEAM_CT].incLosses();
            } else if (eventInfo.Winner == TEAM_CT) {
                teamList[TEAM_CT].streak++;
                teamList[TEAM_T].streak = 0;
                teamList[TEAM_CT].incWins();
                teamList[TEAM_T].incLosses();
            }

            teamList[TEAM_T].incRounds();
            teamList[TEAM_CT].incRounds();

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Round ended. Current player stats:");
            foreach (var entry in teamList) {
                if ( entry.Key == TEAM_S ) {
                    continue;
                }
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Team [{(entry.Key == TEAM_T ? "T" : "CT")}]: {entry.ToString()}");
                entry.Value.printPlayers();
            }

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - [T]: {teamList[TEAM_T].getAverageSkill()}, [CT]: {teamList[TEAM_CT].getAverageSkill()}");
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - [T]: {teamList[TEAM_T].numPlayers()}, [CT]: {teamList[TEAM_CT].numPlayers()}");

            return HookResult.Continue;
        }

        private HookResult OnStartHalftime(EventStartHalftime eventInfo, GameEventInfo gameEventInfo) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - OnStartHalftime triggered.");
            TeamStats buf = teamList[TEAM_T];
            teamList[TEAM_T] = teamList[TEAM_CT];
            teamList[TEAM_CT] = buf;
            return HookResult.Continue;
        }

        // Reset stats at the start of a new map.
         private void OnMapStart(string mapName) {
            this.isWarmup = true;
            this.roundNumber = 0;
            clearStats();
        }

        // Public method to access a player's stats.
        public PlayerStats GetPlayerStats(int userId) {
            if (playerList.ContainsKey(userId)) {
                return playerList[userId];
            }
            return new PlayerStats(); // Return an empty stats object if none exists.
        }

        private void clearStats() {
            // Reset team stats.
            playerList.Clear();
            teamList.Clear();
            teamList[TEAM_S] = new TeamStats();
            teamList[TEAM_T] = new TeamStats();
            teamList[TEAM_CT] = new TeamStats();
             
            // Reset stats for all players.
            playerList.Clear();
            foreach (var player in Utilities.GetPlayers()) {
                if (player != null && player.UserId.HasValue && ! player.IsHLTV ) {
                    playerList[player.UserId.Value] = new PlayerStats();
                    switch (player.TeamNum) {
                        case TEAM_S:
                            teamList[TEAM_S].addPlayer(player.UserId.Value, playerList[player.UserId.Value]);
                            break;
                        case TEAM_T:
                            teamList[TEAM_T].addPlayer(player.UserId.Value, playerList[player.UserId.Value]);
                            break;
                        case TEAM_CT:
                            teamList[TEAM_CT].addPlayer(player.UserId.Value, playerList[player.UserId.Value]);
                            break;
                        default:
                            break;
                    }
                }
            }
        }
        public TeamStats getTeam (int team) {
            if ( team == TEAM_T || team == TEAM_CT) {
                return teamList[team];
            }
            return new TeamStats();     
        }

        // Get team players
        public Dictionary<int, PlayerStats> getTeamPlayers (int team) {
            if ( team == TEAM_T || team == TEAM_CT || team == TEAM_S ) {
                return teamList[team].playerList;
            }
            return new Dictionary<int, PlayerStats>();
        }

        public void movePlayer ( int userId, int team ) {
            Console.WriteLine($"[DEBUG] OSBase[gamestats] - movePlayer: {userId} to team {team}");
            if ( team != (TEAM_T | TEAM_CT) ) {
                Console.WriteLine($"[DEBUG] OSBase[gamestats] - movePlayer: Invalid team {team}");
                return;
            }
            
            if ( playerList.ContainsKey(userId) ) {
                Console.WriteLine($"[DEBUG] OSBase[gamestats] - movePlayer: Player {userId} found.");
                PlayerStats pstats = playerList[userId];
                if ( teamList[TEAM_T] == null ) {
                    teamList[TEAM_T] = new TeamStats();
                }
                if ( teamList[TEAM_CT] == null ) {
                    teamList[TEAM_CT] = new TeamStats();
                }
                teamList[TEAM_T].removePlayer(userId);
                teamList[TEAM_CT].removePlayer(userId);
                teamList[team].addPlayer(userId, playerList[userId]);
                teamList[team == TEAM_T ? TEAM_CT : TEAM_T].removePlayer(userId);
                Console.WriteLine($"[DEBUG] OSBase[gamestats] - movePlayer: Player {userId} moved to team {team}");
            } else {
                Console.WriteLine($"[DEBUG] OSBase[gamestats] - movePlayer: Player {userId} not found.");
            }
        }
    }


    // Data container for game statistics.
    public class PlayerStats {
        public string name { get; set; } = string.Empty;
        public string steamid { get; set; } = string.Empty;
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

        public void incWins() {
            wins++;
            foreach (var p in playerList) {
                p.Value.roundWins++;
            }
        }

        public void incLosses() {
            losses++;
            foreach (var p in playerList) {
                p.Value.roundLosses++;
            }
        }

        public void incRounds() {
            foreach (var p in playerList) {
                p.Value.rounds++;
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
                Console.WriteLine($"[DEBUG] OSBase[gamestats] - Player {kvp.Key}/{kvp.Value.name}: {kvp.Value.rounds}r/{kvp.Value.roundWins}w/{kvp.Value.roundLosses}l - {kvp.Value.kills}k/{kvp.Value.assists}a/{kvp.Value.deaths}d:{kvp.Value.damage}dmg -> {kvp.Value.calcSkill()}p");
            }
        }

        public override string ToString() {
            return $"Wins: {wins}, Losses: {losses}, Streak: {streak}";
        }


    }
    
}