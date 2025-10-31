using System;
using System.Linq;
using System.Collections.Generic;

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Config;
using CounterStrikeSharp.API.Core.Attributes.Registration;

namespace OSBase.Modules {

    public class TeamBalancer : IModule {
        public string ModuleName     => "teambalancer";
        public string ModuleNameNice => "TeamBalancer";

        private OSBase? osbase;
        private Config? config;
        private GameStats? gameStats;

        // Teams
        private const int TEAM_S  = (int)CsTeam.Spectator;
        private const int TEAM_T  = (int)CsTeam.Terrorist;
        private const int TEAM_CT = (int)CsTeam.CounterTerrorist;

        // Map bombsite info
        private readonly Dictionary<string, int> mapBombsites = new();
        private const string mapConfigFile = "teambalancer_mapinfo.cfg";
        private int bombsites = 2; // default
        private string currentMap = "";

        // Warmup policy
        private const float WARMUP_TARGET_DEVIATION = 1500f;
        private const int   WARMUP_MAX_SWAPS        = 2;

        // Warmup "burst" scheduling (single burst just before warmup ends)
        private const float WARMUP_BURST_AT       = 40f; // seconds after map start (warmup ~45s)
        private const int   WARMUP_BURST_PASSES   = 3;   // immediate passes in same tick
        private const int   WARMUP_SWAPS_PER_PASS = 1;   // max swaps attempted per pass

        // Game structure (you run 10 + 10)
        private const int HALF_ROUNDS = 10;
        private const int MAX_ROUNDS  = 20;

        // Live blending / outliers & clamps
        private const float OUTLIER_PCT     = 0.50f; // ±50% vs 90d → ignore live
        private const float OUTLIER_ABS     = 4000f; // ±4000  vs 90d → ignore live
        private const float LATE_ABS_CLAMP  = 2500f; // clamp live around 90d late
        private const float LATE_PCT_CLAMP  = 0.35f; // ±35% of 90d late

        // Swap thresholds (mid/late)
        private const float MID_SWAP_THRESHOLD  = 1500f;
        private const float LATE_SWAP_THRESHOLD = 1900f;
        private const float LATE_HYSTERESIS     = 700f;
        private const float MIN_PROJECTED_GAIN  = 800f;

        // Anti-churn
        private const int MIN_ROUNDS_BETWEEN_SWAPS = 3;
        private const int NO_SWAP_LAST_N_ROUNDS    = 3;
        private const int MAX_LATE_SWAPS           = 1;  // per half
        private const int MAX_SWAPS_PER_MAP        = 3;  // whole map
        private const float EMERGENCY_GAP          = 3500f;

        private int lastSwapRound = -999;
        private int lateSwapsThisHalf = 0;
        private int swapsThisMap = 0;
        private int currentHalfIndex = 0; // 0 first half, 1 second half

        // Per-player cooldown (avoid moving same player twice quickly)
        private readonly Dictionary<int,int> playerSwapRound = new();

        // Provisional (unknown) skill during warmup
        private const int PROV_MIN = 5000;
        private const int PROV_MAX = 7000;

        public void Load(OSBase inOsbase, Config inConfig) {
            osbase  = inOsbase;
            config  = inConfig;
            gameStats = osbase.GetGameStats();

            if (osbase == null || config == null) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] load failed (null deps).");
                return;
            }

            config.RegisterGlobalConfigValue($"{ModuleName}", "1");
            var enabled = config.GetGlobalConfigValue($"{ModuleName}", "0");
            if (enabled != "1") {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in config.");
                return;
            }

            try {
                osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);
                osbase.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

