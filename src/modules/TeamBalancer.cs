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
        private const float WARMUP_BURST_AT         = 44f; // seconds after map start
        private const int   WARMUP_FINAL_MAX_SWAPS  = 10;  // max swap pairs in final warmup balance
        private bool warmupBalancedThisMap = false;

        // Game structure (you run 10 + 10)
        private const int HALF_ROUNDS = 10;
        private const int MAX_ROUNDS  = 20;

        // Live blending / outliers & clamps
        private const float OUTLIER_PCT     = 0.50f; // used for clamping now
        private const float OUTLIER_ABS     = 4000f; // used for clamping now
        private const float LATE_ABS_CLAMP  = 2500f; // clamp live around baseline late
        private const float LATE_PCT_CLAMP  = 0.35f; // ±35% of baseline late

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
            warmupBalancedThisMap = false;

            if (mapBombsites.TryGetValue(mapName, out int bs)) {
                bombsites = bs;
                Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Map {mapName} bombsites={bombsites}");
            } else {
                bombsites = mapName.StartsWith("cs_", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                config?.AddCustomConfigLine(mapConfigFile, $"{mapName} {bombsites}");
                Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Map {mapName} defaulted bombsites={bombsites}");
            }

            // One big "end of warmup" balance ~1s before pistol
            osbase?.AddTimer(WARMUP_BURST_AT, WarmupFinalBalance);
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
            // Only clear immunity; do not move after warmup to avoid spawn bugs
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
            currentHalfIndex  = 1;
            return HookResult.Continue;
        }

        [GameEventHandler(HookMode.Post)]
        private HookResult OnRoundEnd(EventRoundEnd ev, GameEventInfo info) {
            // mimic "do work between rounds", avoid interfering with spawns
            osbase?.AddTimer(6.5f, () => BalanceAtRoundEnd());
            return HookResult.Continue;
        }

        // ===== Warmup final balance (~44s) =====

        private void WarmupFinalBalance() {
            var gs = osbase?.GetGameStats();
            if (gs == null) return;

            // Only run once, only if still in warmup
            if (warmupBalancedThisMap) return;
            if (gs.roundNumber != 0)   return;

            warmupBalancedThisMap = true;

            var tStats = gs.getTeam(TEAM_T);
            var cStats = gs.getTeam(TEAM_CT);

            int tCount = tStats.numPlayers();
            int cCount = cStats.numPlayers();
            int total  = tCount + cCount;

            if (total == 0) return;

            Console.WriteLine($"[DEBUG] OSBase[teambalancer] WarmupFinalBalance: T={tCount}, CT={cCount}");

            // 1) Fix team sizes + odd-player side for start of game
            var (idealT, idealCT) = ComputeIdealSizes(tCount, cCount);
            if (tCount != idealT || cCount != idealCT) {
                int moves = Math.Abs(tCount - idealT);
                bool moveFromT = tCount > idealT;
                EvenTeamSizesWarmup(gs, tStats, cStats, moveFromT, moves);

                // refresh after moves
                tStats = gs.getTeam(TEAM_T);
                cStats = gs.getTeam(TEAM_CT);
                tCount = tStats.numPlayers();
                cCount = cStats.numPlayers();
                Console.WriteLine($"[DEBUG] OSBase[teambalancer] WarmupFinalBalance after size-fix: T={tCount}, CT={cCount}");
            }

            // 2) Aggressive 90d/provisional-based swaps
            int swapsDone = 0;
            while (swapsDone < WARMUP_FINAL_MAX_SWAPS) {
                float tAvg = TeamWarmupAverage90d(gs, tStats);
                float cAvg = TeamWarmupAverage90d(gs, cStats);
                float gap  = MathF.Abs(tAvg - cAvg);

                if (gap <= WARMUP_TARGET_DEVIATION)
                    break;

                if (!FindBestSwapPairWithGain_Warmup(gs, tStats, cStats, out int uA, out int uB, out float gain))
                    break;

                var pA = Utilities.GetPlayerFromUserid(uA);
                var pB = Utilities.GetPlayerFromUserid(uB);
                if (pA == null || pB == null || !pA.UserId.HasValue || !pB.UserId.HasValue)
                    break;

                // Warmup swaps: do NOT touch swap counters or cooldowns.
                RawMove(gs, pA, (pA.TeamNum == TEAM_T) ? TEAM_CT : TEAM_T, announce:false);
                RawMove(gs, pB, (pB.TeamNum == TEAM_T) ? TEAM_CT : TEAM_T, announce:false);
                AnnounceSwap(pA, pB);

                swapsDone++;

                // refresh stats for next loop
                tStats = gs.getTeam(TEAM_T);
                cStats = gs.getTeam(TEAM_CT);
            }

            // 3) If it's 2v1, enforce "best player is solo" rule
            EnsureBestIsSoloIf2v1(gs);

            Console.WriteLine($"[DEBUG] OSBase[teambalancer] WarmupFinalBalance done: swaps={swapsDone}");
        }

        // ===== Live round-end balancing (low-churn) =====

        private void BalanceAtRoundEnd() {
            var gs = osbase?.GetGameStats();
            if (gs == null) return;

            var tStats = gs.getTeam(TEAM_T);
            var cStats = gs.getTeam(TEAM_CT);

            int tCount = tStats.numPlayers();
            int cCount = cStats.numPlayers();

            // 1) ALWAYS fix team sizes first (phase-aware odd-player, incl. last round before halftime)
            var (idealT, idealCT) = ComputeIdealSizesForRound(gs, tCount, cCount);
            if (tCount != idealT || cCount != idealCT) {
                int moves = Math.Abs(tCount - idealT);
                bool moveFromT = tCount > idealT;
                EvenTeamSizesLive(gs, tStats, cStats, moveFromT, moves);

                // refresh after size moves
                tStats = gs.getTeam(TEAM_T);
                cStats = gs.getTeam(TEAM_CT);
            }

            // 2) Then possibly do a performance swap if needed
            float tAvg = TeamSignalAverage(gs, tStats);
            float cAvg = TeamSignalAverage(gs, cStats);
            float gap  = MathF.Abs(tAvg - cAvg);

            if (!ShouldSwapThisRound(gs, gap)) {
                // even if no swap, still enforce 2v1-best-solo rule if applicable
                EnsureBestIsSoloIf2v1(gs);
                return;
            }
            if (swapsThisMap >= MAX_SWAPS_PER_MAP) {
                EnsureBestIsSoloIf2v1(gs);
                return;
            }

            if (FindBestSwapPairWithGain_Live(gs, tStats, cStats, out int uA, out int uB, out float gain) &&
                gain >= MIN_PROJECTED_GAIN)
            {
                var pA = Utilities.GetPlayerFromUserid(uA);
                var pB = Utilities.GetPlayerFromUserid(uB);
                if (pA == null || pB == null || !pA.UserId.HasValue || !pB.UserId.HasValue) {
                    EnsureBestIsSoloIf2v1(gs);
                    return;
                }

                if (PlayerOnCooldown(uA, gs.roundNumber) || PlayerOnCooldown(uB, gs.roundNumber)) {
                    EnsureBestIsSoloIf2v1(gs);
                    return;
                }

                MoveWithImmunity(gs, pA, (pA.TeamNum == TEAM_T) ? TEAM_CT : TEAM_T, announce:false);
                MoveWithImmunity(gs, pB, (pB.TeamNum == TEAM_T) ? TEAM_CT : TEAM_T, announce:false);
                AnnounceSwap(pA, pB);

                lastSwapRound = gs.roundNumber;
                swapsThisMap++;
                if (gs.roundNumber >= HALF_ROUNDS + 1) lateSwapsThisHalf++;
                playerSwapRound[pA.UserId.Value] = gs.roundNumber;
                playerSwapRound[pB.UserId.Value] = gs.roundNumber;
            }

            // 3) After any swaps, enforce 2v1-best-solo rule if applicable
            EnsureBestIsSoloIf2v1(gs);
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

        // ===== 2v1 helper: ensure best player is solo =====

        private void EnsureBestIsSoloIf2v1(GameStats gs) {
            var tStats = gs.getTeam(TEAM_T);
            var cStats = gs.getTeam(TEAM_CT);

            int tCount = tStats.numPlayers();
            int cCount = cStats.numPlayers();
            int total  = tCount + cCount;

            if (total != 3) return; // only care about 2v1

            int soloTeam;
            if (tCount == 1 && cCount == 2) {
                soloTeam = TEAM_T;
            } else if (tCount == 2 && cCount == 1) {
                soloTeam = TEAM_CT;
            } else {
                // Something weird (3v0 etc); ignore.
                return;
            }

            // Determine best skill among the 3 players
            int bestUid      = -1;
            int bestTeamNum  = TEAM_S;
            float bestSkill  = float.MinValue;

            void ConsiderPlayer(KeyValuePair<int, PlayerStats> kv, int teamNum) {
                int uid = kv.Key;
                var ps  = kv.Value;
                float s = (gs.roundNumber == 0)
                    ? WarmupSignalForPlayer(gs, uid, ps)
                    : SignalSkill(gs, uid, ps);

                if (s > bestSkill) {
                    bestSkill  = s;
                    bestUid    = uid;
                    bestTeamNum = teamNum;
                }
            }

            foreach (var kv in tStats.playerList) ConsiderPlayer(kv, TEAM_T);
            foreach (var kv in cStats.playerList) ConsiderPlayer(kv, TEAM_CT);

            if (bestUid == -1)
                return;

            var soloStats = (soloTeam == TEAM_T) ? tStats : cStats;
            if (soloStats.playerList.Count != 1)
                return;

            int soloUid = soloStats.playerList.First().Key;

            // If best player is already solo, we're done
            if (bestUid == soloUid)
                return;

            var soloPlayer = Utilities.GetPlayerFromUserid(soloUid);
            var bestPlayer = Utilities.GetPlayerFromUserid(bestUid);
            if (soloPlayer == null || bestPlayer == null ||
                !soloPlayer.UserId.HasValue || !bestPlayer.UserId.HasValue)
                return;

            int otherTeam = (soloTeam == TEAM_T) ? TEAM_CT : TEAM_T;

            // Move solo player to pair team, best player to solo team
            MoveWithImmunity(gs, soloPlayer, otherTeam, announce:false);
            MoveWithImmunity(gs, bestPlayer, soloTeam, announce:false);
            AnnounceSwap(bestPlayer, soloPlayer);

            playerSwapRound[soloPlayer.UserId.Value] = gs.roundNumber;
            playerSwapRound[bestPlayer.UserId.Value] = gs.roundNumber;
        }

        // ===== Team size helpers =====

        // Base rule: for a given total, odd goes to CT on 2-site, to T on 1-site.
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

        // Phase-aware: on the last round of first half, flip odd owner so after side swap
        // the odd player ends up on the correct side in the second half.
        private (int idealT, int idealCT) ComputeIdealSizesForRound(GameStats gs, int tCount, int ctCount) {
            int total = tCount + ctCount;
            if (total == 0)
                return (0, 0);

            var (baseT, baseCT) = ComputeIdealSizes(tCount, ctCount);

            bool isOdd              = (total % 2) != 0;
            bool lastRoundFirstHalf = (gs.roundNumber == HALF_ROUNDS);

            if (!isOdd || !lastRoundFirstHalf)
                return (baseT, baseCT);

            // Flip odd owner for the final round of the half
            if (bombsites == 2) {
                // Normally CT has the extra. For this round, move it to T,
                // so that after the side swap the extra ends up on CT.
                if (baseCT > baseT) {
                    baseCT--;
                    baseT++;
                }
            } else {
                // Normally T has the extra. For this round, move it to CT,
                // so that after the side swap the extra ends up on T.
                if (baseT > baseCT) {
                    baseT--;
                    baseCT++;
                }
            }

            return (baseT, baseCT);
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

                // refresh teams for next iteration
                tStats = gs.getTeam(TEAM_T);
                cStats = gs.getTeam(TEAM_CT);
            }
        }

        private void EvenTeamSizesLive(GameStats gs, TeamStats tStats, TeamStats cStats, bool moveFromT, int moves) {
            // Hard requirement: team sizes must match ComputeIdealSizesForRound.
            // Ignores cooldown for selection but does not count as a "swap" for anti-churn.
            for (int i = 0; i < moves; i++) {
                var srcTeam = moveFromT ? tStats : cStats;
                if (srcTeam.playerList.Count == 0)
                    break;

                int bestUser = -1;
                float bestScore = float.MaxValue;

                float tBaseSum = SumTeamSignal(gs, tStats);
                float cBaseSum = SumTeamSignal(gs, cStats);
                int tBaseN = tStats.numPlayers();
                int cBaseN = cStats.numPlayers();

                foreach (var kv in srcTeam.playerList) {
                    int uid = kv.Key;
                    var ps  = kv.Value;

                    float sig = SignalSkill(gs, uid, ps);
                    float tSum = tBaseSum;
                    float cSum = cBaseSum;
                    int tn = tBaseN;
                    int cn = cBaseN;

                    if (moveFromT) { tSum -= sig; tn--; cSum += sig; cn++; }
                    else           { cSum -= sig; cn--; tSum += sig; tn++; }

                    float newGap = MathF.Abs((tn > 0 ? tSum / tn : 0f) - (cn > 0 ? cSum / cn : 0f));
                    if (newGap < bestScore) { bestScore = newGap; bestUser = uid; }
                }

                // hard fallback: if somehow no best user, just grab the first in srcTeam
                if (bestUser == -1) {
                    foreach (var kv in srcTeam.playerList) { bestUser = kv.Key; break; }
                    if (bestUser == -1) break;
                }

                var player = Utilities.GetPlayerFromUserid(bestUser);
                if (player == null || !player.UserId.HasValue) break;

                int toTeam = moveFromT ? TEAM_CT : TEAM_T;

                // Size-fix move: we do NOT touch lastSwapRound / swapsThisMap / lateSwaps.
                MoveWithImmunity(gs, player, toTeam);
                playerSwapRound[player.UserId.Value] = gs.roundNumber;

                // refresh for next potential size move
                tStats = gs.getTeam(TEAM_T);
                cStats = gs.getTeam(TEAM_CT);
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
            int round        = gs.roundNumber;
            int playerRounds = ps.rounds;

            float dbSkill  = gs.GetCached90dByUserId(userId);
            float baseline = (dbSkill > 0f) ? dbSkill : ProvisionalSkill(ps); // 90d or provisional
            float live     = ps.calcSkill();

            // No rounds played yet → pure baseline
            if (playerRounds <= 0 || round <= 0)
                return baseline;

            // --- Outlier handling: clamp live around baseline instead of ignoring it ---
            float diff      = Math.Abs(live - baseline);
            float maxDelta1 = OUTLIER_ABS;
            float maxDelta2 = OUTLIER_PCT * Math.Max(1f, baseline);
            float maxDelta  = Math.Max(maxDelta1, maxDelta2);

            if (diff > maxDelta) {
                float delta = live - baseline;
                delta = Math.Clamp(delta, -maxDelta, maxDelta);
                live  = baseline + delta;
            }

            // Extra late-game clamp
            if (round >= 16) {
                float lateBand = Math.Max(LATE_ABS_CLAMP, LATE_PCT_CLAMP * Math.Max(1f, baseline));
                float upper    = baseline + lateBand;
                float lower    = baseline - lateBand;
                live           = Math.Clamp(live, lower, upper);
            }

            // --- Per-player ramp-in: how fast this specific player moves toward live ---
            const float LIVE_PER_ROUND  = 0.15f;
            const float MAX_LIVE_WEIGHT = 0.80f; // => min 20% baseline forever

            float wPlayer = Math.Clamp(playerRounds * LIVE_PER_ROUND, 0f, MAX_LIVE_WEIGHT);

            // --- Global match stage limiter (don’t over-trust live at pistol, etc.) ---
            float wGlobal;
            if (round <= 2)        wGlobal = 0.00f; // pistol chaos → 100% baseline
            else if (round <= 4)   wGlobal = 0.40f;
            else if (round <= 10)  wGlobal = 0.60f;
            else                   wGlobal = 0.80f; // later rounds: allow up to 80% live

            // Final live weight is limited by BOTH: per-player ramp and match stage
            float wLive = MathF.Min(wPlayer, wGlobal);
            wLive       = MathF.Min(wLive, 0.80f); // safety: keep ≥20% baseline

            return baseline * (1f - wLive) + live * wLive;
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

        private void SlayIfWarmup(GameStats gs, CCSPlayerController player) {
            if (gs.roundNumber != 0) return;                // only during warmup
            if (player == null || !player.IsValid) return;
            if (!player.PawnIsAlive) return;

            // Kill so they respawn on the correct team/spawn in warmup
            player.CommitSuicide(false, false);
        }

        private void RawMove(GameStats gs, CCSPlayerController player, int targetTeam, bool announce = true) {
            if (player == null || !player.UserId.HasValue) return;
            int from = player.TeamNum;

            player.SwitchTeam((CsTeam)targetTeam);
            gs.movePlayer(player.UserId.Value, targetTeam);

            // Warmup safety: kill them so they respawn on correct team/spawn
            SlayIfWarmup(gs, player);

            if (announce) AnnounceMove(player, from, targetTeam);
        }

        private void MoveWithImmunity(GameStats gs, CCSPlayerController player, int targetTeam, bool announce = true) {
            if (player == null || !player.UserId.HasValue) return;
            int from = player.TeamNum;

            player.SwitchTeam((CsTeam)targetTeam);
            gs.movePlayer(player.UserId.Value, targetTeam);
            var ps = gs.GetPlayerStats(player.UserId.Value);
            ps.immune += 3;

            // Warmup safety: kill them so they respawn on correct team/spawn
            SlayIfWarmup(gs, player);

            if (announce) AnnounceMove(player, from, targetTeam);
        }
    }
}