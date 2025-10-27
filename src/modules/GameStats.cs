using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using MySqlConnector;

namespace OSBase.Modules {
    public class GameStats {
        public string ModuleName => "gamestats";
        public static GameStats? Current { get; private set; }
        
        private OSBase osbase;
        private Config config;

        private bool isWarmup = false;
        public int roundNumber = 0;

        private const int TEAM_S = (int)CsTeam.Spectator;
        private const int TEAM_T = (int)CsTeam.Terrorist;
        private const int TEAM_CT = (int)CsTeam.CounterTerrorist;

        private bool hasLoadedEvents = false;

        // Per-userId stats (UserId is ephemeral per map; we also store SteamID inside PlayerStats)
        public Dictionary<int, PlayerStats> playerList = new Dictionary<int, PlayerStats>();

        // Team containers for active bookkeeping (T/CT/SPEC)
        private Dictionary<int, TeamStats> teamList = new Dictionary<int, TeamStats>();

        private Database db;

        public GameStats ( OSBase inOsbase, Config inConfig ) {
            Current = this;
            osbase = inOsbase;
            config = inConfig;
            db = new Database(osbase, config);

            createTables();
            loadEventHandlers();
            hasLoadedEvents = true;

            teamList[TEAM_S] = new TeamStats();
            teamList[TEAM_T] = new TeamStats();
            teamList[TEAM_CT] = new TeamStats();

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Init: GameStats constructed.");
        }

        private void createTables ( ) {
            string query =
                "TABLE IF NOT EXISTS skill_log (" +
                "steamid varchar(32)," +
                "name varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci," +
                "skill int(11)," +
                "datestr datetime," +
                "mapname varchar(64) NULL" +
                ");";
            try {
                db.create(query);
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - DB: skill_log ensured.");
            } catch (Exception e) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - CreateTables: {e.Message}");
            }
        }

