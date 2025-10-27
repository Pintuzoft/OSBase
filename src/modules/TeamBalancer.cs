using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules {
    public class TeamBalancer : IModule {
        public string ModuleName => "teambalancer";

        private OSBase? osbase;
        private bool enabled;

        // External live stats (no DB here)
        private GameStats? gameStats;

        // ===== Module config (from configs/teambalancer.cfg) =====
        private string mapConfigPath = "configs/teambalancer_maps.cfg";
        private float intermissionBalanceDelay = 0.8f;
        private bool suppressNearHalftime = true;  // rounds 14..16
        private int suppressLastNRounds = 3;       // e.g., 28..30
        private int swapSearchTopN = 3;
        private int moveCooldownRounds = 4;
        private int liveWarmupRounds = 2;
        private int maxSwapsPerIntermission = 2;
        private int maxMovesPerIntermission = 3;

        // Live influence
        private bool liveEnabled = true;
        private double knownZScale = 50.0;
        private double liveWeightKnown = 0.25;
        private double liveDeltaCapKnown = 50.0;
        private double unknownZScale = 120.0;
        private double liveDeltaCapUnknown = 150.0;
        private int minRoundsForUnknownMove = 3;

        // ===== State =====
        private int currentRound = 0;
        private int ctScore = 0;
        private int tScore = 0;
        private readonly Dictionary<ulong, int> lastMoveRound = new();

        // Map metadata
        private readonly Dictionary<string, int> mapSites = new(StringComparer.OrdinalIgnoreCase);           // de_dust2 -> 2
        private readonly Dictionary<string, CsTeam> preferSideByMap = new(StringComparer.OrdinalIgnoreCase); // de_lake -> T

        // ===== Load / Unload =====
        public void Load ( OSBase inOsbase, Config config ) {
            osbase = inOsbase;

            // Enable ONLY from OSBase.cfg (global toggles)
            enabled = config.GetGlobalConfigValue("teambalancer", "0") == "1";
            if (!enabled) {
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Init: Module disabled in OSBase.cfg.");
                return;
            }

            // Ensure local configs exist (auto-create with sane defaults)
            var localPath = "configs/teambalancer.cfg";
            EnsureLocalConfigExists(localPath);

            // Local module config
            var kv = LoadLocalConfig(localPath);
            Console.WriteLine(kv.Count == 0
                ? $"[WARN] OSBase[{ModuleName}] - Local config '{localPath}' not found or empty. Using defaults."
                : $"[INFO] OSBase[{ModuleName}] - Using local config '{localPath}' ({kv.Count} keys).");

            mapConfigPath = kv.TryGetValue("teambalancer_map_config", out var vMap) ? vMap : "configs/teambalancer_maps.cfg";
            EnsureMapConfigExists(mapConfigPath);

            intermissionBalanceDelay = kv.TryGetValue("teambalancer_intermission_delay", out var vDelay) && float.TryParse(vDelay, out var fDelay) ? Math.Max(0.1f, fDelay) : 0.8f;
            suppressNearHalftime     = kv.TryGetValue("teambalancer_suppress_near_halftime", out var vHf) ? (vHf == "1") : true;
            suppressLastNRounds      = kv.TryGetValue("teambalancer_suppress_last_n_rounds", out var vLast) && int.TryParse(vLast, out var iLast) ? iLast : 3;
            swapSearchTopN           = kv.TryGetValue("teambalancer_swap_search_top_n", out var vTop) && int.TryParse(vTop, out var iTop) ? iTop : 3;
            moveCooldownRounds       = kv.TryGetValue("teambalancer_move_cooldown_rounds", out var vCd) && int.TryParse(vCd, out var iCd) ? iCd : 4;
            liveWarmupRounds         = kv.TryGetValue("teambalancer_live_warmup_rounds", out var vWarm) && int.TryParse(vWarm, out var iWarm) ? iWarm : 2;
            maxSwapsPerIntermission  = kv.TryGetValue("teambalancer_max_swaps_per_intermission", out var vSw) && int.TryParse(vSw, out var iSw) ? Math.Max(0, iSw) : 2;
            maxMovesPerIntermission  = kv.TryGetValue("teambalancer_max_moves_per_intermission", out var vMv) && int.TryParse(vMv, out var iMv) ? Math.Max(0, iMv) : 3;

            // Live stats provider
            gameStats = GameStats.Current;
            if (gameStats == null) {
                liveEnabled = false;
                Console.WriteLine($"[WARN] OSBase[{ModuleName}] - Init: GameStats.Current is null. Live blending disabled.");
            }

            LoadMapConfig(mapConfigPath);

            // Warmup announce: reset state
            osbase.RegisterEventHandler<EventRoundAnnounceWarmup>((ev, info) => {
                currentRound = 0;
                ctScore = 0; tScore = 0;
                lastMoveRound.Clear();
                liveEnabled = (gameStats != null);
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Warmup: LiveEnabled={liveEnabled}, map={Server.MapName}, intermissionDelay={intermissionBalanceDelay}s.");
                return HookResult.Continue;
            });

            // Warmup end: odd-side enforcement immediate, then one pre-balance pass
            osbase.RegisterEventHandler<EventWarmupEnd>((ev, info) => {
                try {
                    Console.WriteLine($"[INFO] OSBase[{ModuleName}] - WarmupEnd: enforcing odd-player policy.");
                    EnforceOddPolicyForCurrentMap();
                    MaybeBalanceNow(true);
                } catch (Exception ex) {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - WarmupEndBalance: {ex.Message}");
                }
                return HookResult.Continue;
            });

            // Halftime: enforce odd-side immediately (before next pistol)
            osbase.RegisterEventHandler<EventStartHalftime>((ev, info) => {
                try {
                    Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Halftime: enforcing odd-player policy.");
                    EnforceOddPolicyForCurrentMap();
                } catch (Exception ex) {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - HalftimeOddEnforce: {ex.Message}");
                }
                return HookResult.Continue;
            });

            osbase.RegisterEventHandler<EventRoundStart>((ev, info) => {
                currentRound++;
                return HookResult.Continue;
            });

            // Intermission balancing: schedule from RoundEnd, complete before next spawn
            osbase.RegisterEventHandler<EventRoundEnd>((ev, info) => {
                try {
                    if (ev.Winner == (int)CsTeam.CounterTerrorist) ctScore++;
                    else if (ev.Winner == (int)CsTeam.Terrorist) tScore++;

                    var delay = intermissionBalanceDelay;
                    osbase.AddTimer(delay, () => {
                        try {
                            EnforceOddPolicyForCurrentMap();   // count fix first
                            MaybeBalanceNow(false);            // then skill
                        } catch (Exception ex) {
                            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - IntermissionBalance: {ex.Message}");
                        }
                    });
                } catch (Exception ex) {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - RoundEnd: {ex.Message}");
                }
                return HookResult.Continue;
            });

            Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Init: Loaded.");
        }

        public void Unload ( bool hotReload ) {
            Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Unload: hotReload={hotReload}");
        }

        // ===== Auto-create configs =====
        private void EnsureLocalConfigExists ( string path ) {
            try {
                if (File.Exists(path)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllText(path,
@"# TeamBalancer settings
teambalancer_map_config configs/teambalancer_maps.cfg
teambalancer_intermission_delay 0.8
teambalancer_suppress_near_halftime 1
teambalancer_suppress_last_n_rounds 3
teambalancer_swap_search_top_n 3
teambalancer_move_cooldown_rounds 4
teambalancer_live_warmup_rounds 2
teambalancer_max_swaps_per_intermission 2
teambalancer_max_moves_per_intermission 3
");
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Created default {path}.");
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - EnsureLocalConfigExists: {ex.Message}");
            }
        }

        private void EnsureMapConfigExists ( string path ) {
            try {
                if (File.Exists(path)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllText(path,
@"# mapname,sites,prefer
de_inferno,2,CT
de_dust2,2,CT
de_mirage,2,CT
de_nuke,2,CT
de_ancient,2,CT
de_vertigo,2,CT
de_overpass,2,CT
de_anubis,2,CT
de_lake,1,T
cs_office,1,T
");
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Created default {path}.");
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - EnsureMapConfigExists: {ex.Message}");
            }
        }

        // ===== Local config loader (key value) =====
        private Dictionary<string, string> LoadLocalConfig ( string path ) {
            var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try {
                if (!File.Exists(path)) return res;
                foreach (var raw in File.ReadAllLines(path)) {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                    int i = line.IndexOf(' ');
                    if (i <= 0) continue;
                    var key = line.Substring(0, i).Trim();
                    var val = line.Substring(i + 1).Trim();
                    if (key.Length > 0) res[key] = val;
                }
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - LoadLocalConfig: {ex.Message}");
            }
            return res;
        }

        // ===== Map config =====
        private void LoadMapConfig ( string path ) {
            mapSites.Clear();
            preferSideByMap.Clear();

            try {
                if (!File.Exists(path)) {
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}] - MapConfig: '{path}' not found. Using defaults.");
                    return;
                }

                var lines = File.ReadAllLines(path);
                foreach (var raw in lines) {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    // CSV: mapname,sites,prefer
                    var parts = line.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) {
                        var map = parts[0].Trim();
                        if (int.TryParse(parts[1].Trim(), out int sites) && sites >= 1) {
                            mapSites[map] = sites;
                        }
                        if (parts.Length >= 3) {
                            var sideStr = parts[2].Trim().ToUpperInvariant();
                            preferSideByMap[map] = (sideStr == "T") ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
                        }
                    }
                }

                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - MapConfig: Loaded {mapSites.Count} entries from '{path}'.");
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - MapConfig: {ex.Message}");
            }
        }

        private int GetSitesForMap ( string mapName ) {
            return mapSites.TryGetValue(mapName, out var n) ? n : 2; // default: 2 bomb sites
        }

        private CsTeam GetPreferredSideForMap ( string mapName ) {
            if (preferSideByMap.TryGetValue(mapName, out var side)) return side;
            // Heuristic fallback: 1-site/hostage -> T+1, multi-site bomb -> CT+1
            return GetSitesForMap(mapName) <= 1 ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
        }

        // ===== Odd-player enforcement (loop until fixed) =====
        private void EnforceOddPolicyForCurrentMap ( ) {
            var preferSideOnOdd = GetPreferredSideForMap(Server.MapName);
            int moves = 0;

            for (int guard = 0; guard < 16; guard++) {
                var players = Utilities.GetPlayers().Where(IsHumanOnTeam).ToList();
                var ct = players.Where(p => p.TeamNum == (byte)CsTeam.CounterTerrorist).ToList();
                var tt = players.Where(p => p.TeamNum == (byte)CsTeam.Terrorist).ToList();

                int nCT = ct.Count, nT = tt.Count, total = nCT + nT;
                if (total == 0) return;

                // current strength diff using EffectiveMu
                double ctStr = TeamStrengthMu(ct);
                double ttStr = TeamStrengthMu(tt);
                double diffMu = ctStr - ttStr; // >0 ⇒ CT stronger

                // Need a COUNT move?
                bool needMove;
                if ((total % 2) == 0) {
                    needMove = (nCT != nT);
                } else {
                    var currentOdd = nCT > nT ? CsTeam.CounterTerrorist : (nT > nCT ? CsTeam.Terrorist : CsTeam.Spectator);
                    needMove = (currentOdd == CsTeam.Spectator) || (currentOdd != preferSideOnOdd);
                }
                if (!needMove) break;

                if (moves >= maxMovesPerIntermission) {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - OddPolicy: hit move cap ({maxMovesPerIntermission}).");
                    break;
                }

                // Decide fromTeam → toTeam
                CsTeam fromTeam, toTeam;
                if ((total % 2) == 0) {
                    // even total: pull from the larger team
                    if (nCT > nT) { fromTeam = CsTeam.CounterTerrorist; toTeam = CsTeam.Terrorist; }
                    else          { fromTeam = CsTeam.Terrorist;        toTeam = CsTeam.CounterTerrorist; }
                } else {
                    // odd total: enforce +1 on preferred side
                    if (preferSideOnOdd == CsTeam.CounterTerrorist) { fromTeam = CsTeam.Terrorist;        toTeam = CsTeam.CounterTerrorist; }
                    else                                            { fromTeam = CsTeam.CounterTerrorist; toTeam = CsTeam.Terrorist; }
                }

                var surplus = (fromTeam == CsTeam.CounterTerrorist) ? ct : tt;

                // Optimal candidate to minimize |diff_after|
                // If moving CT→T: diff_after = diffMu - 2*mu  ⇒ target mu ≈ diffMu/2
                // If moving T→CT: diff_after = diffMu + 2*mu  ⇒ target mu ≈ -diffMu/2
                double muTarget = (fromTeam == CsTeam.CounterTerrorist) ? (diffMu / 2.0) : (-diffMu / 2.0);

                CCSPlayerController? pick = surplus
                    .Where(p => CanMoveNow(p.SteamID))
                    .OrderBy(p => Math.Abs(EffectiveMu(p.SteamID) - muTarget))
                    .FirstOrDefault();

                if (pick == null) {
                    // cooldown bypass fallback to guarantee headcount fix
                    pick = surplus
                        .OrderBy(p => Math.Abs(EffectiveMu(p.SteamID) - muTarget))
                        .FirstOrDefault();
                }
                if (pick == null) {
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}] - OddPolicy: no candidate to move {fromTeam}→{toTeam}.");
                    break;
                }

                pick.SwitchTeam(toTeam);
                try { if (pick.UserId.HasValue) gameStats?.movePlayer(pick.UserId.Value, (int)toTeam); } catch { }
                lastMoveRound[pick.SteamID] = currentRound;
                moves++;

                PrintToAll($"{pick.PlayerName} moved to {toTeam}");
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - OddPolicy: moved {pick.PlayerName} {fromTeam}→{toTeam} (mu={EffectiveMu(pick.SteamID):F0}, target≈{muTarget:F0}, move #{moves}).");
            }
        }

        // ===== Main balancing (multi-swap, then single moves as needed) =====
        private void MaybeBalanceNow ( bool force ) {
            int swaps = 0;
            int singles = 0;

            for (int attempts = 0; attempts < 8; attempts++) {
                var players = Utilities.GetPlayers().Where(IsHumanOnTeam).ToList();
                if (players.Count < 2) return;

                var ct = players.Where(p => p.TeamNum == (byte)CsTeam.CounterTerrorist).ToList();
                var tt = players.Where(p => p.TeamNum == (byte)CsTeam.Terrorist).ToList();
                if (ct.Count == 0 || tt.Count == 0) return;

                // Suppress windows for skill balancing (odd-policy handled elsewhere)
                if (!force) {
                    if (suppressNearHalftime && currentRound >= 14 && currentRound <= 16) {
                        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Suppress: near halftime (round {currentRound}).");
                        return;
                    }
                    if (suppressLastNRounds > 0 && currentRound >= 30 - suppressLastNRounds) {
                        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Suppress: late game (round {currentRound}).");
                        return;
                    }
                }

                // Objective
                double ctStrMu = TeamStrengthMu(ct);
                double ttStrMu = TeamStrengthMu(tt);
                double diffMu = ctStrMu - ttStrMu; // >0 ⇒ CT stronger

                // Control metric and threshold
                double ctLive = TeamStrengthLive(ct);
                double ttLive = TeamStrengthLive(tt);
                double controlDiff = Math.Abs(ctLive - ttLive);
                double threshold = GetDynamicThreshold();

                if (!force && controlDiff < threshold) return;

                bool ctIsStronger = diffMu > 0;
                var stronger = ctIsStronger ? ct : tt;
                var weaker = ctIsStronger ? tt : ct;
                var toTeam = ctIsStronger ? CsTeam.Terrorist : CsTeam.CounterTerrorist;

                // Try SWAPS if allowed
                if (swaps < maxSwapsPerIntermission) {
                    var topStrong = stronger
                        .OrderByDescending(p => EffectiveMu(p.SteamID))
                        .Take(Math.Min(swapSearchTopN, stronger.Count))
                        .ToList();

                    var topWeak = weaker
                        .OrderByDescending(p => EffectiveMu(p.SteamID))
                        .Take(Math.Min(swapSearchTopN, weaker.Count))
                        .ToList();

                    (CCSPlayerController a, CCSPlayerController b)? bestSwap = null;
                    double bestAfterAbs = Math.Abs(diffMu);

                    foreach (var s in topStrong) {
                        foreach (var w in topWeak) {
                            if (!CanMoveNow(s.SteamID) || !CanMoveNow(w.SteamID)) continue;

                            double muS = EffectiveMu(s.SteamID);
                            double muW = EffectiveMu(w.SteamID);

                            int sign = ((CsTeam)s.TeamNum == CsTeam.CounterTerrorist) ? +1 : -1;
                            double after = Math.Abs(diffMu - 2.0 * sign * (muS - muW));

                            if (after + 1e-6 < bestAfterAbs) {
                                bestAfterAbs = after;
                                bestSwap = (s, w);
                            }
                        }
                    }

                    if (bestSwap.HasValue) {
                        var s = bestSwap.Value.a;
                        var w = bestSwap.Value.b;

                        if (IsHumanOnTeam(s) && IsHumanOnTeam(w)) {
                            var sTeam = (CsTeam)s.TeamNum;
                            var wTeam = (CsTeam)w.TeamNum;

                            s.SwitchTeam(wTeam);
                            w.SwitchTeam(sTeam);

                            lastMoveRound[s.SteamID] = currentRound;
                            lastMoveRound[w.SteamID] = currentRound;

                            PrintToAll($"{s.PlayerName}[{TeamTag(sTeam)}] <-> {w.PlayerName}[{TeamTag(wTeam)}]");
                            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Swap: {s.PlayerName}({EffectiveMu(s.SteamID):F0}) ↔ {w.PlayerName}({EffectiveMu(w.SteamID):F0})");
                            try { gameStats?.movePlayer(s.UserId!.Value, (int)wTeam); gameStats?.movePlayer(w.UserId!.Value, (int)sTeam); } catch { }

                            swaps++;
                            // re-loop to re-evaluate diff/threshold
                            continue;
                        } else {
                            Console.WriteLine($"[WARN] OSBase[{ModuleName}] - Swap: candidates invalid during intermission.");
                        }
                    }
                }

                // If swap didn't happen or cap reached, try SINGLE move if allowed
                if (singles < maxMovesPerIntermission) {
                    var single = stronger
                        .Where(p => CanMoveNow(p.SteamID))
                        .OrderBy(p => EffectiveMu(p.SteamID)) // least damaging first
                        .FirstOrDefault();

                    if (single != null) {
                        single.SwitchTeam(toTeam);
                        lastMoveRound[single.SteamID] = currentRound;

                        PrintToAll($"{single.PlayerName} moved to {toTeam}");
                        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Single: {single.PlayerName} → {toTeam} (diffMu={diffMu:F1})");
                        try { gameStats?.movePlayer(single.UserId!.Value, (int)toTeam); } catch { }

                        singles++;
                        // re-loop to re-evaluate
                        continue;
                    }
                }

                // Nothing else to do this intermission
                return;
            }
        }

        // ===== Strength & skills =====
        private double EffectiveMu ( ulong steamId ) {
            // Live-driven; no DB baselines here
            double liveSkill = 0;
            int rounds = 0;
            bool hasLive = (gameStats != null) && gameStats.TryGetLiveSkillBySteam(steamId, out liveSkill, out rounds);

            if (!hasLive || rounds < liveWarmupRounds) return 1000.0;

            var (mean, std, _) = gameStats!.GetLiveSkillMomentsActive();
            double z = (liveSkill - mean) / Math.Max(std, 1e-6);

            if (rounds < Math.Max(liveWarmupRounds + 1, 4)) {
                double deltaU = Math.Clamp(z * unknownZScale, -liveDeltaCapUnknown, liveDeltaCapUnknown);
                return 1000.0 + deltaU;
            } else {
                if (!liveEnabled) return 1000.0;
                double deltaK = Math.Clamp(z * knownZScale, -liveDeltaCapKnown, liveDeltaCapKnown);
                return 1000.0 + (liveWeightKnown * deltaK);
            }
        }

        private double TeamStrengthMu ( IEnumerable<CCSPlayerController> team ) {
            return team.Sum(p => EffectiveMu(p.SteamID));
        }

        private double TeamStrengthLive ( IEnumerable<CCSPlayerController> team ) {
            if (gameStats == null) return 0.0;
            double sum = 0.0;
            foreach (var p in team) {
                double s; int r;
                if (gameStats.TryGetLiveSkillBySteam(p.SteamID, out s, out r)) sum += s;
            }
            return sum;
        }

        // ===== Round-based dynamic threshold =====
        private double GetDynamicThreshold ( ) {
            int round = gameStats?.roundNumber ?? currentRound;
            switch (round) {
                case 1:   return 10000f;
                case 2:   return 10000f;
                case 3:   return 8000f;
                case 4:   return 7000f;
                case 5:   return 1000f;
                case 6:   return 2000f;
                case 7:   return 5000f;
                case 8:   return 4000f;
                case 9:   return 5000f;
                case 10:  return 5000f;
                case 11:  return 4000f;
                case 12:  return 3000f;
                case 13:  return 2000f;
                case 14:  return 3000f;
                case 15:  return 5000f;
                case 16:  return 5000f;
                case 17:  return 6000f;
                case 18:  return 7000f;
                case 19:  return 8000f;
                case 20:  return 9000f;
                default:
                    return 8000f + Math.Max(0, round - 20) * 200f;
            }
        }

        // ===== Helpers =====
        private bool CanMoveNow ( ulong steamId ) {
            if (lastMoveRound.TryGetValue(steamId, out var r) && currentRound - r < moveCooldownRounds)
                return false;

            // Unknowns must build some rounds before we dare move them
            if (gameStats != null) {
                double s; int rounds;
                if (!gameStats.TryGetLiveSkillBySteam(steamId, out s, out rounds))
                    return false;
                if (rounds < minRoundsForUnknownMove)
                    return false;
            }

            return true;
        }

        private static bool IsHumanOnTeam ( CCSPlayerController p ) {
            return p != null && p.IsValid && !p.IsHLTV && !p.IsBot &&
                   (p.TeamNum == (byte)CsTeam.CounterTerrorist || p.TeamNum == (byte)CsTeam.Terrorist);
        }

        private static string TeamTag ( CsTeam t ) {
            return t == CsTeam.CounterTerrorist ? "CT" : "T";
        }

        private void PrintToAll ( string msg ) {
            foreach (var p in Utilities.GetPlayers().Where(x => x != null && x.IsValid && !x.IsHLTV)) {
                try { p.PrintToChat(msg); } catch { }
            }
        }
    }
}