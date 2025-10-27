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

        // External: live stats provider
        private GameStats? gameStats;

        // ===== Config / knobs =====
        private string mapConfigPath = "configs/teambalancer_maps.cfg";

        // Run skill-balancing during intermission using a short delay after RoundEnd
        private float intermissionBalanceDelay = 0.8f; // seconds (tweak per server tickrate)

        // Suppress general skill-balancing near halftime / late (odd enforcement still runs)
        private bool suppressNearHalftime = true;   // rounds 14..16
        private int suppressLastNRounds = 3;        // last N rounds (e.g., 28..30)

        // Candidate search breadth + churn controls
        private int swapSearchTopN = 3;
        private int moveCooldownRounds = 4;

        // Live blending → EffectiveMu (small influence)
        private bool liveEnabled = true;
        private int liveWarmupRounds = 2;
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

        // Per-map metadata
        private readonly Dictionary<string, int> mapSites = new(StringComparer.OrdinalIgnoreCase);           // de_dust2 -> 2
        private readonly Dictionary<string, CsTeam> preferSideByMap = new(StringComparer.OrdinalIgnoreCase);  // de_shortdust -> T

        // ===== Module wiring =====
        public void Load ( OSBase inOsbase, Config config ) {
            osbase = inOsbase;

            // Config defaults
            config.RegisterGlobalConfigValue($"{ModuleName}_enabled", "1");
            config.RegisterGlobalConfigValue($"{ModuleName}_map_config", mapConfigPath);
            config.RegisterGlobalConfigValue($"{ModuleName}_intermission_delay", "0.8");
            config.RegisterGlobalConfigValue($"{ModuleName}_suppress_near_halftime", "1");
            config.RegisterGlobalConfigValue($"{ModuleName}_suppress_last_n_rounds", "3");
            config.RegisterGlobalConfigValue($"{ModuleName}_swap_search_top_n", "3");
            config.RegisterGlobalConfigValue($"{ModuleName}_move_cooldown_rounds", "4");
            config.RegisterGlobalConfigValue($"{ModuleName}_live_warmup_rounds", "2");

            enabled = config.GetGlobalConfigValue($"{ModuleName}_enabled", "1") == "1";
            mapConfigPath = config.GetGlobalConfigValue($"{ModuleName}_map_config", mapConfigPath);

            if (float.TryParse(config.GetGlobalConfigValue($"{ModuleName}_intermission_delay", "0.8"), out var d))
                intermissionBalanceDelay = Math.Max(0.1f, d);

            suppressNearHalftime = config.GetGlobalConfigValue($"{ModuleName}_suppress_near_halftime", "1") == "1";
            if (!int.TryParse(config.GetGlobalConfigValue($"{ModuleName}_suppress_last_n_rounds", "3"), out suppressLastNRounds)) suppressLastNRounds = 3;
            if (!int.TryParse(config.GetGlobalConfigValue($"{ModuleName}_swap_search_top_n", "3"), out swapSearchTopN)) swapSearchTopN = 3;
            if (!int.TryParse(config.GetGlobalConfigValue($"{ModuleName}_move_cooldown_rounds", "4"), out moveCooldownRounds)) moveCooldownRounds = 4;
            if (!int.TryParse(config.GetGlobalConfigValue($"{ModuleName}_live_warmup_rounds", "2"), out liveWarmupRounds)) liveWarmupRounds = 2;

            if (!enabled) {
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Init: Module disabled in global config.");
                return;
            }

            // Resolve live stats
            gameStats = GameStats.Current;
            if (gameStats == null) {
                Console.WriteLine($"[WARN] OSBase[{ModuleName}] - Init: GameStats.Current is null. Live blending disabled.");
                liveEnabled = false;
            }

            LoadMapConfig(mapConfigPath);

            osbase.RegisterEventHandler<EventRoundAnnounceWarmup>((ev, info) => {
                currentRound = 0;
                ctScore = 0; tScore = 0;
                lastMoveRound.Clear();
                liveEnabled = (gameStats != null);
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Warmup: LiveEnabled={liveEnabled}, map={Server.MapName}");
                return HookResult.Continue;
            });

            // Enforce odd-player side at warmup end (immediate), then one pre-balance pass
            osbase.RegisterEventHandler<EventWarmupEnd>((ev, info) => {
                try {
                    Console.WriteLine($"[INFO] OSBase[{ModuleName}] - WarmupEnd: enforcing odd-player policy.");
                    EnforceOddPolicyForCurrentMap();   // immediate switch BEFORE pistol spawn
                    MaybeBalanceNow(force: true);      // optional pre-balance (intermission context)
                } catch (Exception ex) {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - WarmupEndBalance: {ex.Message}");
                }
                return HookResult.Continue;
            });

            // Enforce odd-player side exactly at halftime (immediate), BEFORE post-half pistol
            osbase.RegisterEventHandler<EventStartHalftime>((ev, info) => {
                try {
                    Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Halftime: enforcing odd-player policy.");
                    EnforceOddPolicyForCurrentMap();   // immediate switch during intermission
                } catch (Exception ex) {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - HalftimeOddEnforce: {ex.Message}");
                }
                return HookResult.Continue;
            });

            osbase.RegisterEventHandler<EventRoundStart>((ev, info) => {
                currentRound++;
                return HookResult.Continue;
            });

            // For normal rounds, balance DURING intermission:
            // schedule from RoundEnd with a short delay so we complete BEFORE next spawn
            osbase.RegisterEventHandler<EventRoundEnd>((ev, info) => {
                try {
                    // keep score counters in sync (optional, useful for gating on big gaps)
                    if (ev.Winner == (int)CsTeam.CounterTerrorist) {
                        ctScore++;
                    } else if (ev.Winner == (int)CsTeam.Terrorist) {
                        tScore++;
                    }

                    var delay = intermissionBalanceDelay;
                    osbase.AddTimer(delay, () => {
                        try {
                            // Re-enforce odd policy (players may have joined/quit in intermission)
                            EnforceOddPolicyForCurrentMap();   // immediate
                            // Then (optionally) do skill balancing, unless we're in a suppression window
                            MaybeBalanceNow(force: false);     // immediate
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

        // ===== Odd-player enforcement (used at warmup end, halftime, and intermission after each round) =====
        private void EnforceOddPolicyForCurrentMap ( ) {
            var players = Utilities.GetPlayers().Where(IsHumanOnTeam).ToList();
            var ct = players.Where(p => p.TeamNum == (byte)CsTeam.CounterTerrorist).ToList();
            var tt = players.Where(p => p.TeamNum == (byte)CsTeam.Terrorist).ToList();

            int total = ct.Count + tt.Count;
            if (total == 0) return;

            var preferSideOnOdd = GetPreferredSideForMap(Server.MapName);

            if ((total % 2) == 1) {
                // which side currently has +1?
                var extraSide =
                    ct.Count > tt.Count ? CsTeam.CounterTerrorist :
                    tt.Count > ct.Count ? CsTeam.Terrorist :
                    CsTeam.Spectator;

                if (extraSide != CsTeam.Spectator && extraSide != preferSideOnOdd) {
                    // Move one from surplus to deficit: pick least damaging (lowest EffectiveMu)
                    var surplus = extraSide == CsTeam.CounterTerrorist ? ct : tt;
                    var target = extraSide == CsTeam.CounterTerrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist;

                    var candidate = surplus
                        .OrderBy(p => EffectiveMu(p.SteamID))
                        .FirstOrDefault();

                    if (candidate != null) {
                        candidate.SwitchTeam(target); // immediate during intermission
                        try { gameStats?.movePlayer(candidate.UserId!.Value, (int)target); } catch { }
                        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - OddPolicy: moved {candidate.PlayerName} → {target} to enforce {preferSideOnOdd}+1 on {Server.MapName} ({GetSitesForMap(Server.MapName)} site(s)).");
                        PrintToAll($"{candidate.PlayerName} moved to {target}");
                    }
                }
            }
        }

        // ===== Main skill balancing (swap-first, then single) =====
        private void MaybeBalanceNow ( bool force ) {
            var players = Utilities.GetPlayers().Where(IsHumanOnTeam).ToList();
            if (players.Count < 2) return;

            var ct = players.Where(p => p.TeamNum == (byte)CsTeam.CounterTerrorist).ToList();
            var tt = players.Where(p => p.TeamNum == (byte)CsTeam.Terrorist).ToList();
            if (ct.Count == 0 || tt.Count == 0) return;

            // Respect suppression windows for skill balancing (odd enforcement runs elsewhere)
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

            // Strength via EffectiveMu (objective for balancing)
            double ctStrMu = TeamStrengthMu(ct);
            double ttStrMu = TeamStrengthMu(tt);
            double diffMu = ctStrMu - ttStrMu; // >0 ⇒ CT stronger

            // Control metric: LIVE skill diff, threshold via your round table
            double ctLive = TeamStrengthLive(ct);
            double ttLive = TeamStrengthLive(tt);
            double controlDiff = Math.Abs(ctLive - ttLive);
            double threshold = GetDynamicThreshold();

            if (!force && controlDiff < threshold) {
                return;
            }

            bool ctIsStronger = diffMu > 0;
            var stronger = ctIsStronger ? ct : tt;
            var weaker = ctIsStronger ? tt : ct;
            var toTeam = ctIsStronger ? CsTeam.Terrorist : CsTeam.CounterTerrorist;

            // --- SWAP first ---
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
                        if (after <= Math.Max(20.0, 0.25 * bestAfterAbs)) goto haveSwap;
                    }
                }
            }

            haveSwap:
            if (bestSwap.HasValue) {
                var s = bestSwap.Value.a;
                var w = bestSwap.Value.b;

                // Intermission context: switch immediately
                if (IsHumanOnTeam(s) && IsHumanOnTeam(w)) {
                    var sTeam = (CsTeam)s.TeamNum;
                    var wTeam = (CsTeam)w.TeamNum;

                    s.SwitchTeam(wTeam);
                    w.SwitchTeam(sTeam);

                    lastMoveRound[s.SteamID] = currentRound;
                    lastMoveRound[w.SteamID] = currentRound;

                    PrintToAll($"{s.PlayerName}[{TeamTag(sTeam)}] <-> {w.PlayerName}[{TeamTag(wTeam)}]");
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Swap: {s.PlayerName}({EffectiveMu(s.SteamID):F0}) ↔ {w.PlayerName}({EffectiveMu(w.SteamID):F0}) | after |diffMu|={bestAfterAbs:F1}");
                    try { gameStats?.movePlayer(s.UserId!.Value, (int)wTeam); gameStats?.movePlayer(w.UserId!.Value, (int)sTeam); } catch { }
                } else {
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}] - Swap: candidates invalid during intermission.");
                }
                return;
            }

            // --- Single move fallback ---
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
            }
        }

        // ===== EffectiveMu & team sums =====
        private double EffectiveMu ( ulong steamId ) {
            // Live-driven (no 90d baseline in this version)
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

        // ===== Your round-based dynamic threshold =====
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

            // unknowns must have enough live rounds before we dare move them
            if (gameStats != null) {
                double s; int rounds;
                if (!gameStats.TryGetLiveSkillBySteam(steamId, out s, out rounds))
                    return false; // no live yet -> don’t move
                if (rounds < minRoundsForUnknownMove)
                    return false; // too volatile -> wait
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