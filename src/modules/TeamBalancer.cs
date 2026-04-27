using System;
using System.Linq;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using OSBase.Helpers;

namespace OSBase.Modules {

    public class TeamBalancer : IModule {
        public string ModuleName => "teambalancer";
        public string ModuleNameNice => "TeamBalancer";

        public static TeamBalancer? Current { get; private set; }

        private bool handlersLoaded = false;
        private CounterStrikeSharp.API.Modules.Timers.Timer? warmupBalanceTimer;
        private OSBase? osbase;
        private Config? config;
        private GameStats? gameStats;
        private bool isActive = false;

        // Teams
        private const int TEAM_S = (int)CsTeam.Spectator;
        private const int TEAM_T = (int)CsTeam.Terrorist;
        private const int TEAM_CT = (int)CsTeam.CounterTerrorist;

        // Map bombsite info
        private readonly Dictionary<string, int> mapBombsites = new();
        private const string mapConfigFile = "teambalancer_mapinfo.cfg";
        private int bombsites = 2;
        private string currentMap = "";

        // Warmup policy
        private const float WARMUP_TARGET_DEVIATION = 1500f;
        private const float WARMUP_BURST_AT = 57.0f; // 60s warmup -> run with ~3s left
        private const int WARMUP_FINAL_MAX_SWAPS = 10;
        private bool warmupBalancedThisMap = false;

        // Round 1 safety-net
        private bool firstRoundSizeFixDone = false;

        // Game structure
        private const int HALF_ROUNDS = 10;
        private const int MAX_ROUNDS = 20;

        // Swap thresholds
        private const float MID_SWAP_THRESHOLD = 1500f;
        private const float LATE_SWAP_THRESHOLD = 1900f;
        private const float LATE_HYSTERESIS = 700f;
        private const float MIN_PROJECTED_GAIN = 800f;

        // Anti-churn
        private const int MIN_ROUNDS_BETWEEN_SWAPS = 3;
        private const int NO_SWAP_LAST_N_ROUNDS = 3;
        private const int MAX_LATE_SWAPS = 1;
        private const int MAX_SWAPS_PER_MAP = 3;
        private const float EMERGENCY_GAP = 3500f;

        private int lastSwapRound = -999;
        private int lateSwapsThisHalf = 0;
        private int swapsThisMap = 0;
        private int currentHalfIndex = 0; // 0 first half, 1 second half

        // Per-player cooldown
        private readonly Dictionary<int, int> playerSwapRound = new();

        // 3-player halftime latch
        private bool threePlayerHalftimeMode = false;

        public void Load(OSBase inOsbase, Config inConfig) {
            Current = this;
            isActive = true;

            osbase = inOsbase;
            config = inConfig;
            gameStats = osbase.GetGameStats();

            if (osbase == null || config == null) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] load failed (null deps).");
                return;
            }

            config.RegisterGlobalConfigValue(ModuleName, "1");
            var enabled = config.GetGlobalConfigValue(ModuleName, "0");
            if (enabled != "1") {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in config.");
                return;
            }

            LoadMapInfo();