                osbase.RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd, HookMode.Post);
                osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd, HookMode.Post);
                osbase.RegisterEventHandler<EventStartHalftime>(OnStartHalftime, HookMode.Post);

                LoadMapInfo();
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded.");
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] registering: {ex.Message}");
            }
        }

        private void OnMapStart(string mapName) {
            currentMap = mapName;
            swapsThisMap = 0;
            lateSwapsThisHalf = 0;
            currentHalfIndex = 0;

            if (mapBombsites.TryGetValue(mapName, out int bs)) {
                bombsites = bs;
                Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Map {mapName} bombsites={bombsites}");
            } else {
                bombsites = mapName.StartsWith("cs_", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                config?.AddCustomConfigLine(mapConfigFile, $"{mapName} {bombsites}");
                Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Map {mapName} defaulted bombsites={bombsites}");
            }

            // Schedule one warmup burst close to warmup end
            osbase?.AddTimer(WARMUP_BURST_AT, WarmupBalanceBurst);
        }

        private void OnMapEnd() {
            // counters reset on next start
        }

        private void LoadMapInfo() {
            config?.CreateCustomConfig($"{mapConfigFile}", "// Map info\nde_dust2 2\n");
            var lines = config?.FetchCustomConfig($"{mapConfigFile}") ?? new List<string>();
            foreach (var raw in lines) {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("//")) continue;
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[1], out int bs)) {
                    mapBombsites[parts[0]] = bs;
                }
            }
        }

        // ===== Event handlers =====

        [GameEventHandler(HookMode.Post)]
        private HookResult OnWarmupEnd(EventWarmupEnd ev, GameEventInfo info) {
            // Only clear immunity; do not move after warmup
            var gs = osbase?.GetGameStats();
            if (gs != null) {
                foreach (var kv in gs.getTeam(TEAM_T).playerList)  kv.Value.immune = 0;
                foreach (var kv in gs.getTeam(TEAM_CT).playerList) kv.Value.immune = 0;
                foreach (var kv in gs.getTeam(TEAM_S).playerList)  kv.Value.immune = 0;
            }
            return HookResult.Continue;
        }

        [GameEventHandler(HookMode.Post)]
        private HookResult OnStartHalftime(EventStartHalftime ev, GameEventInfo info) {
            lateSwapsThisHalf = 0;
            currentHalfIndex = 1;
            return HookResult.Continue;
        }

        [GameEventHandler(HookMode.Post)]
        private HookResult OnRoundEnd(EventRoundEnd ev, GameEventInfo info) {
            osbase?.AddTimer(6.5f, () => BalanceAtRoundEnd());
            return HookResult.Continue;
        }

        // ===== Warmup burst (before warmup ends) =====

        private void WarmupBalanceBurst() {
            var gs = osbase?.GetGameStats();
            if (gs == null) return;

            // Only run during warmup (GameStats keeps roundNumber == 0 in warmup)
            if (gs.roundNumber != 0) {
                Console.WriteLine("[DEBUG] OSBase[teambalancer] WarmupBurst skipped: not in warmup.");
                return;
            }

            var tStats = gs.getTeam(TEAM_T);
            var cStats = gs.getTeam(TEAM_CT);

            // --- Always fix team sizes (odd player) even with few players ---
            var (idealT, idealCT) = ComputeIdealSizes(tStats.numPlayers(), cStats.numPlayers());
            if (tStats.numPlayers() != idealT || cStats.numPlayers() != idealCT) {
                int moves = Math.Abs(tStats.numPlayers() - idealT);
                bool moveFromT = tStats.numPlayers() > idealT;
                EvenTeamSizesWarmup(gs, tStats, cStats, moveFromT, moves);
                tStats = gs.getTeam(TEAM_T);
                cStats = gs.getTeam(TEAM_CT);
            }

            // --- Swaps only if a sensible lobby (prevents chaos in 2v2/3v2) ---
            if (tStats.numPlayers() + cStats.numPlayers() < 6 || tStats.numPlayers() == 0 || cStats.numPlayers() == 0) {
                Console.WriteLine("[DEBUG] OSBase[teambalancer] WarmupBurst: skip swaps (too few players).");
                return;
            }

            // Run N passes back-to-back (90d-only) to pull averages within target deviation
            for (int pass = 0; pass < WARMUP_BURST_PASSES; pass++) {
                int did = TryBalanceWarmupToDeviationWithBudget(WARMUP_SWAPS_PER_PASS);
                if (did == 0) break;
                tStats = gs.getTeam(TEAM_T);
                cStats = gs.getTeam(TEAM_CT);
            }
        }

        private int TryBalanceWarmupToDeviationWithBudget(int budget) {
            var gs = osbase?.GetGameStats();
            if (gs == null || budget <= 0) return 0;

            var tStats = gs.getTeam(TEAM_T);
            var cStats = gs.getTeam(TEAM_CT);

            int swaps = 0;
            for (int i = 0; i < budget; i++) {
                float tAvg = TeamWarmupAverage90d(gs, tStats);
                float cAvg = TeamWarmupAverage90d(gs, cStats);
                float gap  = MathF.Abs(tAvg - cAvg);
                if (gap <= WARMUP_TARGET_DEVIATION) break;

                if (!FindBestSwapPairWithGain_Warmup(gs, tStats, cStats, out int uA, out int uB, out float gain))
                    break;

                var pA = Utilities.GetPlayerFromUserid(uA);
                var pB = Utilities.GetPlayerFromUserid(uB);
                if (pA == null || pB == null || !pA.UserId.HasValue || !pB.UserId.HasValue)
                    break;

                // Silence individual move messages; announce only the combined swap
                RawMove(gs, pA, (pA.TeamNum == TEAM_T) ? TEAM_CT : TEAM_T, announce:false);
                RawMove(gs, pB, (pB.TeamNum == TEAM_T) ? TEAM_CT : TEAM_T, announce:false);
                AnnounceSwap(pA, pB);
                swaps++;
            }
            return swaps;
        }

        // ===== Live round-end balancing (low-churn) =====

        private void BalanceAtRoundEnd() {
            var gs = osbase?.GetGameStats();
            if (gs == null) return;

            var tStats = gs.getTeam(TEAM_T);
            var cStats = gs.getTeam(TEAM_CT);

            // If sizes off, fix first using live signals
            var (idealT, idealCT) = ComputeIdealSizes(tStats.numPlayers(), cStats.numPlayers());
            if (tStats.numPlayers() != idealT || cStats.numPlayers() != idealCT) {
                int moves = Math.Abs(tStats.numPlayers() - idealT);
                bool moveFromT = tStats.numPlayers() > idealT;
                EvenTeamSizesLive(gs, tStats, cStats, moveFromT, moves);
                return; // let it settle; avoid swap same tick
            }

            float tAvg = TeamSignalAverage(gs, tStats);
            float cAvg = TeamSignalAverage(gs, cStats);
            float gap  = MathF.Abs(tAvg - cAvg);

            if (!ShouldSwapThisRound(gs, gap)) return;
            if (swapsThisMap >= MAX_SWAPS_PER_MAP) return;

            if (FindBestSwapPairWithGain_Live(gs, tStats, cStats, out int uA, out int uB, out float gain) &&
                gain >= MIN_PROJECTED_GAIN)
            {
                var pA = Utilities.GetPlayerFromUserid(uA);
                var pB = Utilities.GetPlayerFromUserid(uB);
                if (pA == null || pB == null || !pA.UserId.HasValue || !pB.UserId.HasValue) return;

                if (PlayerOnCooldown(uA, gs.roundNumber) || PlayerOnCooldown(uB, gs.roundNumber)) return;

                // Silence individual move messages; announce only the combined swap
                MoveWithImmunity(gs, pA, (pA.TeamNum == TEAM_T) ? TEAM_CT : TEAM_T, announce:false);
                MoveWithImmunity(gs, pB, (pB.TeamNum == TEAM_T) ? TEAM_CT : TEAM_T, announce:false);
                AnnounceSwap(pA, pB);

                lastSwapRound = gs.roundNumber;
                swapsThisMap++;
                if (gs.roundNumber >= HALF_ROUNDS + 1) lateSwapsThisHalf++;
                playerSwapRound[pA.UserId.Value] = gs.roundNumber;
                playerSwapRound[pB.UserId.Value] = gs.roundNumber;
            }
        }

        private bool ShouldSwapThisRound(GameStats gs, float gap) {
            int round = gs.roundNumber;

            // Track current half automatically
            currentHalfIndex = (round > HALF_ROUNDS) ? 1 : 0;

            // Quiet final rounds unless catastrophic
            if (round >= (MAX_ROUNDS - NO_SWAP_LAST_N_ROUNDS) && gap < EMERGENCY_GAP)
                return false;

            // Cooldown
            if (round - lastSwapRound < MIN_ROUNDS_BETWEEN_SWAPS)
                return false;

            // Threshold & late-half limits
            float thr = (currentHalfIndex == 0) ? MID_SWAP_THRESHOLD : LATE_SWAP_THRESHOLD;
            float startBand = thr + LATE_HYSTERESIS;

            if (currentHalfIndex == 1 && lateSwapsThisHalf >= MAX_LATE_SWAPS)
                return false;

            return (gap > ((currentHalfIndex == 0) ? thr : startBand));
        }

        private bool PlayerOnCooldown(int userId, int currentRound, int rounds = 4) {
            return playerSwapRound.TryGetValue(userId, out var last) && currentRound - last < rounds;
        }

        // ===== Team size helpers =====

        private (int idealT, int idealCT) ComputeIdealSizes(int tCount, int ctCount) {
            int total = tCount + ctCount;
            if (bombsites == 2) {
                // CT get the odd on 2-site maps
                return (total / 2, total / 2 + total % 2);
            } else {
                // T get the odd on 1-site (hostage/single) maps
                return (total / 2 + total % 2, total / 2);
            }
        }

        private void EvenTeamSizesWarmup(GameStats gs, TeamStats tStats, TeamStats cStats, bool moveFromT, int moves) {
            for (int i = 0; i < moves; i++) {
                var srcTeam = moveFromT ? tStats : cStats;
                int bestUser = -1;
                float bestDelta = float.MaxValue;

                float tAvg = TeamWarmupAverage90d(gs, tStats);
                float cAvg = TeamWarmupAverage90d(gs, cStats);
                float targetDeltaPer = MathF.Abs(tAvg - cAvg) / Math.Max(1, moves - i);

                foreach (var kv in srcTeam.playerList) {
                    int uid = kv.Key;
                    var ps  = kv.Value;
                    float sig = WarmupSignalForPlayer(gs, uid, ps);
                    float diff = MathF.Abs(sig - targetDeltaPer);
                    if (diff < bestDelta) { bestDelta = diff; bestUser = uid; }
                }

                if (bestUser == -1) break;
                var player = Utilities.GetPlayerFromUserid(bestUser);
                if (player == null || !player.UserId.HasValue) break;

                int toTeam = moveFromT ? TEAM_CT : TEAM_T;
                RawMove(gs, player, toTeam); // announce single move
            }
        }

        private void EvenTeamSizesLive(GameStats gs, TeamStats tStats, TeamStats cStats, bool moveFromT, int moves) {
            for (int i = 0; i < moves; i++) {
                var srcTeam = moveFromT ? tStats : cStats;
                int bestUser = -1;
                float bestScore = float.MaxValue;

                float baseGap = MathF.Abs(TeamSignalAverage(gs, tStats) - TeamSignalAverage(gs, cStats));
                foreach (var kv in srcTeam.playerList) {
                    int uid = kv.Key;
                    var ps  = kv.Value;
                    if (PlayerOnCooldown(uid, gs.roundNumber)) continue;

                    float sig = SignalSkill(gs, uid, ps);
                    float tSum = SumTeamSignal(gs, tStats);
                    float cSum = SumTeamSignal(gs, cStats);
                    int tn = tStats.numPlayers();
                    int cn = cStats.numPlayers();

                    if (moveFromT) { tSum -= sig; tn--; cSum += sig; cn++; }
                    else           { cSum -= sig; cn--; tSum += sig; tn++; }

                    float newGap = MathF.Abs((tSum / Math.Max(1, tn)) - (cSum / Math.Max(1, cn)));
                    if (newGap < bestScore) { bestScore = newGap; bestUser = uid; }
                }

                if (bestUser == -1) break;
                var player = Utilities.GetPlayerFromUserid(bestUser);
                if (player == null || !player.UserId.HasValue) break;

                int toTeam = moveFromT ? TEAM_CT : TEAM_T;
                MoveWithImmunity(gs, player, toTeam); // announce single move
                lastSwapRound = gs.roundNumber;
                swapsThisMap++;
                playerSwapRound[player.UserId.Value] = gs.roundNumber;
            }
        }

        // ===== Swap selection (composition-aware) =====

        private bool FindBestSwapPairWithGain_Warmup(GameStats gs, TeamStats tStats, TeamStats cStats,
            out int uA, out int uB, out float bestGain)
        {
            return FindBestSwapPairCore(gs, tStats, cStats, useWarmupSignals: true, out uA, out uB, out bestGain);
        }

        private bool FindBestSwapPairWithGain_Live(GameStats gs, TeamStats tStats, TeamStats cStats,
            out int uA, out int uB, out float bestGain)
        {
            return FindBestSwapPairCore(gs, tStats, cStats, useWarmupSignals: false, out uA, out uB, out bestGain);
        }

        private bool FindBestSwapPairCore(GameStats gs, TeamStats tStats, TeamStats cStats, bool useWarmupSignals,
            out int uA, out int uB, out float bestGain)
        {
            uA = -1; uB = -1; bestGain = 0f;

            var all = new List<(int uid, float sig, bool isT)>();
            foreach (var kv in tStats.playerList) {
                float sig = useWarmupSignals ? WarmupSignalForPlayer(gs, kv.Key, kv.Value) : SignalSkill(gs, kv.Key, kv.Value);
                all.Add((kv.Key, sig, true));
            }
            foreach (var kv in cStats.playerList) {
                float sig = useWarmupSignals ? WarmupSignalForPlayer(gs, kv.Key, kv.Value) : SignalSkill(gs, kv.Key, kv.Value);
                all.Add((kv.Key, sig, false));
            }

            if (all.Count < 2 || tStats.numPlayers() == 0 || cStats.numPlayers() == 0) return false;

            float baseScore = ScoreState(all, tStats, cStats);
            float bestScore = float.MaxValue;

            var sorted = all.OrderByDescending(x => x.sig).ToList();
            int quart = Math.Max(1, sorted.Count / 4);
            var strongSet = new HashSet<int>(sorted.Take(quart).Select(x => x.uid));
            var weakSet   = new HashSet<int>(sorted.Skip(Math.Max(0, sorted.Count - quart)).Select(x => x.uid));

            foreach (var s in all) {
                if (!useWarmupSignals && PlayerOnCooldown(s.uid, gs.roundNumber)) continue;
                foreach (var w in all) {
                    if (s.isT == w.isT) continue;
                    if (!useWarmupSignals && PlayerOnCooldown(w.uid, gs.roundNumber)) continue;

                    float score = ScoreStateSwapSim(all, tStats, cStats, s.uid, w.uid, strongSet, weakSet);
                    if (score < bestScore) {
                        bestScore = score;
                        uA = s.uid; uB = w.uid;
                        bestGain = baseScore - score;
                    }
                }
            }

            return (uA != -1 && uB != -1);
        }

        private float ScoreState(List<(int uid, float sig, bool isT)> all, TeamStats tStats, TeamStats cStats) {
            float tSum = 0, cSum = 0; int tn = 0, cn = 0;
            foreach (var x in all) {
                if (x.isT) { tSum += x.sig; tn++; } else { cSum += x.sig; cn++; }
            }
            float meanGap = MathF.Abs((tn > 0 ? tSum/tn : 0) - (cn > 0 ? cSum/cn : 0));

            var sorted = all.OrderByDescending(x => x.sig).ToList();
            int quart = Math.Max(1, sorted.Count / 4);
            var strongSet = new HashSet<int>(sorted.Take(quart).Select(x => x.uid));
            var weakSet   = new HashSet<int>(sorted.Skip(Math.Max(0, sorted.Count - quart)).Select(x => x.uid));

            int strongT = 0, strongCT = 0, weakT = 0, weakCT = 0;
            foreach (var x in all) {
                if (strongSet.Contains(x.uid)) { if (x.isT) strongT++; else strongCT++; }
                if (weakSet.Contains(x.uid))   { if (x.isT) weakT++;   else weakCT++;   }
            }
            float compPenalty = 300f * (MathF.Abs(strongT - strongCT) + MathF.Abs(weakT - weakCT));
            return meanGap + compPenalty;
        }

        private float ScoreStateSwapSim(List<(int uid, float sig, bool isT)> all, TeamStats tStats, TeamStats cStats,
            int uidA, int uidB, HashSet<int> strongSet, HashSet<int> weakSet)
        {
            float tSum = 0, cSum = 0; int tn = 0, cn = 0;
            int strongT = 0, strongCT = 0, weakT = 0, weakCT = 0;

            foreach (var x in all) {
                bool isT = x.isT;
                if (x.uid == uidA) isT = !isT;
                if (x.uid == uidB) isT = !isT;

                if (isT) { tSum += x.sig; tn++; } else { cSum += x.sig; cn++; }

                bool isStrong = strongSet.Contains(x.uid);
                bool isWeak   = weakSet.Contains(x.uid);
                if (isStrong) { if (isT) strongT++; else strongCT++; }
                if (isWeak)   { if (isT) weakT++;   else weakCT++;   }
            }

            float meanGap = MathF.Abs((tn > 0 ? tSum/tn : 0) - (cn > 0 ? cSum/cn : 0));
            float compPenalty = 300f * (MathF.Abs(strongT - strongCT) + MathF.Abs(weakT - weakCT));
            return meanGap + compPenalty;
        }

        // ===== Signals & averaging =====

        private float WarmupSignalForPlayer(GameStats gs, int userId, PlayerStats ps) {
            float s90 = gs.GetCached90dByUserId(userId);
            if (s90 > 0f) return s90;
            return ProvisionalSkill(ps);
        }

        private float TeamWarmupAverage90d(GameStats gs, TeamStats team) {
            if (team.playerList.Count == 0) return 0f;
            double sum = 0;
            foreach (var kv in team.playerList) {
                sum += WarmupSignalForPlayer(gs, kv.Key, kv.Value);
            }
            return (float)(sum / team.playerList.Count);
        }

        private float TeamSignalAverage(GameStats gs, TeamStats team) {
            if (team.playerList.Count == 0) return 0f;
            double sum = 0;
            foreach (var kv in team.playerList) {
                sum += SignalSkill(gs, kv.Key, kv.Value);
            }
            return (float)(sum / team.playerList.Count);
        }

        private float SumTeamSignal(GameStats gs, TeamStats team) {
            double sum = 0;
            foreach (var kv in team.playerList) sum += SignalSkill(gs, kv.Key, kv.Value);
            return (float)sum;
        }

        private float SignalSkill(GameStats gs, int userId, PlayerStats ps) {
            int round = gs.roundNumber;
            float s90  = gs.GetCached90dByUserId(userId);
            float live = ps.calcSkill();

            if (s90 > 0f) {
                bool pctOut = Math.Abs(live - s90) > OUTLIER_PCT * s90;
                bool absOut = Math.Abs(live - s90) > OUTLIER_ABS;

                if (round <= 2 || pctOut || absOut) return s90;

                float wLive;
                if (round <= 6)        wLive = 0.35f;
                else if (round <= 15)  wLive = 0.45f;
                else                   wLive = 0.30f;

                if (round >= 16) {
                    float upper = s90 + Math.Max(LATE_ABS_CLAMP, LATE_PCT_CLAMP * s90);
                    float lower = s90 - Math.Max(LATE_ABS_CLAMP, LATE_PCT_CLAMP * s90);
                    live = Math.Clamp(live, lower, upper);
                }
                return s90 * (1f - wLive) + live * wLive;
            }

            if (ps.rounds == 0) return ProvisionalSkill(ps);
            return live;
        }

        private static int StableHash(string s) {
            unchecked {
                int h = 23;
                for (int i = 0; i < s.Length; i++) h = h * 31 + s[i];
                return h == 0 ? 1 : h;
            }
        }

        private float ProvisionalSkill(PlayerStats ps) {
            string key = string.IsNullOrEmpty(ps.steamid) ? (ps.name ?? "unknown") : ps.steamid;
            int span = PROV_MAX - PROV_MIN + 1;
            int val = PROV_MIN + Math.Abs(StableHash(key)) % span;
            return val;
        }

        // ===== Moves & messages =====

        private static string TeamName(int t) => t == TEAM_T ? "T" : t == TEAM_CT ? "CT" : "SPEC";

        private void AnnounceMove(CCSPlayerController p, int fromTeam, int toTeam) {
            try {
                Server.PrintToChatAll($"[TeamBalancer] {p.PlayerName} {TeamName(fromTeam)} → {TeamName(toTeam)}");
            } catch { }
        }

        private void AnnounceSwap(CCSPlayerController a, CCSPlayerController b) {
            try {
                Server.PrintToChatAll($"[TeamBalancer] Swap: {a.PlayerName} ↔ {b.PlayerName}");
            } catch { }
        }

        private void RawMove(GameStats gs, CCSPlayerController player, int targetTeam, bool announce = true) {
            if (player == null || !player.UserId.HasValue) return;
            int from = player.TeamNum;
            player.SwitchTeam((CsTeam)targetTeam);
            gs.movePlayer(player.UserId.Value, targetTeam);
            if (announce) AnnounceMove(player, from, targetTeam);
        }

        private void MoveWithImmunity(GameStats gs, CCSPlayerController player, int targetTeam, bool announce = true) {
            if (player == null || !player.UserId.HasValue) return;
            int from = player.TeamNum;
            player.SwitchTeam((CsTeam)targetTeam);
            gs.movePlayer(player.UserId.Value, targetTeam);
            var ps = gs.GetPlayerStats(player.UserId.Value);
            ps.immune += 3;
            if (announce) AnnounceMove(player, from, targetTeam);
        }
    }
}