        public void loadEventHandlers ( ) {
            if (hasLoadedEvents) {
                return;
            }

            osbase.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
            osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
            osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);
            osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);
            osbase.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
            osbase.RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
            osbase.RegisterEventHandler<EventStartHalftime>(OnStartHalftime);
            osbase.RegisterEventHandler<EventWeaponFire>(OnWeaponFire);

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Handlers loaded.");
        }

        // ===== Event handlers =====

        private void OnMapStart ( string mapName ) {
            isWarmup = true;
            roundNumber = 0;
            clearStats();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - MapStart: {mapName}");
        }

        private void OnMapEnd ( ) {
            // Persist only with a reasonable population
            if (playerList.Count < 10) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - MapEnd: Not enough players to write stats.");
                return;
            }

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - MapEnd: Writing player stats.");
            foreach (var entry in playerList) {
                var ps = entry.Value;

                if (ps.steamid.Equals("0") || ps.name.Length == 0 || ps.rounds < 10) {
                    continue;
                }

                string query =
                    "INSERT INTO skill_log (steamid, name, skill, datestr, mapname) " +
                    "VALUES (@steamid, @name, @skill, NOW(), @mapname);";

                var parameters = new MySqlParameter[] {
                    new MySqlParameter("@steamid", ps.steamid),
                    new MySqlParameter("@name", ps.name),
                    new MySqlParameter("@skill", ps.calcSkill()),
                    new MySqlParameter("@mapname", Server.MapName ?? "")
                };

                try {
                    db.insert(query, parameters);
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - DB: Inserted {ps.name} ({ps.steamid}).");
                } catch (Exception e) {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - MapEndInsert: {e.Message}");
                }
            }

            playerList.Clear();
            teamList[TEAM_T].resetPlayers();
            teamList[TEAM_CT].resetPlayers();
            teamList[TEAM_S].resetPlayers();

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - MapEnd: Stats cleared.");
        }

        private HookResult OnWarmupEnd ( EventWarmupEnd ev, GameEventInfo info ) {
            isWarmup = false;
            clearStats();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - WarmupEnd.");
            return HookResult.Continue;
        }

        private HookResult OnWeaponFire ( EventWeaponFire ev, GameEventInfo info ) {
            if (isWarmup) return HookResult.Continue;
            if (ev.Userid?.UserId == null || !ev.Userid.UserId.HasValue) return HookResult.Continue;

            int shooterId = ev.Userid.UserId.Value;
            if (!playerList.ContainsKey(shooterId)) {
                playerList[shooterId] = new PlayerStats();
            }
            playerList[shooterId].shotsFired++;
            return HookResult.Continue;
        }

        private HookResult OnPlayerHurt ( EventPlayerHurt ev, GameEventInfo info ) {
            if (isWarmup) return HookResult.Continue;

            if (ev.Attacker != null && ev.Attacker.UserId.HasValue) {
                int attackerId = ev.Attacker.UserId.Value;
                if (!playerList.ContainsKey(attackerId)) {
                    playerList[attackerId] = new PlayerStats();
                }
                playerList[attackerId].damage += ev.DmgHealth;
                playerList[attackerId].shotsHit++;
            }
            return HookResult.Continue;
        }

        private HookResult OnPlayerDeath ( EventPlayerDeath ev, GameEventInfo info ) {
            if (isWarmup) return HookResult.Continue;

            if (ev.Attacker != null && ev.Attacker.UserId.HasValue) {
                int attackerId = ev.Attacker.UserId.Value;
                if (!playerList.ContainsKey(attackerId)) {
                    playerList[attackerId] = new PlayerStats();
                }
                playerList[attackerId].kills++;
                if (ev.Hitgroup == 1) {
                    playerList[attackerId].headshotKills++;
                }
            }

            if (ev.Userid != null && ev.Userid.UserId.HasValue) {
                int victimId = ev.Userid.UserId.Value;
                if (!playerList.ContainsKey(victimId)) {
                    playerList[victimId] = new PlayerStats();
                }
                playerList[victimId].deaths++;
            }

            if (ev.Assister != null && ev.Assister.UserId.HasValue) {
                int assistId = ev.Assister.UserId.Value;
                if (!playerList.ContainsKey(assistId)) {
                    playerList[assistId] = new PlayerStats();
                }
                playerList[assistId].assists++;
            }

            return HookResult.Continue;
        }

        private HookResult OnRoundStart ( EventRoundStart ev, GameEventInfo info ) {
            if (isWarmup) return HookResult.Continue;

            roundNumber++;
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - RoundStart: {roundNumber}");

            return HookResult.Continue;
        }

        private HookResult OnRoundEnd ( EventRoundEnd ev, GameEventInfo info ) {
            if (isWarmup) return HookResult.Continue;

            // Rebuild team membership snapshot each round end
            teamList[TEAM_T].resetPlayers();
            teamList[TEAM_CT].resetPlayers();
            teamList[TEAM_S].resetPlayers();

            foreach (var p in Utilities.GetPlayers()) {
                if (p != null && p.UserId.HasValue && !p.IsHLTV) {
                    if (!playerList.ContainsKey(p.UserId.Value)) {
                        playerList[p.UserId.Value] = new PlayerStats();
                    }
                    var ps = playerList[p.UserId.Value];
                    if (ps.name.Length == 0) ps.name = p.PlayerName ?? "";
                    if (ps.steamid.Length == 0) ps.steamid = p.SteamID.ToString();

                    switch (p.TeamNum) {
                        case TEAM_S:
                            teamList[TEAM_S].addPlayer(p.UserId.Value, ps);
                            break;
                        case TEAM_T:
                            teamList[TEAM_T].addPlayer(p.UserId.Value, ps);
                            break;
                        case TEAM_CT:
                            teamList[TEAM_CT].addPlayer(p.UserId.Value, ps);
                            break;
                        default:
                            break;
                    }
                }
            }

            // Update team W/L and streaks from the winner code
            if (ev.Winner == TEAM_T) {
                teamList[TEAM_T].streak++;
                teamList[TEAM_CT].streak = 0;
                teamList[TEAM_T].incWins();
                teamList[TEAM_CT].incLosses();
            } else if (ev.Winner == TEAM_CT) {
                teamList[TEAM_CT].streak++;
                teamList[TEAM_T].streak = 0;
                teamList[TEAM_CT].incWins();
                teamList[TEAM_T].incLosses();
            }

            teamList[TEAM_T].incRounds();
            teamList[TEAM_CT].incRounds();

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - RoundEnd: T(avg)={teamList[TEAM_T].getAverageSkill()} CT(avg)={teamList[TEAM_CT].getAverageSkill()}");
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - RoundEnd: T({teamList[TEAM_T].numPlayers()}), CT({teamList[TEAM_CT].numPlayers()}), SPEC({teamList[TEAM_S].numPlayers()})");

            return HookResult.Continue;
        }

        private HookResult OnStartHalftime ( EventStartHalftime ev, GameEventInfo info ) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Halftime.");
            // Flip references (keeps streaks per “new” sides)
            var buf = teamList[TEAM_T];
            teamList[TEAM_T] = teamList[TEAM_CT];
            teamList[TEAM_CT] = buf;
            return HookResult.Continue;
        }

        // ===== Public helpers =====

        public PlayerStats GetPlayerStats ( int userId ) {
            if (playerList.ContainsKey(userId)) {
                return playerList[userId];
            }
            return new PlayerStats();
        }

        public TeamStats getTeam ( int team ) {
            if (team == TEAM_T || team == TEAM_CT || team == TEAM_S) {
                return teamList.TryGetValue(team, out var ts) ? ts : new TeamStats();
            }
            return new TeamStats();
        }

        public Dictionary<int, PlayerStats> getTeamPlayers ( int team ) {
            if (team == TEAM_T || team == TEAM_CT || team == TEAM_S) {
                return teamList.TryGetValue(team, out var ts) ? ts.playerList : new Dictionary<int, PlayerStats>();
            }
            return new Dictionary<int, PlayerStats>();
        }

        // Bookkeeping only: keep internal team tracking in sync (T/CT/SPEC)
        public void movePlayer ( int userId, int team ) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - movePlayer: {userId} -> team {team}");

            if (!playerList.ContainsKey(userId)) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - movePlayer: Player {userId} not found.");
                return;
            }

            if (!teamList.ContainsKey(TEAM_T)) teamList[TEAM_T] = new TeamStats();
            if (!teamList.ContainsKey(TEAM_CT)) teamList[TEAM_CT] = new TeamStats();
            if (!teamList.ContainsKey(TEAM_S)) teamList[TEAM_S] = new TeamStats();

            teamList[TEAM_T].removePlayer(userId);
            teamList[TEAM_CT].removePlayer(userId);
            teamList[TEAM_S].removePlayer(userId);

            if (team == TEAM_T || team == TEAM_CT || team == TEAM_S) {
                teamList[team].addPlayer(userId, playerList[userId]);
            } else {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - movePlayer: Invalid team {team}, defaulting SPEC.");
                teamList[TEAM_S].addPlayer(userId, playerList[userId]);
            }
        }

        // Quick lookup by SteamID (returns T/CT/SPEC; defaults to SPEC if unknown)
        public int GetTeamBySteam ( ulong steamId64 ) {
            string sid = steamId64.ToString();
            if (teamList.ContainsKey(TEAM_T))
                foreach (var kv in teamList[TEAM_T].playerList)
                    if (kv.Value.steamid == sid) return TEAM_T;

            if (teamList.ContainsKey(TEAM_CT))
                foreach (var kv in teamList[TEAM_CT].playerList)
                    if (kv.Value.steamid == sid) return TEAM_CT;

            if (teamList.ContainsKey(TEAM_S))
                foreach (var kv in teamList[TEAM_S].playerList)
                    if (kv.Value.steamid == sid) return TEAM_S;

            return TEAM_S;
        }

        // Live skill lookup for TeamBalancer (by Steam)
        public bool TryGetLiveSkillBySteam ( ulong steamId64, out double skill, out int rounds ) {
            string sid = steamId64.ToString();
            foreach (var kv in playerList) {
                var ps = kv.Value;
                if (ps.steamid == sid) {
                    skill = ps.calcSkill();
                    rounds = ps.rounds;
                    return true;
                }
            }
            skill = 0;
            rounds = 0;
            return false;
        }

        // Mean/std across ACTIVE players only (T + CT), excludes spectators
        public (double mean, double std, int count) GetLiveSkillMomentsActive ( ) {
            var vals = new List<double>();

            if (!teamList.ContainsKey(TEAM_T)) teamList[TEAM_T] = new TeamStats();
            if (!teamList.ContainsKey(TEAM_CT)) teamList[TEAM_CT] = new TeamStats();

            foreach (var kv in teamList[TEAM_T].playerList) vals.Add(kv.Value.calcSkill());
            foreach (var kv in teamList[TEAM_CT].playerList) vals.Add(kv.Value.calcSkill());

            int n = vals.Count;
            if (n <= 1) return (10000.0, 1.0, n);

            double mean = vals.Average();
            double var = 0.0;
            foreach (var v in vals) var += (v - mean) * (v - mean);
            var /= (n - 1);
            double std = Math.Sqrt(Math.Max(var, 1e-6));
            return (mean, std, n);
        }

        // ===== Internals =====

        private void clearStats ( ) {
            playerList.Clear();
            teamList.Clear();

            teamList[TEAM_S] = new TeamStats();
            teamList[TEAM_T] = new TeamStats();
            teamList[TEAM_CT] = new TeamStats();

            foreach (var p in Utilities.GetPlayers()) {
                if (p != null && p.UserId.HasValue && !p.IsHLTV) {
                    playerList[p.UserId.Value] = new PlayerStats();
                    switch (p.TeamNum) {
                        case TEAM_S:
                            teamList[TEAM_S].addPlayer(p.UserId.Value, playerList[p.UserId.Value]);
                            break;
                        case TEAM_T:
                            teamList[TEAM_T].addPlayer(p.UserId.Value, playerList[p.UserId.Value]);
                            break;
                        case TEAM_CT:
                            teamList[TEAM_CT].addPlayer(p.UserId.Value, playerList[p.UserId.Value]);
                            break;
                        default:
                            break;
                    }
                }
            }
        }
    }

    // ===== Data classes =====

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

        // Your calcSkill formula (unchanged)
        public float calcSkill ( ) {
            if (rounds == 0)
                return 10000f;

            float avgDamage = (float)damage / rounds;
            float baseDamageScore = 4000f + (avgDamage * 60f);

            float killBonus = kills * 200f;
            float assistBonus = assists * 100f;
            float deathPenalty = deaths * 150f;
            float headshotBonus = headshotKills * 100f;

            float accuracy = shotsFired > 0 ? (float)shotsHit / shotsFired : 0f;
            float baselineAccuracy = 0.3f;
            float accuracyBonus = accuracy > baselineAccuracy ? (accuracy - baselineAccuracy) * 2000f : 0f;

            float totalRating = baseDamageScore + killBonus + assistBonus - deathPenalty + headshotBonus + accuracyBonus;
            return totalRating;
        }
    }

    public class TeamStats {
        public int wins { get; set; }
        public int losses { get; set; }
        public int streak { get; set; }

        public Dictionary<int, PlayerStats> playerList = new Dictionary<int, PlayerStats>();

        public int numPlayers ( ) {
            return playerList.Count;
        }

        public void resetPlayers ( ) {
            playerList.Clear();
        }

        public void addPlayer ( int userId, PlayerStats stats ) {
            if (!playerList.ContainsKey(userId)) {
                playerList[userId] = stats;
            }
        }

        public void removePlayer ( int userId ) {
            if (playerList.ContainsKey(userId)) {
                playerList.Remove(userId);
            }
        }

        public void incWins ( ) {
            wins++;
            foreach (var p in playerList) {
                p.Value.roundWins++;
            }
        }

        public void incLosses ( ) {
            losses++;
            foreach (var p in playerList) {
                p.Value.roundLosses++;
            }
        }

        public void incRounds ( ) {
            foreach (var p in playerList) {
                p.Value.rounds++;
            }
        }

        public float getTotalSkill ( ) {
            return playerList.Values.Sum(p => p.calcSkill());
        }

        public float getAverageSkill ( ) {
            int count = numPlayers();
            float sum = count > 0 ? getTotalSkill() / count : 0f;
            sum += streak * 500f;
            return sum;
        }

        public CCSPlayerController? getPlayerBySkill ( float targetSkill ) {
            int bestPlayerId = -1;
            float bestDiff = float.MaxValue;

            foreach (var kvp in playerList) {
                var stats = kvp.Value;
                if (stats.immune > 0) continue;

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

        public CCSPlayerController? getPlayerBySkillNonImmune ( float targetSkill ) {
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

        public CCSPlayerController? GetPlayerByDeviation ( float targetDeviation, bool forStrongTeam ) {
            if (float.IsInfinity(targetDeviation) || float.IsNaN(targetDeviation)) {
                targetDeviation = 1000f;
            }

            int leaderId = -1;
            if (playerList.Count > 0) {
                leaderId = playerList.OrderByDescending(kvp => kvp.Value.calcSkill()).First().Key;
            }

            int bestPlayerId = -1;
            float bestDiff = float.MaxValue;
            float teamAvg = getAverageSkill();

            foreach (var kvp in playerList) {
                if (kvp.Value.immune > 0 || kvp.Key == leaderId) continue;

                float playerSkill = kvp.Value.calcSkill();
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

        public void printPlayers ( ) {
            foreach (var kvp in playerList) {
                Console.WriteLine($"[DEBUG] OSBase[gamestats] - Player {kvp.Key}/{kvp.Value.name}: {kvp.Value.rounds}r/{kvp.Value.roundWins}w/{kvp.Value.roundLosses}l - {kvp.Value.kills}k/{kvp.Value.assists}a/{kvp.Value.deaths}d:{kvp.Value.damage}dmg -> {kvp.Value.calcSkill()}p");
            }
        }

        public override string ToString ( ) {
            return $"Wins: {wins}, Losses: {losses}, Streak: {streak}";
        }
    }
}