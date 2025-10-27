using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace OSBase.Modules {

    public class TeamBalancer {
        private const string ModuleName = "teambalancer";

        // External deps
        private readonly Config config;

        // Host delegates (wire from your core)
        private readonly Action<string,string> LogInfo;
        private readonly Action<string,string> LogDebug;
        private readonly Action<string,string> LogError;
        private readonly Action<string> SayToAll;
        private readonly Func<string,int,bool> MovePlayerToTeam; // steamid, teamInt(2=T,3=CT) -> ok?
        private readonly Func<string,string> GetPlayerName;

        // Toggles
        private bool Enabled = true;
        private bool LiveEnabled = true;
        private bool AllowDuringWarmup = true;
        private bool IgnoreBots = true;
        private bool Announce = true;
        private bool Debug = false;

        // Limits & thresholds (instant)
        private int MinPlayers = 6;
        private int MinRoundsBeforeBalance = 3;
        private int RoundCooldown = 3;            // min rounds between moves
        private int MaxMovesPerMatch = 6;
        private int ThresholdScoreDiff = 2;       // immediate scoreboard gap
        private int ThresholdSkillDiff = 250;     // immediate skill-sum gap

        // Rolling history thresholds (averaged over last N rounds)
        private int HistorySize = 6;
        private int MinAverageScoreDiff = 2;
        private int MinAverageSkillDiff = 200;

        // Cosmetic timing (kept for parity with your logs)
        private double IntermissionDelay = 0.8;

        // Map overrides: map -> kv
        private readonly Dictionary<string, Dictionary<string, string>> MapConfig = new(StringComparer.OrdinalIgnoreCase);

        // Runtime state
        private readonly Dictionary<string, PlayerState> Players = new();
        private readonly HashSet<string> RecentlyMoved = new();
        private readonly List<int> RecentScoreGaps = new();
        private readonly List<int> RecentSkillDeltas = new();

        private int LastMoveRound = -9999;
        private int MovesThisMatch = 0;
        private bool InWarmup = false;
        private string CurrentMap = "";

        // Skills (purely in-memory)
        // DefaultSkill is used when we have no info yet.
        private readonly Dictionary<string,int> Skill = new();
        private int DefaultSkill = 1000;

        private enum Team { Unassigned = 0, T = 2, CT = 3 }

        private sealed class PlayerState {
            public string SteamId = "";
            public Team Team = Team.Unassigned;
            public bool IsBot = false;
            public int LastMovedRound = -9999;
        }

        private sealed class Candidate {
            public string SteamId = "";
            public Team From;
            public Team To;
            public int NewDelta;
        }

        private sealed class TeamSnapshot {
            public int SumT;
            public int SumCT;
            public int TCount;
            public int CTCount;
            public int Delta { get { return SumCT - SumT; } } // + => CT stronger
        }

        public TeamBalancer (
            Config inConfig,
            Action<string,string> logInfo,
            Action<string,string> logDebug,
            Action<string,string> logError,
            Action<string> sayToAll,
            Func<string,int,bool> movePlayerToTeam,
            Func<string,string> getPlayerName
        ) {
            config = inConfig ?? throw new ArgumentNullException(nameof(inConfig));

            LogInfo = logInfo ?? ((m,s) => Console.WriteLine($"[INFO] {m}: {s}"));
            LogDebug = logDebug ?? ((m,s) => Console.WriteLine($"[DEBUG] {m}: {s}"));
            LogError = logError ?? ((m,s) => Console.WriteLine($"[ERROR] {m}: {s}"));
            SayToAll = sayToAll ?? (s => Console.WriteLine($"[SAY] {s}"));
            MovePlayerToTeam = movePlayerToTeam ?? ((id,t) => false);
            GetPlayerName = getPlayerName ?? (id => id);

            LoadBaseConfig();
            LoadMapOverrides();

            LogInfo(ModuleName, "Init: Loaded.");
        }

        // ===================== Public Hooks =====================

        public void OnMapStart ( string mapName, bool isWarmup ) {
            CurrentMap = mapName ?? "";
            InWarmup = isWarmup;

            MovesThisMatch = 0;
            LastMoveRound = -9999;
            RecentlyMoved.Clear();
            RecentScoreGaps.Clear();
            RecentSkillDeltas.Clear();

            // Keep Skill dictionary across the map by default.
            // If you want a fresh slate each map, uncomment the next line.
            // Skill.Clear();

            ApplyMapOverrides(CurrentMap);

            if (Debug) LogDebug(ModuleName, $"MapStart: {CurrentMap}, warmup={InWarmup}, enabled={Enabled}, live={LiveEnabled}");
            if (Announce && Enabled) {
                SaySafe($"[TeamBalancer] {(AllowDuringWarmup ? "Active during warmup" : "Inactive during warmup")}.");
            }
        }

        public void OnWarmupStart ( ) {
            InWarmup = true;
            if (Debug) LogDebug(ModuleName, "WarmupStart");
        }

        public void OnWarmupEnd ( ) {
            InWarmup = false;
            if (Debug) LogDebug(ModuleName, "WarmupEnd");
        }

        public void OnPlayerConnected ( string steamId64, bool isBot ) {
            if (string.IsNullOrEmpty(steamId64)) return;
            if (!Players.TryGetValue(steamId64, out var st)) {
                st = new PlayerState { SteamId = steamId64, IsBot = isBot };
                Players[steamId64] = st;
            } else {
                st.IsBot = isBot;
            }
            // If you like, seed a default skill immediately.
            if (!Skill.ContainsKey(steamId64)) Skill[steamId64] = DefaultSkill;
        }

        public void OnPlayerDisconnected ( string steamId64 ) {
            if (string.IsNullOrEmpty(steamId64)) return;
            Players.Remove(steamId64);
            RecentlyMoved.Remove(steamId64);
            // Keep skill cached so reconnects within the map have it;
            // if you want to drop it, uncomment:
            // Skill.Remove(steamId64);
        }

        public void OnTeamChanged ( string steamId64, int newTeam ) {
            if (string.IsNullOrEmpty(steamId64)) return;
            if (!Players.TryGetValue(steamId64, out var st)) {
                st = new PlayerState { SteamId = steamId64 };
                Players[steamId64] = st;
            }
            st.Team = NormalizeTeam(newTeam);
        }

        public void OnRoundEnd ( int roundNumber, int scoreT, int scoreCT ) {
            if (!Enabled || !LiveEnabled) return;
            if (InWarmup && !AllowDuringWarmup) return;
            if (!HasMinimumPlayers()) return;

            var snap = SnapshotTeams(IgnoreBots);
            int scoreGap = Math.Abs(scoreT - scoreCT);
            int skillGap = Math.Abs(snap.Delta);

            PushHistory(RecentScoreGaps, scoreGap, HistorySize);
            PushHistory(RecentSkillDeltas, skillGap, HistorySize);

            if (roundNumber < MinRoundsBeforeBalance && !InWarmup) {
                if (Debug) LogDebug(ModuleName, $"Skip early rounds: now={roundNumber}, need>={MinRoundsBeforeBalance}");
                return;
            }
            if (MovesThisMatch >= MaxMovesPerMatch) {
                if (Debug) LogDebug(ModuleName, $"Skip: move cap {MovesThisMatch}/{MaxMovesPerMatch}");
                return;
            }
            if ((roundNumber - LastMoveRound) < RoundCooldown) {
                if (Debug) LogDebug(ModuleName, $"Skip: cooldown {(roundNumber - LastMoveRound)}/{RoundCooldown}");
                return;
            }

            double avgScore = Average(RecentScoreGaps);
            double avgSkill = Average(RecentSkillDeltas);

            bool scoreTrip = scoreGap >= ThresholdScoreDiff || avgScore >= MinAverageScoreDiff;
            bool skillTrip = skillGap >= ThresholdSkillDiff || avgSkill >= MinAverageSkillDiff;
            if (!scoreTrip && !skillTrip) {
                if (Debug) LogDebug(ModuleName, $"No action: inst(score={scoreGap}, skill={skillGap}) avg(score={avgScore:F1}, skill={avgSkill:F1})");
                return;
            }

            Team stronger = snap.Delta > 0 ? Team.CT : (snap.Delta < 0 ? Team.T : (scoreCT >= scoreT ? Team.CT : Team.T));
            Team weaker = stronger == Team.CT ? Team.T : Team.CT;

            var fromPlayers = GetTeamPlayers(stronger);
            var toPlayers = GetTeamPlayers(weaker);
            if (fromPlayers.Count == 0 || toPlayers.Count == 0) return;

            var best = ChooseBestMove(snap, stronger, weaker, roundNumber, fromPlayers);
            if (best == null) {
                if (Debug) LogDebug(ModuleName, "No viable candidate (recently moved / cooldown / none improves delta).");
                return;
            }

            if (Move(best.SteamId, weaker)) {
                LastMoveRound = roundNumber;
                MovesThisMatch++;
                if (Players.TryGetValue(best.SteamId, out var st)) st.LastMovedRound = roundNumber;

                RecentlyMoved.Clear();
                RecentlyMoved.Add(best.SteamId);

                if (Announce) {
                    string who = SafeName(best.SteamId);
                    SaySafe($"[TeamBalancer] Moved {who} to {(weaker == Team.CT ? "CT" : "T")} to improve balance.");
                }

                if (Debug) {
                    LogDebug(ModuleName, $"Moved {best.SteamId}; Δbefore={Math.Abs(snap.Delta)}, Δafter={Math.Abs(best.NewDelta)}; avgScore={avgScore:F1}, avgSkill={avgSkill:F1}");
                }
            }
        }

        // ===================== External Skill API (no DB inside) =====================

        // Set/overwrite a player's skill. Call from GameStats (e.g., when you calculate/receive skill).
        public void SetSkill ( string steamId64, int skill ) {
            if (string.IsNullOrEmpty(steamId64)) return;
            Skill[steamId64] = Math.Max(0, skill);
            if (Debug) LogDebug(ModuleName, $"SetSkill {steamId64} = {skill}");
        }

        // Get current cached skill; returns false if unknown.
        public bool TryGetSkill ( string steamId64, out int skill ) {
            if (string.IsNullOrEmpty(steamId64)) { skill = DefaultSkill; return false; }
            if (Skill.TryGetValue(steamId64, out var s)) { skill = s; return true; }
            skill = DefaultSkill;
            return false;
        }

        // Seed many at once (e.g., after one bulk DB read you do elsewhere).
        public void BulkSetSkills ( IDictionary<string,int> snapshot ) {
            if (snapshot == null) return;
            foreach (var kv in snapshot) {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                Skill[kv.Key] = Math.Max(0, kv.Value);
            }
            if (Debug) LogDebug(ModuleName, $"BulkSetSkills seeded {snapshot.Count} players.");
        }

        // ===================== Decision Helpers =====================

        private Candidate? ChooseBestMove ( TeamSnapshot snap, Team from, Team to, int roundNumber, List<string> fromPlayers ) {
            int baseT = snap.SumT;
            int baseCT = snap.SumCT;
            int beforeAbs = Math.Abs(snap.Delta);

            Candidate? best = null;
            int now = roundNumber;
            int minSince = Math.Max(1, RoundCooldown);

            foreach (var steam in fromPlayers) {
                if (!Players.TryGetValue(steam, out var st)) continue;
                if (RecentlyMoved.Contains(steam)) continue;
                if ((now - st.LastMovedRound) < minSince) continue;

                int s = GetSkillLocal(steam);
                int newT = baseT;
                int newCT = baseCT;

                if (from == Team.CT && to == Team.T) { newCT -= s; newT += s; }
                else if (from == Team.T && to == Team.CT) { newT -= s; newCT += s; }

                int newDelta = newCT - newT;
                int newAbs = Math.Abs(newDelta);
                if (newAbs >= beforeAbs) continue; // must strictly improve

                if (best == null || newAbs < Math.Abs(best.NewDelta)) {
                    best = new Candidate { SteamId = steam, From = from, To = to, NewDelta = newDelta };
                }
            }

            return best;
        }

        private bool Move ( string steamId, Team target ) {
            try {
                int targetInt = target == Team.CT ? 3 : 2;
                bool ok = MovePlayerToTeam(steamId, targetInt);
                if (ok && Players.TryGetValue(steamId, out var st)) st.Team = target;
                return ok;
            } catch (Exception e) {
                LogError(ModuleName, $"Move failed {steamId}->{target}: {e.Message}");
                return false;
            }
        }

        // ===================== Snapshot / Players / Skill =====================

        private TeamSnapshot SnapshotTeams ( bool skipBots ) {
            var snap = new TeamSnapshot();
            foreach (var st in Players.Values) {
                if (st.Team != Team.T && st.Team != Team.CT) continue;
                if (skipBots && st.IsBot) continue;

                int s = GetSkillLocal(st.SteamId);
                if (st.Team == Team.CT) { snap.SumCT += s; snap.CTCount++; }
                else { snap.SumT += s; snap.TCount++; }
            }
            return snap;
        }

        private List<string> GetTeamPlayers ( Team team ) {
            return Players.Values
                .Where(p => p.Team == team && (!IgnoreBots || !p.IsBot))
                .Select(p => p.SteamId)
                .ToList();
        }

        private Team NormalizeTeam ( int teamInt ) {
            return teamInt == 3 ? Team.CT : teamInt == 2 ? Team.T : Team.Unassigned;
        }

        private bool HasMinimumPlayers ( ) {
            int count = Players.Values.Count(p => (p.Team == Team.T || p.Team == Team.CT) && (!IgnoreBots || !p.IsBot));
            return count >= MinPlayers;
        }

        private int GetSkillLocal ( string steamId ) {
            if (string.IsNullOrEmpty(steamId)) return DefaultSkill;
            if (Skill.TryGetValue(steamId, out var s)) return s;
            return DefaultSkill;
        }

        private string SafeName ( string steamId ) {
            try {
                string? n = GetPlayerName(steamId);
                return string.IsNullOrEmpty(n) ? steamId : n!;
            } catch { return steamId; }
        }

        // ===================== History Helpers =====================

        private void PushHistory ( List<int> buffer, int value, int cap ) {
            buffer.Add(value);
            if (buffer.Count > cap) buffer.RemoveAt(0);
        }

        private double Average ( List<int> buffer ) {
            if (buffer.Count == 0) return 0;
            long sum = 0;
            for (int i = 0; i < buffer.Count; i++) sum += buffer[i];
            return (double)sum / buffer.Count;
        }

        // ===================== Config =====================

        private void LoadBaseConfig ( ) {
            var path = "configs/teambalancer.cfg";
            var kv = LoadKv(path);

            Enabled = GetBool(kv, "enabled", true);
            LiveEnabled = GetBool(kv, "liveEnabled", true);
            AllowDuringWarmup = GetBool(kv, "allowDuringWarmup", true);
            IgnoreBots = GetBool(kv, "ignoreBots", true);
            Announce = GetBool(kv, "announce", true);
            Debug = GetBool(kv, "debug", false);

            MinPlayers = GetInt(kv, "minPlayers", 6);
            MinRoundsBeforeBalance = GetInt(kv, "minRoundsBeforeBalance", 3);
            RoundCooldown = GetInt(kv, "roundCooldown", 3);
            MaxMovesPerMatch = GetInt(kv, "maxMovesPerMatch", 6);
            ThresholdScoreDiff = GetInt(kv, "thresholdScoreDiff", 2);
            ThresholdSkillDiff = GetInt(kv, "thresholdSkillDiff", 250);

            HistorySize = GetInt(kv, "historySize", 6);
            MinAverageScoreDiff = GetInt(kv, "minAverageScoreDiff", 2);
            MinAverageSkillDiff = GetInt(kv, "minAverageSkillDiff", 200);

            IntermissionDelay = GetDouble(kv, "intermissionDelay", 0.8);

            DefaultSkill = GetInt(kv, "defaultSkill", 1000);

            LogInfo(ModuleName, $"Using local config '{path}' ({kv.Count} keys).");
        }

        private void ApplyMapOverrides ( string map ) {
            if (string.IsNullOrEmpty(map)) return;
            if (!MapConfig.TryGetValue(map, out var kv)) return;

            SetIfExistsBool(kv, "enabled", v => Enabled = v);
            SetIfExistsBool(kv, "liveEnabled", v => LiveEnabled = v);
            SetIfExistsBool(kv, "allowDuringWarmup", v => AllowDuringWarmup = v);
            SetIfExistsBool(kv, "ignoreBots", v => IgnoreBots = v);
            SetIfExistsBool(kv, "announce", v => Announce = v);
            SetIfExistsBool(kv, "debug", v => Debug = v);

            SetIfExistsInt(kv, "minPlayers", v => MinPlayers = v);
            SetIfExistsInt(kv, "minRoundsBeforeBalance", v => MinRoundsBeforeBalance = v);
            SetIfExistsInt(kv, "roundCooldown", v => RoundCooldown = v);
            SetIfExistsInt(kv, "maxMovesPerMatch", v => MaxMovesPerMatch = v);
            SetIfExistsInt(kv, "thresholdScoreDiff", v => ThresholdScoreDiff = v);
            SetIfExistsInt(kv, "thresholdSkillDiff", v => ThresholdSkillDiff = v);

            SetIfExistsInt(kv, "historySize", v => HistorySize = v);
            SetIfExistsInt(kv, "minAverageScoreDiff", v => MinAverageScoreDiff = v);
            SetIfExistsInt(kv, "minAverageSkillDiff", v => MinAverageSkillDiff = v);

            SetIfExistsDouble(kv, "intermissionDelay", v => IntermissionDelay = v);
            SetIfExistsInt(kv, "defaultSkill", v => DefaultSkill = v);

            if (Debug) LogDebug(ModuleName, $"MapConfig applied for '{map}'.");
        }

        private void LoadMapOverrides ( ) {
            var path = "configs/teambalancer_maps.cfg";
            try {
                if (!File.Exists(path)) return;
                int added = 0;
                foreach (var raw in File.ReadAllLines(path)) {
                    var line = (raw ?? "").Trim();
                    if (line.Length == 0 || line.StartsWith("//")) continue;

                    var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2) continue;

                    var map = parts[0].Trim();
                    var kv = ParseKvPairs(parts[1].Trim());
                    if (kv.Count > 0) { MapConfig[map] = kv; added++; }
                }
                LogDebug(ModuleName, $"MapConfig: Loaded {added} entries from '{path}'.");
            } catch (Exception e) {
                LogError(ModuleName, $"MapConfig load failed: {e.Message}");
            }
        }

        private Dictionary<string, string> LoadKv ( string path ) {
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try {
                if (!File.Exists(path)) {
                    string content = string.Join(Environment.NewLine, new [] {
                        "// TeamBalancer config",
                        "enabled=1",
                        "liveEnabled=1",
                        "allowDuringWarmup=1",
                        "ignoreBots=1",
                        "announce=1",
                        "debug=0",
                        "minPlayers=6",
                        "minRoundsBeforeBalance=3",
                        "roundCooldown=3",
                        "maxMovesPerMatch=6",
                        "thresholdScoreDiff=2",
                        "thresholdSkillDiff=250",
                        "historySize=6",
                        "minAverageScoreDiff=2",
                        "minAverageSkillDiff=200",
                        "intermissionDelay=0.8",
                        "defaultSkill=1000"
                    });
                    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                    File.WriteAllText(path, content);
                }

                foreach (var raw in File.ReadAllLines(path)) {
                    var line = (raw ?? "").Trim();
                    if (line.Length == 0 || line.StartsWith("//")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    kv[key] = val;
                }
            } catch (Exception e) {
                LogError(ModuleName, $"Failed to load '{path}': {e.Message}");
            }
            return kv;
        }

        private Dictionary<string, string> ParseKvPairs ( string s ) {
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts) {
                int eq = p.IndexOf('=');
                if (eq <= 0) continue;
                string k = p.Substring(0, eq).Trim();
                string v = p.Substring(eq + 1).Trim();
                kv[k] = v;
            }
            return kv;
        }

        private bool GetBool ( Dictionary<string, string> kv, string key, bool defVal ) {
            if (!kv.TryGetValue(key, out var v)) return defVal;
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private int GetInt ( Dictionary<string, string> kv, string key, int defVal ) {
            return kv.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : defVal;
        }

        private double GetDouble ( Dictionary<string, string> kv, string key, double defVal ) {
            return kv.TryGetValue(key, out var v) &&
                double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : defVal;
        }

        private void SetIfExistsBool ( Dictionary<string, string> kv, string key, Action<bool> set ) {
            if (kv.TryGetValue(key, out var v)) set(v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
        }

        private void SetIfExistsInt ( Dictionary<string, string> kv, string key, Action<int> set ) {
            if (kv.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) set(n);
        }

        private void SetIfExistsDouble ( Dictionary<string, string> kv, string key, Action<double> set ) {
            if (kv.TryGetValue(key, out var v) &&
                double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var n)) set(n);
        }

        // ===================== Say wrapper =====================

        private void SaySafe ( string msg ) {
            try { SayToAll(msg); } catch { }
        }
    }

}