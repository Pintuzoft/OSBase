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
        private GameStats? gameStats;

        private const int TEAM_T  = (int)CsTeam.Terrorist;        // 2
        private const int TEAM_CT = (int)CsTeam.CounterTerrorist; // 3

        private readonly Dictionary<string, int> mapBombsites = new();
        private const string mapConfigFile = "teambalancer_mapinfo.cfg";
        private int bombsites = 2;

        private const float postRoundDelay = 6.5f;
        private bool halftimePending = false;

        private const int HALFTIME_ROUND = 10;

        // Early-swap policy (with solid cache)
        private const float MIN_CACHE_COVERAGE   = 0.80f; // 80%

        // Signal blending / outlier handling
        private const float MID_LIVE_WEIGHT   = 0.25f;  // r3–6
        private const float LATE_LIVE_WEIGHT  = 0.40f;  // r7+
        private const float OUTLIER_PCT       = 0.50f;  // ±50% vs 90d
        private const float OUTLIER_ABS       = 6000f;  // or ±6k

        // Composition scoring (how much to punish bad “mix”)
        // Each strong/weak count away from target costs ~1200 “skill” units
        private const float COMPOSITION_UNIT  = 1200f;
        private const float COMPOSITION_WEIGHT = 1.0f;

        private enum BalancePhase { WarmupEnd, HalftimeStart, RoundEnd }

        public void Load(OSBase inOsbase, Config inConfig) {
            osbase   = inOsbase;
            config   = inConfig;
            gameStats = osbase?.GetGameStats();

            if (osbase == null || config == null) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] osbase/config is null.");
                return;
            }

            config.RegisterGlobalConfigValue($"{ModuleName}", "1");
            var enabled = config.GetGlobalConfigValue($"{ModuleName}", "0") == "1";
            if (!enabled) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled via config.");
                return;
            }

            RegisterHandlers();
            LoadMapInfo();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded.");
        }

        private void RegisterHandlers() {
            osbase!.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
            osbase!.RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
            osbase!.RegisterEventHandler<EventRoundStart>(OnRoundStartPre, HookMode.Pre);
            osbase!.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        }

        private void LoadMapInfo() {
            config?.CreateCustomConfig($"{mapConfigFile}", "// Map info\nde_dust2 2\n");
            var lines = config?.FetchCustomConfig($"{mapConfigFile}") ?? new List<string>();
            foreach (var raw in lines) {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//")) continue;
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[1], out int bs))
                    mapBombsites[parts[0]] = bs;
            }
        }

        private void OnMapStart(string mapName) {
            halftimePending = false;
            if (mapBombsites.TryGetValue(mapName, out var bs)) {
                bombsites = bs;
            } else {
                bombsites = mapName.StartsWith("cs_", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                Console.WriteLine($"[WARN] [{ModuleName}] No bombsite entry for {mapName}; defaulting bombsites={bombsites}. Add to {mapConfigFile} to override.");
            }
            Console.WriteLine($"[INFO] [{ModuleName}] Map {mapName}: bombsites={bombsites} → odd→{(bombsites==1?"T":"CT")}");
        }

        [GameEventHandler(HookMode.Post)]
        private HookResult OnRoundEnd(EventRoundEnd ev, GameEventInfo info) {
            try {
                int round = gameStats?.roundNumber ?? 0;
                if (round == HALFTIME_ROUND) {
                    halftimePending = true; // after round 10 ends
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Halftime pending after round {round}");
                }
            } catch { /* ignore */ }

            osbase?.AddTimer(postRoundDelay, () => BalanceTeams(BalancePhase.RoundEnd));
            return HookResult.Continue;
        }

        [GameEventHandler(HookMode.Pre)]
        private HookResult OnRoundStartPre(EventRoundStart ev, GameEventInfo info) {
            if (halftimePending) {
                BalanceTeams(BalancePhase.HalftimeStart); // before first round of second half
                halftimePending = false;
            }
            return HookResult.Continue;
        }

        [GameEventHandler(HookMode.Pre)]
        private HookResult OnWarmupEnd(EventWarmupEnd ev, GameEventInfo info) {
            BalanceTeams(BalancePhase.WarmupEnd); // fix sizes only
            return HookResult.Continue;
        }

        private (int idealT, int idealCT) ComputeIdealSizes(int tCount, int ctCount) {
            int total = tCount + ctCount;
            if (bombsites == 2) {
                // 2 sites → CT gets odd
                return (total / 2, total / 2 + total % 2);
            } else {
                // 1 site → T gets odd
                return (total / 2 + total % 2, total / 2);
            }
        }

        // === Thresholds ===

        private static float Clamp(float v, float lo, float hi) => MathF.Max(lo, MathF.Min(hi, v));

        // Variance & cache-aware threshold
        private float ComputeThreshold(GameStats gs, TeamStats tStats, TeamStats ctStats) {
            int round = gs.roundNumber;
            bool cacheOk = HasSufficientCache(gs, tStats, ctStats, out _, out _);

            var (_, std, count) = gs.GetLiveSkillMomentsActive();
            float sigma = (float)(float.IsFinite((float)std) && count >= 4 ? std : 4000f);

            float k, minCap;
            if (cacheOk) {
                if (round <= 2) { k = 0.35f; minCap = 1200f; }
                else if (round <= 6) { k = 0.45f; minCap = 1500f; }
                else { k = 0.55f; minCap = 1800f; }
            } else {
                if (round <= 2) { k = 1.50f; minCap = 4000f; }
                else if (round <= 6) { k = 0.90f; minCap = 3000f; }
                else { k = 0.70f; minCap = 2500f; }
            }
            float thr = k * sigma;
            return Clamp(thr, minCap, 9000f);
        }

        private bool HasSufficientCache(GameStats gs, TeamStats tStats, TeamStats ctStats, out int cached, out int total) {
            cached = 0; total = 0;
            foreach (var kv in tStats.playerList) { total++; if (gs.GetCached90dByUserId(kv.Key) > 0f) cached++; }
            foreach (var kv in ctStats.playerList) { total++; if (gs.GetCached90dByUserId(kv.Key) > 0f) cached++; }
            return total > 0 && (cached / (float)total) >= MIN_CACHE_COVERAGE;
        }

        // === Skill signal (90d with outlier-protected live blend) ===
        private float SignalSkill(GameStats gs, int userId, PlayerStats ps) {
            int round = gs.roundNumber;
            float s90  = gs.GetCached90dByUserId(userId);
            float live = ps.calcSkill();

            if (s90 > 0f) {
                bool pctOut = Math.Abs(live - s90) > OUTLIER_PCT * s90;
                bool absOut = Math.Abs(live - s90) > OUTLIER_ABS;
                if (round <= 2 || pctOut || absOut) return s90;

                float wLive = (round <= 6) ? MID_LIVE_WEIGHT : LATE_LIVE_WEIGHT;
                return s90 * (1f - wLive) + live * wLive;
            }

            // No 90d → just live (small teams; fine)
            return live;
        }

        // === Composition helpers ===

        private class CompState {
            public float sumT, sumCT;
            public int nT, nCT;
            public int strongT, strongCT;
            public int weakT, weakCT;
            public int strongTargetT, strongTargetCT;
            public int weakTargetT, weakTargetCT;
        }

        // Build global ranking and strong/weak sets (top and bottom halves by signal)
        private void BuildComposition(GameStats gs, TeamStats tStats, TeamStats ctStats,
                                      out Dictionary<int,float> sig,
                                      out HashSet<int> strongSet,
                                      out HashSet<int> weakSet,
                                      out int strongK, out int weakK) {
            sig = new Dictionary<int,float>();
            var all = new List<(int uid, float s)>();

            foreach (var kv in tStats.playerList) {
                float s = SignalSkill(gs, kv.Key, kv.Value);
                sig[kv.Key] = s; all.Add((kv.Key, s));
            }
            foreach (var kv in ctStats.playerList) {
                float s = SignalSkill(gs, kv.Key, kv.Value);
                sig[kv.Key] = s; all.Add((kv.Key, s));
            }

            all.Sort((a,b) => b.s.CompareTo(a.s)); // desc
            int total = all.Count;
            strongK = Math.Max(1, (int)Math.Round(total * 0.5)); // top half “strong”
            weakK   = Math.Max(1, (int)Math.Round(total * 0.5)); // bottom half “weak”

            strongSet = new HashSet<int>(all.Take(strongK).Select(x => x.uid));
            weakSet   = new HashSet<int>(all.Skip(Math.Max(0,total - weakK)).Select(x => x.uid));
        }

        private CompState MakeCompState(GameStats gs, TeamStats tStats, TeamStats ctStats,
                                        Dictionary<int,float> sig,
                                        HashSet<int> strongSet, HashSet<int> weakSet,
                                        int strongK, int weakK) {
            var cs = new CompState();
            cs.nT = tStats.playerList.Count; cs.nCT = ctStats.playerList.Count;

            foreach (var kv in tStats.playerList) {
                cs.sumT += sig[kv.Key];
                if (strongSet.Contains(kv.Key)) cs.strongT++;
                if (weakSet.Contains(kv.Key))   cs.weakT++;
            }
            foreach (var kv in ctStats.playerList) {
                cs.sumCT += sig[kv.Key];
                if (strongSet.Contains(kv.Key)) cs.strongCT++;
                if (weakSet.Contains(kv.Key))   cs.weakCT++;
            }

            int total = cs.nT + cs.nCT;
            // Distribute targets proportional to team sizes
            cs.strongTargetT  = (int)Math.Round(strongK * (cs.nT / (double)total));
            cs.strongTargetCT = strongK - cs.strongTargetT;
            cs.weakTargetT    = (int)Math.Round(weakK   * (cs.nT / (double)total));
            cs.weakTargetCT   = weakK - cs.weakTargetT;

            return cs;
        }

        private float CompositionPenalty(CompState cs) {
            int strongMiss = Math.Abs(cs.strongT - cs.strongTargetT) + Math.Abs(cs.strongCT - cs.strongTargetCT);
            int weakMiss   = Math.Abs(cs.weakT   - cs.weakTargetT)   + Math.Abs(cs.weakCT   - cs.weakTargetCT);
            return COMPOSITION_WEIGHT * (strongMiss + weakMiss) * COMPOSITION_UNIT;
        }

        private float ScoreState(CompState cs) {
            float avgT  = cs.nT  > 0 ? cs.sumT  / cs.nT  : 0f;
            float avgCT = cs.nCT > 0 ? cs.sumCT / cs.nCT : 0f;
            float meanGap = MathF.Abs(avgT - avgCT);
            return meanGap + CompositionPenalty(cs);
        }

        // === Main balance ===

        private void BalanceTeams(BalancePhase phase) {
            var gs = osbase?.GetGameStats();
            if (gs == null) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] BalanceTeams: GameStats is null.");
                return;
            }

            var tStats = gs.getTeam(TEAM_T);
            var ctStats = gs.getTeam(TEAM_CT);

            int tCount = tStats.numPlayers();
            int ctCount = ctStats.numPlayers();
            var (idealT, idealCT) = ComputeIdealSizes(tCount, ctCount);
            bool sizeOk = (tCount == idealT && ctCount == idealCT);

            float tAvg90 = gs.TeamAverage90d(TEAM_T);
            float ctAvg90 = gs.TeamAverage90d(TEAM_CT);

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Phase={phase} T:{tCount}({tAvg90:F0}) CT:{ctCount}({ctAvg90:F0}) IdealT:{idealT} IdealCT:{idealCT}");

            if (!sizeOk) {
                evenTeamSizes(gs, tStats, ctStats, moveFromT: tCount > idealT, moves: Math.Abs(tCount - idealT));
            }

            if (phase == BalancePhase.WarmupEnd) return; // never swap at warmup end

            int round = gs.roundNumber;
            float diff = Math.Abs(tAvg90 - ctAvg90);
            float thr  = ComputeThreshold(gs, tStats, ctStats);

            if (diff > thr) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Swap r{round}: diff={diff:F0} thr={thr:F0}");
                doSkillBalance(gs, tStats, ctStats);
            }
        }

        // Size fix: simulate every candidate and pick the move minimizing (mean gap + composition penalty)
        private void evenTeamSizes(GameStats gs, TeamStats tStats, TeamStats ctStats, bool moveFromT, int moves) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] evenTeamSizes: moving {moves} → {(moveFromT ? "CT" : "T")}");

            BuildComposition(gs, tStats, ctStats, out var sig, out var strongSet, out var weakSet, out int strongK, out int weakK);
            CompState cs = MakeCompState(gs, tStats, ctStats, sig, strongSet, weakSet, strongK, weakK);

            for (int m = 0; m < moves; m++) {
                var src = moveFromT ? tStats : ctStats;
                var dst = moveFromT ? ctStats : tStats;

                int bestUid = -1;
                float bestScore = float.MaxValue;
                float chosenSig = 0f;

                foreach (var kv in src.playerList) {
                    int uid = kv.Key;
                    var ps  = kv.Value;
                    if (ps.immune > 0) continue;

                    float s = sig[uid];
                    var tryCs = new CompState {
                        sumT = cs.sumT, sumCT = cs.sumCT,
                        nT = cs.nT, nCT = cs.nCT,
                        strongT = cs.strongT, strongCT = cs.strongCT,
                        weakT = cs.weakT, weakCT = cs.weakCT,
                        strongTargetT = cs.strongTargetT, strongTargetCT = cs.strongTargetCT,
                        weakTargetT   = cs.weakTargetT,   weakTargetCT   = cs.weakTargetCT
                    };

                    bool isStrong = strongSet.Contains(uid);
                    bool isWeak   = weakSet.Contains(uid);

                    if (moveFromT) {
                        tryCs.sumT  -= s; tryCs.nT--;
                        tryCs.sumCT += s; tryCs.nCT++;
                        if (isStrong) { tryCs.strongT--; tryCs.strongCT++; }
                        if (isWeak)   { tryCs.weakT--;   tryCs.weakCT++; }
                    } else {
                        tryCs.sumCT -= s; tryCs.nCT--;
                        tryCs.sumT  += s; tryCs.nT++;
                        if (isStrong) { tryCs.strongCT--; tryCs.strongT++; }
                        if (isWeak)   { tryCs.weakCT--;   tryCs.weakT++; }
                    }

                    float score = ScoreState(tryCs);

                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] pick? uid={uid} name={ps.name} sig={s:F0} score={score:F0} strong={isStrong} weak={isWeak} immune={ps.immune}");

                    if (score < bestScore) { bestScore = score; bestUid = uid; chosenSig = s; }
                }

                if (bestUid == -1) {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] evenTeamSizes: no candidate.");
                    break;
                }

                var player = Utilities.GetPlayerFromUserid(bestUid);
                if (player == null || !player.UserId.HasValue) break;

                // Apply to live state and comp state
                bool wasStrong = strongSet.Contains(bestUid);
                bool wasWeak   = weakSet.Contains(bestUid);
                if (moveFromT) {
                    cs.sumT  -= chosenSig; cs.nT--;
                    cs.sumCT += chosenSig; cs.nCT++;
                    if (wasStrong) { cs.strongT--; cs.strongCT++; }
                    if (wasWeak)   { cs.weakT--;   cs.weakCT++; }
                } else {
                    cs.sumCT -= chosenSig; cs.nCT--;
                    cs.sumT  += chosenSig; cs.nT++;
                    if (wasStrong) { cs.strongCT--; cs.strongT++; }
                    if (wasWeak)   { cs.weakCT--;   cs.weakT++; }
                }

                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] choose uid={bestUid} sig={chosenSig:F0} score*={bestScore:F0}");
                movePlayer(gs, player, moveFromT ? TEAM_CT : TEAM_T, tStats, ctStats);
                Server.PrintToChatAll($"[TeamBalancer] Moved: {player.PlayerName} {(moveFromT ? "T→CT" : "CT→T")}");
            }
        }

        // Skill balance: try all valid pairs; pick swap minimizing (mean gap + composition penalty)
        private void doSkillBalance(GameStats gs, TeamStats tStats, TeamStats ctStats) {
            BuildComposition(gs, tStats, ctStats, out var sig, out var strongSet, out var weakSet, out int strongK, out int weakK);
            CompState baseCs = MakeCompState(gs, tStats, ctStats, sig, strongSet, weakSet, strongK, weakK);

            bool tStrong = (baseCs.nT > 0 && baseCs.nCT > 0) ? (baseCs.sumT / baseCs.nT) > (baseCs.sumCT / baseCs.nCT) : false;
            var strongTeam = tStrong ? tStats : ctStats;
            var weakTeam   = tStrong ? ctStats : tStats;

            int bestStrong = -1, bestWeak = -1;
            float bestScore = float.MaxValue;

            foreach (var kvS in strongTeam.playerList) {
                if (kvS.Value.immune > 0) continue;
                int uS = kvS.Key;
                float sS = sig[uS];
                bool sStrong = strongSet.Contains(uS);
                bool sWeak   = weakSet.Contains(uS);

                foreach (var kvW in weakTeam.playerList) {
                    if (kvW.Value.immune > 0) continue;
                    int uW = kvW.Key;
                    float sW = sig[uW];
                    bool wStrong = strongSet.Contains(uW);
                    bool wWeak   = weakSet.Contains(uW);

                    // simulate swap
                    var cs = new CompState {
                        sumT = baseCs.sumT, sumCT = baseCs.sumCT,
                        nT = baseCs.nT, nCT = baseCs.nCT,
                        strongT = baseCs.strongT, strongCT = baseCs.strongCT,
                        weakT = baseCs.weakT, weakCT = baseCs.weakCT,
                        strongTargetT = baseCs.strongTargetT, strongTargetCT = baseCs.strongTargetCT,
                        weakTargetT   = baseCs.weakTargetT,   weakTargetCT   = baseCs.weakTargetCT
                    };

                    if (tStrong) {
                        // S from T -> CT; W from CT -> T
                        cs.sumT  = cs.sumT  - sS + sW;
                        cs.sumCT = cs.sumCT - sW + sS;
                        if (sStrong) { cs.strongT--; cs.strongCT++; }
                        if (sWeak)   { cs.weakT--;   cs.weakCT++; }
                        if (wStrong) { cs.strongCT--; cs.strongT++; }
                        if (wWeak)   { cs.weakCT--;   cs.weakT++; }
                    } else {
                        // S from CT -> T; W from T -> CT
                        cs.sumCT = cs.sumCT - sS + sW;
                        cs.sumT  = cs.sumT  - sW + sS;
                        if (sStrong) { cs.strongCT--; cs.strongT++; }
                        if (sWeak)   { cs.weakCT--;   cs.weakT++; }
                        if (wStrong) { cs.strongT--; cs.strongCT++; }
                        if (wWeak)   { cs.weakT--;   cs.weakCT++; }
                    }

                    float score = ScoreState(cs);
                    if (score < bestScore) { bestScore = score; bestStrong = uS; bestWeak = uW; }
                }
            }

            if (bestStrong == -1 || bestWeak == -1) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] doSkillBalance: no valid swap.");
                return;
            }

            var strongP = Utilities.GetPlayerFromUserid(bestStrong);
            var weakP   = Utilities.GetPlayerFromUserid(bestWeak);
            if (strongP == null || weakP == null || !strongP.UserId.HasValue || !weakP.UserId.HasValue) return;

            // Perform swap, keep sizes
            movePlayer(gs, strongP, tStrong ? TEAM_CT : TEAM_T, tStats, ctStats);
            movePlayer(gs, weakP,   tStrong ? TEAM_T  : TEAM_CT, tStats, ctStats);
            Server.PrintToChatAll($"[TeamBalancer] Swapped: {strongP.PlayerName} ↔ {weakP.PlayerName}");
        }

        private void movePlayer(GameStats gs, CCSPlayerController player, int targetTeam, TeamStats tStats, TeamStats ctStats) {
            if (player == null || !player.UserId.HasValue) return;

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - T roster:");
            tStats.printPlayers();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - CT roster:");
            ctStats.printPlayers();

            player.SwitchTeam((CsTeam)targetTeam);
            gs.movePlayer(player.UserId.Value, targetTeam);

            gs.GetPlayerStats(player.UserId.Value).immune += 2;
            player.PrintToCenterAlert($"!! YOU HAVE BEEN MOVED TO {(targetTeam == TEAM_T ? "T" : "CT")} !!");
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] movePlayer: {player.PlayerName} → {(targetTeam == TEAM_T ? "T" : "CT")}");

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - T roster (post):");
            tStats.printPlayers();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - CT roster (post):");
            ctStats.printPlayers();
        }
    }
}