            try {
                LoadHandlers();
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded. warmup_final_at={WARMUP_BURST_AT:0.0}s live_roundend_only_after_warmup=true");
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] registering: {ex.Message}");
            }
        }

        private void LoadHandlers() {
            if (osbase == null || handlersLoaded) {
                return;
            }

            osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);
            osbase.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

            osbase.RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd, HookMode.Post);
            osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd, HookMode.Post);
            osbase.RegisterEventHandler<EventStartHalftime>(OnStartHalftime, HookMode.Post);
            osbase.RegisterEventHandler<EventRoundPrestart>(OnRoundPrestart, HookMode.Post);

            handlersLoaded = true;
        }

        public void Unload() {
            warmupBalanceTimer?.Kill();
            warmupBalanceTimer = null;
            isActive = false;

            if (osbase != null && handlersLoaded) {
                osbase.RemoveListener<Listeners.OnMapStart>(OnMapStart);
                osbase.RemoveListener<Listeners.OnMapEnd>(OnMapEnd);

                osbase.DeregisterEventHandler<EventWarmupEnd>(OnWarmupEnd, HookMode.Post);
                osbase.DeregisterEventHandler<EventRoundEnd>(OnRoundEnd, HookMode.Post);
                osbase.DeregisterEventHandler<EventStartHalftime>(OnStartHalftime, HookMode.Post);
                osbase.DeregisterEventHandler<EventRoundPrestart>(OnRoundPrestart, HookMode.Post);

                handlersLoaded = false;
            }

            if (ReferenceEquals(Current, this)) {
                Current = null;
            }

            gameStats = null;
            config = null;
            osbase = null;

            currentMap = "";
            bombsites = 2;
            warmupBalancedThisMap = false;
            firstRoundSizeFixDone = false;
            threePlayerHalftimeMode = false;
            lastSwapRound = -999;
            lateSwapsThisHalf = 0;
            swapsThisMap = 0;
            currentHalfIndex = 0;

            playerSwapRound.Clear();

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] unloaded.");
        }

        public void ReloadConfig(Config inConfig) {
            config = inConfig;
            gameStats = osbase?.GetGameStats();

            LoadMapInfo();

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] config reloaded.");
        }

        private void LoadMapInfo() {
            mapBombsites.Clear();

            config?.CreateCustomConfig(mapConfigFile, "// Map info\nde_dust2 2\n");
            var lines = config?.FetchCustomConfig(mapConfigFile) ?? new List<string>();

            foreach (var raw in lines) {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("//")) {
                    continue;
                }

                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[1], out int bs)) {
                    mapBombsites[parts[0]] = bs;
                }
            }
        }

        private void OnMapStart(string mapName) {
            currentMap = mapName;
            swapsThisMap = 0;
            lateSwapsThisHalf = 0;
            currentHalfIndex = 0;
            warmupBalancedThisMap = false;
            firstRoundSizeFixDone = false;
            threePlayerHalftimeMode = false;
            lastSwapRound = -999;

            playerSwapRound.Clear();

            warmupBalanceTimer?.Kill();
            warmupBalanceTimer = null;

            if (mapBombsites.TryGetValue(mapName, out int bs)) {
                bombsites = bs;
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] map={mapName} bombsites={bombsites}");
            } else {
                bombsites = mapName.StartsWith("cs_", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                config?.AddCustomConfigLine(mapConfigFile, $"{mapName} {bombsites}");
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] map={mapName} defaulted bombsites={bombsites}");
            }

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] warmup window scheduled: final_balance_at={WARMUP_BURST_AT:0.0}s map={mapName} bombsites={bombsites}");
            warmupBalanceTimer = osbase?.AddTimer(WARMUP_BURST_AT, WarmupFinalBalance);
        }

        private void OnMapEnd() {
            warmupBalanceTimer?.Kill();
            warmupBalanceTimer = null;
        }

        public float GetEffectiveSkillForPriority(CCSPlayerController player) {
            return SkillResolver.GetEffectiveSkillForPlayer(gameStats, player);
        }

        public float GetEffectiveSkillForPriority(int userId) {
            return SkillResolver.GetEffectiveSkill(gameStats, userId);
        }

        private bool IsWarmup(GameStats gs) {
            return gs.roundNumber == 0;
        }

        private string PhaseName(GameStats gs) {
            return IsWarmup(gs) ? "warmup" : $"live_round_{gs.roundNumber}";
        }

        private void SyncTeams(GameStats gs) {
            gs.SyncTeamsNow();
            UpdateThreePlayerHalftimeMode(gs);
        }

        private static bool IsHumanPlayer(CCSPlayerController? player) {
            return player != null
                && player.IsValid
                && player.UserId.HasValue
                && !player.IsBot;
        }

        private static bool IsPlayingTeam(CCSPlayerController player) {
            return player.TeamNum == TEAM_T || player.TeamNum == TEAM_CT;
        }

        private int CountHumansOnTeams() {
            try {
                return Utilities.GetPlayers()
                    .Count(p => IsHumanPlayer(p) && IsPlayingTeam(p));
            } catch {
                return 0;
            }
        }

        private bool SkipBalanceDueToTooFewHumans(string source) {
            int humans = CountHumansOnTeams();

            if (humans < 2) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {source} skipped: humans_on_teams={humans} < 2");
                return true;
            }

            return false;
        }

        private void UpdateThreePlayerHalftimeMode(GameStats gs) {
            int total = gs.getTeam(TEAM_T).numPlayers() + gs.getTeam(TEAM_CT).numPlayers();

            if (total != 3) {
                if (threePlayerHalftimeMode) {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] threePlayerHalftimeMode OFF total={total}");
                }
                threePlayerHalftimeMode = false;
                return;
            }

            if (bombsites != 1) {
                threePlayerHalftimeMode = false;
                return;
            }
        }

        private HookResult OnWarmupEnd(EventWarmupEnd ev, GameEventInfo info) {
            var gs = osbase?.GetGameStats();
            if (gs != null) {
                gs.SyncTeamsNow();

                foreach (var kv in gs.getTeam(TEAM_T).playerList) {
                    kv.Value.immune = 0;
                }
                foreach (var kv in gs.getTeam(TEAM_CT).playerList) {
                    kv.Value.immune = 0;
                }
                foreach (var kv in gs.getTeam(TEAM_S).playerList) {
                    kv.Value.immune = 0;
                }
            }

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] WarmupEnd fired. no_moves_here=true");
            return HookResult.Continue;
        }

        private HookResult OnStartHalftime(EventStartHalftime ev, GameEventInfo info) {
            lateSwapsThisHalf = 0;
            currentHalfIndex = 1;

            var gs = osbase?.GetGameStats();
            if (gs != null) {
                gs.SyncTeamsNow();
                int total = gs.getTeam(TEAM_T).numPlayers() + gs.getTeam(TEAM_CT).numPlayers();

                threePlayerHalftimeMode = (bombsites == 1 && total == 3 && CountHumansOnTeams() == 3);
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Halftime: threePlayerHalftimeMode={threePlayerHalftimeMode} total={total}");
            } else {
                threePlayerHalftimeMode = false;
            }

            return HookResult.Continue;
        }

        private HookResult OnRoundEnd(EventRoundEnd ev, GameEventInfo info) {
            var gs = osbase?.GetGameStats();
            if (gs == null) {
                return HookResult.Continue;
            }

            if (IsWarmup(gs)) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] RoundEnd ignored during warmup. source=roundend phase=warmup");
                return HookResult.Continue;
            }

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] RoundEnd scheduling live balance in 6.5s. round={gs.roundNumber}");
            osbase?.AddTimer(6.5f, () => BalanceAtRoundEnd());

            return HookResult.Continue;
        }

        private HookResult OnRoundPrestart(EventRoundPrestart ev, GameEventInfo info) {
            var gs = osbase?.GetGameStats();
            if (gs == null) return HookResult.Continue;
            if (firstRoundSizeFixDone) return HookResult.Continue;
            if (gs.roundNumber != 1) return HookResult.Continue;

            firstRoundSizeFixDone = true;

            Console.WriteLine($"[WARN] OSBase[{ModuleName}] RoundPrestart round=1 fallback triggered. size-fix only.");
            osbase?.AddTimer(0.2f, () => ForceSizeFixForFirstRound());
            return HookResult.Continue;
        }

        private void ForceSizeFixForFirstRound() {
            if (!isActive) return;
            var gs = osbase?.GetGameStats();
            if (gs == null) return;

            SyncTeams(gs);

            if (SkipBalanceDueToTooFewHumans("round1_sizefix")) {
                return;
            }

            var tStats = gs.getTeam(TEAM_T);
            var cStats = gs.getTeam(TEAM_CT);

            int tCount = tStats.numPlayers();
            int cCount = cStats.numPlayers();

            var (idealT, idealCT) = ComputeIdealSizesForRound(gs, tCount, cCount);
            if (tCount == idealT && cCount == idealCT) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Round1 fallback sizes already OK. T={tCount} CT={cCount}");
                EnsureBestIsSoloIf2v1(gs);
                return;
            }

            int moves = Math.Abs(tCount - idealT);
            bool moveFromT = tCount > idealT;

            Console.WriteLine($"[WARN] OSBase[{ModuleName}] Round1 fallback size-fix: T={tCount},CT={cCount} -> T={idealT},CT={idealCT} moves={moves} from={(moveFromT ? "T" : "CT")}");
            EvenTeamSizesLive(gs, tStats, cStats, moveFromT, moves, reason: "round1_sizefix");

            EnsureBestIsSoloIf2v1(gs);
        }

        private void WarmupFinalBalance() {
            if (!isActive) return;
            var gs = osbase?.GetGameStats();
            if (gs == null) return;

            SyncTeams(gs);

            if (SkipBalanceDueToTooFewHumans("warmup_final")) {
                return;
            }

            if (warmupBalancedThisMap) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] WarmupFinalBalance skipped: already ran.");
                return;
            }

            if (!IsWarmup(gs)) {
                Console.WriteLine($"[WARN] OSBase[{ModuleName}] WarmupFinalBalance missed warmup. roundNumber={gs.roundNumber}");
                return;
            }

            warmupBalancedThisMap = true;

            var tStats = gs.getTeam(TEAM_T);
            var cStats = gs.getTeam(TEAM_CT);

            int tCount = tStats.numPlayers();
            int cCount = cStats.numPlayers();
            int total = tCount + cCount;

            Console.WriteLine($"[INFO] OSBase[{ModuleName}] WarmupFinalBalance RUN source=warmup_final phase={PhaseName(gs)} t={tCount} ct={cCount} total={total} map={currentMap}");
            if (total == 0) return;

            var (idealT, idealCT) = ComputeIdealSizes(tCount, cCount);
            if (tCount != idealT || cCount != idealCT) {
                int moves = Math.Abs(tCount - idealT);
                bool moveFromT = tCount > idealT;
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] WarmupFinalBalance size-fix: T={tCount},CT={cCount} -> T={idealT},CT={idealCT} moves={moves} from={(moveFromT ? "T" : "CT")}");
                EvenTeamSizesWarmup(gs, tStats, cStats, moveFromT, moves, reason: "warmup_sizefix");

                tStats = gs.getTeam(TEAM_T);
                cStats = gs.getTeam(TEAM_CT);
                tCount = tStats.numPlayers();
                cCount = cStats.numPlayers();
                total = tCount + cCount;

                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] WarmupFinalBalance after size-fix: T={tCount} CT={cCount}");
            }

            if (total < 4) {
                EnsureBestIsSoloIf2v1(gs);
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] WarmupFinalBalance DONE total<{4}. skill_swaps_skipped=true");
                return;
            }

            int swapsDone = 0;
            while (swapsDone < WARMUP_FINAL_MAX_SWAPS) {
                float tAvg = TeamWarmupAverage90d(gs, tStats);
                float cAvg = TeamWarmupAverage90d(gs, cStats);
                float gap = MathF.Abs(tAvg - cAvg);

                if (gap <= WARMUP_TARGET_DEVIATION) {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] WarmupFinalBalance gap OK gap={gap:0} threshold={WARMUP_TARGET_DEVIATION:0}");
                    break;
                }

                if (!FindBestSwapPairWithGain_Warmup(gs, tStats, cStats, out int uA, out int uB, out float gain) || gain <= 0f) {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] WarmupFinalBalance no useful swap pair found. gain={gain:0}");
                    break;
                }

                var pA = Utilities.GetPlayerFromUserid(uA);
                var pB = Utilities.GetPlayerFromUserid(uB);
                if (pA == null || pB == null || !pA.UserId.HasValue || !pB.UserId.HasValue) {
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}] WarmupFinalBalance invalid controllers for swap pair.");
                    break;
                }

                Console.WriteLine($"[INFO] OSBase[{ModuleName}] WarmupFinalBalance swap plan: {pA.PlayerName}({uA})[{TeamName(pA.TeamNum)}] <-> {pB.PlayerName}({uB})[{TeamName(pB.TeamNum)}] gain={gain:0}");

                RawMove(gs, pA, (pA.TeamNum == TEAM_T) ? TEAM_CT : TEAM_T, announce: false, reason: "warmup_swap");
                RawMove(gs, pB, (pB.TeamNum == TEAM_T) ? TEAM_CT : TEAM_T, announce: false, reason: "warmup_swap");
                AnnounceSwap(pA, pB);

                swapsDone++;

                tStats = gs.getTeam(TEAM_T);
                cStats = gs.getTeam(TEAM_CT);
            }

            EnsureBestIsSoloIf2v1(gs);

            Console.WriteLine($"[INFO] OSBase[{ModuleName}] WarmupFinalBalance DONE swaps={swapsDone}");
        }

        private void BalanceAtRoundEnd() {
            if (!isActive) return;
            var gs = osbase?.GetGameStats();
            if (gs == null) return;

            if (IsWarmup(gs)) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] BalanceAtRoundEnd aborted during warmup.");
                return;
            }

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] BalanceAtRoundEnd RUN source=roundend_live phase={PhaseName(gs)}");

            SyncTeams(gs);

            if (SkipBalanceDueToTooFewHumans("roundend_live")) {
                return;
            }

            var tStats = gs.getTeam(TEAM_T);
            var cStats = gs.getTeam(TEAM_CT);

            int tCount = tStats.numPlayers();
            int cCount = cStats.numPlayers();
            int total = tCount + cCount;

            var (idealT, idealCT) = ComputeIdealSizesForRound(gs, tCount, cCount);
            if (tCount != idealT || cCount != idealCT) {
                int moves = Math.Abs(tCount - idealT);
                bool moveFromT = tCount > idealT;
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] RoundEnd size-fix: round={gs.roundNumber} T={tCount},CT={cCount} -> T={idealT},CT={idealCT} moves={moves} from={(moveFromT ? "T" : "CT")}");
                EvenTeamSizesLive(gs, tStats, cStats, moveFromT, moves, reason: "roundend_sizefix");

                tStats = gs.getTeam(TEAM_T);
                cStats = gs.getTeam(TEAM_CT);
                tCount = tStats.numPlayers();
                cCount = cStats.numPlayers();
                total = tCount + cCount;
            }

            if (total < 4) {
                EnsureBestIsSoloIf2v1(gs);
                return;
            }

            float tAvg = TeamSignalAverage(gs, tStats);
            float cAvg = TeamSignalAverage(gs, cStats);
            float gap = MathF.Abs(tAvg - cAvg);

            if (!ShouldSwapThisRound(gs, gap)) {
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

                Console.WriteLine($"[INFO] OSBase[{ModuleName}] RoundEnd swap plan: round={gs.roundNumber} {pA.PlayerName}({uA})[{TeamName(pA.TeamNum)}] <-> {pB.PlayerName}({uB})[{TeamName(pB.TeamNum)}] gain={gain:0}");

                MoveWithImmunity(gs, pA, (pA.TeamNum == TEAM_T) ? TEAM_CT : TEAM_T, announce: false, reason: "roundend_swap");
                MoveWithImmunity(gs, pB, (pB.TeamNum == TEAM_T) ? TEAM_CT : TEAM_T, announce: false, reason: "roundend_swap");
                AnnounceSwap(pA, pB);

                lastSwapRound = gs.roundNumber;
                swapsThisMap++;
                if (gs.roundNumber >= HALF_ROUNDS + 1) {
                    lateSwapsThisHalf++;
                }

                playerSwapRound[pA.UserId.Value] = gs.roundNumber;
                playerSwapRound[pB.UserId.Value] = gs.roundNumber;
            }

            EnsureBestIsSoloIf2v1(gs);
        }

        private bool ShouldSwapThisRound(GameStats gs, float gap) {
            int round = gs.roundNumber;
            currentHalfIndex = (round > HALF_ROUNDS) ? 1 : 0;

            if (round >= (MAX_ROUNDS - NO_SWAP_LAST_N_ROUNDS) && gap < EMERGENCY_GAP) {
                return false;
            }

            if (round - lastSwapRound < MIN_ROUNDS_BETWEEN_SWAPS) {
                return false;
            }

            float thr = (currentHalfIndex == 0) ? MID_SWAP_THRESHOLD : LATE_SWAP_THRESHOLD;
            float startBand = thr + LATE_HYSTERESIS;

            if (currentHalfIndex == 1 && lateSwapsThisHalf >= MAX_LATE_SWAPS) {
                return false;
            }

            return gap > ((currentHalfIndex == 0) ? thr : startBand);
        }

        private bool PlayerOnCooldown(int userId, int currentRound, int rounds = 4) {
            return playerSwapRound.TryGetValue(userId, out var last) && currentRound - last < rounds;
        }

        private void EnsureBestIsSoloIf2v1(GameStats gs) {
            SyncTeams(gs);

            int humans = CountHumansOnTeams();
            if (humans != 3) {
                return;
            }

            var tStats = gs.getTeam(TEAM_T);
            var cStats = gs.getTeam(TEAM_CT);

            int tCount = tStats.numPlayers();
            int cCount = cStats.numPlayers();
            int total = tCount + cCount;

            if (total != 3) return;

            int soloTeam;
            if (tCount == 1 && cCount == 2) {
                soloTeam = TEAM_T;
            } else if (tCount == 2 && cCount == 1) {
                soloTeam = TEAM_CT;
            } else {
                return;
            }

            int bestUid = -1;
            float bestSkill = float.MinValue;

            void ConsiderPlayer(KeyValuePair<int, PlayerStats> kv) {
                int uid = kv.Key;
                var ps = kv.Value;
                float s = IsWarmup(gs)
                    ? WarmupSignalForPlayer(gs, uid, ps)
                    : SignalSkill(gs, uid, ps);

                if (s > bestSkill) {
                    bestSkill = s;
                    bestUid = uid;
                }
            }

            foreach (var kv in tStats.playerList) {
                ConsiderPlayer(kv);
            }
            foreach (var kv in cStats.playerList) {
                ConsiderPlayer(kv);
            }

            if (bestUid == -1) return;

            var soloStats = (soloTeam == TEAM_T) ? tStats : cStats;
            if (soloStats.playerList.Count != 1) return;

            int soloUid = soloStats.playerList.First().Key;
            if (bestUid == soloUid) return;

            var soloPlayer = Utilities.GetPlayerFromUserid(soloUid);
            var bestPlayer = Utilities.GetPlayerFromUserid(bestUid);
            if (soloPlayer == null || bestPlayer == null || !soloPlayer.UserId.HasValue || !bestPlayer.UserId.HasValue) {
                return;
            }

            int otherTeam = (soloTeam == TEAM_T) ? TEAM_CT : TEAM_T;

            Console.WriteLine($"[INFO] OSBase[{ModuleName}] 2v1 rule: making best solo. best={bestPlayer.PlayerName}({bestUid}) soloWas={soloPlayer.PlayerName}({soloUid})");

            MoveWithImmunity(gs, soloPlayer, otherTeam, announce: false, reason: "2v1_best_solo");
            MoveWithImmunity(gs, bestPlayer, soloTeam, announce: false, reason: "2v1_best_solo");
            AnnounceSwap(bestPlayer, soloPlayer);

            playerSwapRound[soloPlayer.UserId.Value] = gs.roundNumber;
            playerSwapRound[bestPlayer.UserId.Value] = gs.roundNumber;
        }

        private (int idealT, int idealCT) ComputeIdealSizes(int tCount, int ctCount) {
            int total = tCount + ctCount;

            if (bombsites == 2) {
                return (total / 2, total / 2 + total % 2);
            }

            return (total / 2 + total % 2, total / 2);
        }

        private (int idealT, int idealCT) ComputeIdealSizesForRound(GameStats gs, int tCount, int ctCount) {
            UpdateThreePlayerHalftimeMode(gs);

            int total = tCount + ctCount;
            if (total == 0) return (0, 0);

            var (baseT, baseCT) = ComputeIdealSizes(tCount, ctCount);

            bool isOdd = (total % 2) != 0;
            bool lastRoundFirstHalf = (gs.roundNumber == HALF_ROUNDS);

            if (isOdd && lastRoundFirstHalf && total == 3) {
                return (baseT, baseCT);
            }

            if (threePlayerHalftimeMode && bombsites == 1 && total == 3 && gs.roundNumber > HALF_ROUNDS) {
                return (1, 2);
            }

            if (!isOdd || !lastRoundFirstHalf) {
                return (baseT, baseCT);
            }

            if (bombsites == 2) {
                if (baseCT > baseT) {
                    baseCT--;
                    baseT++;
                }
            } else {
                if (baseT > baseCT) {
                    baseT--;
                    baseCT++;
                }
            }

            return (baseT, baseCT);
        }

        private void EvenTeamSizesWarmup(GameStats gs, TeamStats tStats, TeamStats cStats, bool moveFromT, int moves, string reason) {
            for (int i = 0; i < moves; i++) {
                var srcTeam = moveFromT ? tStats : cStats;
                int bestUser = -1;
                float bestDelta = float.MaxValue;

                float tAvg = TeamWarmupAverage90d(gs, tStats);
                float cAvg = TeamWarmupAverage90d(gs, cStats);
                float targetDeltaPer = MathF.Abs(tAvg - cAvg) / Math.Max(1, moves - i);

                foreach (var kv in srcTeam.playerList) {
                    int uid = kv.Key;
                    var ps = kv.Value;
                    float sig = WarmupSignalForPlayer(gs, uid, ps);
                    float diff = MathF.Abs(sig - targetDeltaPer);

                    if (diff < bestDelta) {
                        bestDelta = diff;
                        bestUser = uid;
                    }
                }

                if (bestUser == -1) break;

                var player = Utilities.GetPlayerFromUserid(bestUser);
                if (player == null || !player.UserId.HasValue) break;

                int toTeam = moveFromT ? TEAM_CT : TEAM_T;

                Console.WriteLine($"[INFO] OSBase[{ModuleName}] Warmup size-move plan ({reason}): {player.PlayerName}({bestUser}) {TeamName(player.TeamNum)} -> {TeamName(toTeam)}");
                RawMove(gs, player, toTeam, announce: true, reason: reason);

                tStats = gs.getTeam(TEAM_T);
                cStats = gs.getTeam(TEAM_CT);
            }
        }

        private void EvenTeamSizesLive(GameStats gs, TeamStats tStats, TeamStats cStats, bool moveFromT, int moves, string reason) {
            for (int i = 0; i < moves; i++) {
                var srcTeam = moveFromT ? tStats : cStats;
                if (srcTeam.playerList.Count == 0) break;

                int bestUser = -1;
                float bestScore = float.MaxValue;

                float tBaseSum = SumTeamSignal(gs, tStats);
                float cBaseSum = SumTeamSignal(gs, cStats);
                int tBaseN = tStats.numPlayers();
                int cBaseN = cStats.numPlayers();

                foreach (var kv in srcTeam.playerList) {
                    int uid = kv.Key;
                    var ps = kv.Value;

                    float sig = SignalSkill(gs, uid, ps);
                    float tSum = tBaseSum;
                    float cSum = cBaseSum;
                    int tn = tBaseN;
                    int cn = cBaseN;

                    if (moveFromT) {
                        tSum -= sig;
                        tn--;
                        cSum += sig;
                        cn++;
                    } else {
                        cSum -= sig;
                        cn--;
                        tSum += sig;
                        tn++;
                    }

                    float newGap = MathF.Abs((tn > 0 ? tSum / tn : 0f) - (cn > 0 ? cSum / cn : 0f));
                    if (newGap < bestScore) {
                        bestScore = newGap;
                        bestUser = uid;
                    }
                }

                if (bestUser == -1) {
                    foreach (var kv in srcTeam.playerList) {
                        bestUser = kv.Key;
                        break;
                    }

                    if (bestUser == -1) break;
                }

                var player = Utilities.GetPlayerFromUserid(bestUser);
                if (player == null || !player.UserId.HasValue) break;

                int toTeam = moveFromT ? TEAM_CT : TEAM_T;

                Console.WriteLine($"[INFO] OSBase[{ModuleName}] Live size-move plan ({reason}): {player.PlayerName}({bestUser}) {TeamName(player.TeamNum)} -> {TeamName(toTeam)}");
                MoveWithImmunity(gs, player, toTeam, announce: true, reason: reason);
                playerSwapRound[player.UserId.Value] = gs.roundNumber;

                tStats = gs.getTeam(TEAM_T);
                cStats = gs.getTeam(TEAM_CT);
            }
        }

        private bool FindBestSwapPairWithGain_Warmup(GameStats gs, TeamStats tStats, TeamStats cStats, out int uA, out int uB, out float bestGain) {
            return FindBestSwapPairCore(gs, tStats, cStats, true, out uA, out uB, out bestGain);
        }

        private bool FindBestSwapPairWithGain_Live(GameStats gs, TeamStats tStats, TeamStats cStats, out int uA, out int uB, out float bestGain) {
            return FindBestSwapPairCore(gs, tStats, cStats, false, out uA, out uB, out bestGain);
        }

        private bool FindBestSwapPairCore(GameStats gs, TeamStats tStats, TeamStats cStats, bool useWarmupSignals, out int uA, out int uB, out float bestGain) {
            uA = -1;
            uB = -1;
            bestGain = 0f;

            var all = new List<(int uid, float sig, bool isT)>();

            foreach (var kv in tStats.playerList) {
                float sig = useWarmupSignals ? WarmupSignalForPlayer(gs, kv.Key, kv.Value) : SignalSkill(gs, kv.Key, kv.Value);
                all.Add((kv.Key, sig, true));
            }

            foreach (var kv in cStats.playerList) {
                float sig = useWarmupSignals ? WarmupSignalForPlayer(gs, kv.Key, kv.Value) : SignalSkill(gs, kv.Key, kv.Value);
                all.Add((kv.Key, sig, false));
            }

            if (all.Count < 2 || tStats.numPlayers() == 0 || cStats.numPlayers() == 0) {
                return false;
            }

            float baseScore = ScoreState(all);
            float bestScore = float.MaxValue;

            var sorted = all.OrderByDescending(x => x.sig).ToList();
            int quart = Math.Max(1, sorted.Count / 4);
            var strongSet = new HashSet<int>(sorted.Take(quart).Select(x => x.uid));
            var weakSet = new HashSet<int>(sorted.Skip(Math.Max(0, sorted.Count - quart)).Select(x => x.uid));

            foreach (var s in all) {
                if (!useWarmupSignals && PlayerOnCooldown(s.uid, gs.roundNumber)) continue;

                foreach (var w in all) {
                    if (s.isT == w.isT) continue;
                    if (!useWarmupSignals && PlayerOnCooldown(w.uid, gs.roundNumber)) continue;

                    float score = ScoreStateSwapSim(all, s.uid, w.uid, strongSet, weakSet);
                    if (score < bestScore) {
                        bestScore = score;
                        uA = s.uid;
                        uB = w.uid;
                        bestGain = baseScore - score;
                    }
                }
            }

            return (uA != -1 && uB != -1);
        }

        private float ScoreState(List<(int uid, float sig, bool isT)> all) {
            float tSum = 0f;
            float cSum = 0f;
            int tn = 0;
            int cn = 0;

            foreach (var x in all) {
                if (x.isT) {
                    tSum += x.sig;
                    tn++;
                } else {
                    cSum += x.sig;
                    cn++;
                }
            }

            float meanGap = MathF.Abs((tn > 0 ? tSum / tn : 0f) - (cn > 0 ? cSum / cn : 0f));

            var sorted = all.OrderByDescending(x => x.sig).ToList();
            int quart = Math.Max(1, sorted.Count / 4);
            var strongSet = new HashSet<int>(sorted.Take(quart).Select(x => x.uid));
            var weakSet = new HashSet<int>(sorted.Skip(Math.Max(0, sorted.Count - quart)).Select(x => x.uid));

            int strongT = 0;
            int strongCT = 0;
            int weakT = 0;
            int weakCT = 0;

            foreach (var x in all) {
                if (strongSet.Contains(x.uid)) {
                    if (x.isT) strongT++;
                    else strongCT++;
                }

                if (weakSet.Contains(x.uid)) {
                    if (x.isT) weakT++;
                    else weakCT++;
                }
            }

            float compPenalty = 300f * (MathF.Abs(strongT - strongCT) + MathF.Abs(weakT - weakCT));
            return meanGap + compPenalty;
        }

        private float ScoreStateSwapSim(List<(int uid, float sig, bool isT)> all, int uidA, int uidB, HashSet<int> strongSet, HashSet<int> weakSet) {
            float tSum = 0f;
            float cSum = 0f;
            int tn = 0;
            int cn = 0;
            int strongT = 0;
            int strongCT = 0;
            int weakT = 0;
            int weakCT = 0;

            foreach (var x in all) {
                bool isT = x.isT;
                if (x.uid == uidA) isT = !isT;
                if (x.uid == uidB) isT = !isT;

                if (isT) {
                    tSum += x.sig;
                    tn++;
                } else {
                    cSum += x.sig;
                    cn++;
                }

                bool isStrong = strongSet.Contains(x.uid);
                bool isWeak = weakSet.Contains(x.uid);

                if (isStrong) {
                    if (isT) strongT++;
                    else strongCT++;
                }

                if (isWeak) {
                    if (isT) weakT++;
                    else weakCT++;
                }
            }

            float meanGap = MathF.Abs((tn > 0 ? tSum / tn : 0f) - (cn > 0 ? cSum / cn : 0f));
            float compPenalty = 300f * (MathF.Abs(strongT - strongCT) + MathF.Abs(weakT - weakCT));
            return meanGap + compPenalty;
        }

        private float WarmupSignalForPlayer(GameStats gs, int userId, PlayerStats ps) {
            return SkillResolver.GetWarmupSignal(gs, userId, ps);
        }

        private float TeamWarmupAverage90d(GameStats gs, TeamStats team) {
            if (team.playerList.Count == 0) return 0f;

            double sum = 0d;
            foreach (var kv in team.playerList) {
                sum += WarmupSignalForPlayer(gs, kv.Key, kv.Value);
            }

            return (float)(sum / team.playerList.Count);
        }

        private float TeamSignalAverage(GameStats gs, TeamStats team) {
            if (team.playerList.Count == 0) return 0f;

            double sum = 0d;
            foreach (var kv in team.playerList) {
                sum += SignalSkill(gs, kv.Key, kv.Value);
            }

            return (float)(sum / team.playerList.Count);
        }

        private float SumTeamSignal(GameStats gs, TeamStats team) {
            double sum = 0d;
            foreach (var kv in team.playerList) {
                sum += SignalSkill(gs, kv.Key, kv.Value);
            }

            return (float)sum;
        }

        private float SignalSkill(GameStats gs, int userId, PlayerStats ps) {
            return SkillResolver.GetEffectiveSkill(gs, userId, ps);
        }

        private static string TeamName(int t) {
            return t == TEAM_T ? "T" : t == TEAM_CT ? "CT" : "SPEC";
        }

        private void AnnounceMove(CCSPlayerController p, int fromTeam, int toTeam) {
            try {
                Server.PrintToChatAll($"[TeamBalancer] {p.PlayerName} {TeamName(fromTeam)} → {TeamName(toTeam)}");
            } catch {
            }
        }

        private void AnnounceSwap(CCSPlayerController a, CCSPlayerController b) {
            try {
                Server.PrintToChatAll($"[TeamBalancer] Swap: {a.PlayerName} ↔ {b.PlayerName}");
            } catch {
            }
        }

        private void SlayIfWarmup(GameStats gs, CCSPlayerController player) {
            if (!IsWarmup(gs)) return;
            if (player == null || !player.IsValid) return;
            if (!player.PawnIsAlive) return;

            player.CommitSuicide(false, false);
        }

        private void RawMove(GameStats gs, CCSPlayerController player, int targetTeam, bool announce = true, string reason = "") {
            if (player == null || !player.UserId.HasValue) return;

            int from = player.TeamNum;

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] MOVE phase={PhaseName(gs)} reason={reason} player={player.PlayerName}({player.UserId.Value}) from={TeamName(from)} to={TeamName(targetTeam)}");
            player.SwitchTeam((CsTeam)targetTeam);
            gs.movePlayer(player.UserId.Value, targetTeam);

            SlayIfWarmup(gs, player);

            if (announce) {
                AnnounceMove(player, from, targetTeam);
            }
        }

        private void MoveWithImmunity(GameStats gs, CCSPlayerController player, int targetTeam, bool announce = true, string reason = "") {
            if (player == null || !player.UserId.HasValue) return;

            int from = player.TeamNum;

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] MOVE+IMM phase={PhaseName(gs)} reason={reason} player={player.PlayerName}({player.UserId.Value}) from={TeamName(from)} to={TeamName(targetTeam)}");
            player.SwitchTeam((CsTeam)targetTeam);
            gs.movePlayer(player.UserId.Value, targetTeam);

            var ps = gs.GetPlayerStats(player.UserId.Value);
            ps.immune += 3;

            SlayIfWarmup(gs, player);

            if (announce) {
                AnnounceMove(player, from, targetTeam);
            }
        }
    }
}