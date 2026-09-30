using System;
using System.Linq;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using OSBase.Helpers;

namespace OSBase.Modules {

    public class TeamBalancer : ModuleBase {
        public override string ModuleName => "teambalancer";
        public string ModuleNameNice => "TeamBalancer";

        private CounterStrikeSharp.API.Modules.Timers.Timer? warmupBalanceTimer;
        private GameStats? gameStats;
        private EloRating? eloRating;

        // Teams
        private const int TEAM_S = (int)CsTeam.Spectator;
        private const int TEAM_T = (int)CsTeam.Terrorist;
        private const int TEAM_CT = (int)CsTeam.CounterTerrorist;

        // Map bombsite info
        private readonly Dictionary<string, int> mapBombsites = new();
        private const string mapConfigFile = "teambalancer_mapinfo.cfg";
        private int bombsites = 2;
        private string currentMap = "";

        // cfg (teambalancer.cfg) -- min_rated_matches: below this many rated Elo duels, a
        // player's own rating is too noisy to trust for balancing and they're treated as the
        // roster median instead (see SkillResolver.GetEffectiveSkill). Config, not a constant,
        // for the same reason ask 11's min_players is: nobody knows the right number until
        // real rating data exists, and changing it shouldn't need a rebuild.
        private int minRatedMatches = 10;

        // Elo-mode decision model (osbase-order-2026Q4.md section 13, replaces the threshold/
        // spread heuristics below for balancer_skill_source elo):
        //   strength = R + min(rounds / R0, CAP) * (P - R)
        //   P        = opponents' average rating + 400 * log10((kills + 0.5) / (deaths + 0.5))
        //   win%     = 1 / (1 + 10^(-(A - B) / 400)) for the stronger team's average
        // Balance only when the stronger side's win chance exceeds balance_trigger_pct, and
        // only with a swap that brings it under balance_target_pct -- a swap that merely
        // flips which team is too strong is not made. At most one swap per
        // min_rounds_between_moves rounds, never the same player twice on a map. All config
        // so the owner can retune after a week without a rebuild.
        private double balanceTriggerPct = 60.0;
        private double balanceTargetPct = 55.0;
        private int mapWeightRounds = 20;
        private double mapWeightCap = 0.3;
        private int minRoundsBetweenMoves = 3;

        // userIds moved by any balance action on this map (swap or size-fix) -- order section
        // 13: "Samma spelare flyttas aldrig två gånger på samma mapp."
        private readonly HashSet<int> movedThisMap = new();

        // balancer_skill_source: gamestats (default) | elo | shadow. Elo doesn't replace
        // GameStats' skill signal the moment this code ships -- GameStats.calcSkill()/
        // skill_log keeps writing regardless (SaveIfEligible is self-contained, triggered by
        // its own round/map-end hooks, not by anything reading calcSkill() -- verified, not
        // assumed), and the plan is to let ratings warm up for a season before the actual
        // balancing decision moves. "shadow" balances on GameStats as always but also
        // computes what Elo would have shown for the same teams and logs the difference plus
        // how many of the roster have cleared min_rated_matches -- makes "are the ratings
        // ready" an observation instead of a guessed cutover date. Overlap between GameStats
        // and player_daily_stat.rating (ask 22, STATS-MODULE.md) is the form curve's only
        // control period -- once GameStats stops, those days don't come back.
        private string balancerSkillSource = "gamestats";

        // Elo can feed the skill signal (see SkillResolver.cs and ELO-MODULE.md), selected by
        // balancerSkillSource above; gameStats itself stays regardless -- it's still the
        // team/round-tracking substrate (getTeam, movePlayer, roundNumber, immune), that part
        // was never being retired. currentRosterMedian/currentRosterSpread are recomputed once
        // per balance pass (RefreshRosterStats) rather than per player-lookup -- cheap either
        // way at CS2 headcounts, but there's no reason to rescan the roster per lookup.
        private const float FallbackSkill = 1000f; // matches EloRating's start_rating default
        private float currentRosterMedian = FallbackSkill;
        private float currentRosterSpread = MIN_ROSTER_SPREAD;

        // Warmup policy
        // Two scales, selected by balancer_skill_source, not one replacing the other. In
        // "gamestats"/"shadow" mode the gap being compared is on GameStats' own ~4000-11000
        // skill scale -- the ORIGINAL literal constants below are already correct for that
        // and need no conversion. Only in "elo" mode does the gap come from Elo's much
        // narrower scale, where the same literal numbers would make swaps almost never
        // trigger -- silently near-inert, not just miscalibrated. The *_RATIO constants
        // preserve each original constant's fraction of GameStats' typical spread (~7000,
        // not re-guessed for Elo's still-unknown distribution -- that would just be guessing
        // again), multiplied by the roster's actual current Elo spread (see
        // SkillResolver.ComputeRosterSpread) only when that's the scale actually in play.
        // Revisit the ratios themselves once real Elo spread data exists.
        private const float LegacySpreadReference = 7000f;
        private const float WARMUP_TARGET_DEVIATION_LEGACY = 1500f;
        private const float WARMUP_TARGET_DEVIATION_RATIO = WARMUP_TARGET_DEVIATION_LEGACY / LegacySpreadReference;
        private const float WARMUP_BURST_AT = 57.0f; // 60s warmup -> run with ~3s left
        private const int WARMUP_FINAL_MAX_SWAPS = 10;
        private bool warmupBalancedThisMap = false;

        private float WARMUP_TARGET_DEVIATION => balancerSkillSource == "elo"
            ? WARMUP_TARGET_DEVIATION_RATIO * currentRosterSpread
            : WARMUP_TARGET_DEVIATION_LEGACY;

        // Round 1 safety-net
        private bool firstRoundSizeFixDone = false;

        // Game structure
        private const int HALF_ROUNDS = 10;
        private const int MAX_ROUNDS = 20;

        // Swap thresholds
        private const float MID_SWAP_THRESHOLD_LEGACY = 1500f;
        private const float LATE_SWAP_THRESHOLD_LEGACY = 1900f;
        private const float LATE_HYSTERESIS_LEGACY = 700f;
        private const float MIN_PROJECTED_GAIN_LEGACY = 800f;
        private const float MID_SWAP_THRESHOLD_RATIO = MID_SWAP_THRESHOLD_LEGACY / LegacySpreadReference;
        private const float LATE_SWAP_THRESHOLD_RATIO = LATE_SWAP_THRESHOLD_LEGACY / LegacySpreadReference;
        private const float LATE_HYSTERESIS_RATIO = LATE_HYSTERESIS_LEGACY / LegacySpreadReference;
        private const float MIN_PROJECTED_GAIN_RATIO = MIN_PROJECTED_GAIN_LEGACY / LegacySpreadReference;

        private float MID_SWAP_THRESHOLD => balancerSkillSource == "elo"
            ? MID_SWAP_THRESHOLD_RATIO * currentRosterSpread : MID_SWAP_THRESHOLD_LEGACY;
        private float LATE_SWAP_THRESHOLD => balancerSkillSource == "elo"
            ? LATE_SWAP_THRESHOLD_RATIO * currentRosterSpread : LATE_SWAP_THRESHOLD_LEGACY;
        private float LATE_HYSTERESIS => balancerSkillSource == "elo"
            ? LATE_HYSTERESIS_RATIO * currentRosterSpread : LATE_HYSTERESIS_LEGACY;
        private float MIN_PROJECTED_GAIN => balancerSkillSource == "elo"
            ? MIN_PROJECTED_GAIN_RATIO * currentRosterSpread : MIN_PROJECTED_GAIN_LEGACY;

        // Anti-churn
        private const int MIN_ROUNDS_BETWEEN_SWAPS = 3;
        private const int NO_SWAP_LAST_N_ROUNDS = 3;
        private const int MAX_LATE_SWAPS = 1;
        private const int MAX_SWAPS_PER_MAP = 3;
        private const float EMERGENCY_GAP_LEGACY = 3500f;
        private const float COMP_PENALTY_LEGACY = 300f;
        private const float EMERGENCY_GAP_RATIO = EMERGENCY_GAP_LEGACY / LegacySpreadReference;
        private const float COMP_PENALTY_RATIO = COMP_PENALTY_LEGACY / LegacySpreadReference;
        private const float MIN_ROSTER_SPREAD = 50f;

        private float EMERGENCY_GAP => balancerSkillSource == "elo"
            ? EMERGENCY_GAP_RATIO * currentRosterSpread : EMERGENCY_GAP_LEGACY;
        private float CompPenaltyScale => balancerSkillSource == "elo"
            ? COMP_PENALTY_RATIO * currentRosterSpread : COMP_PENALTY_LEGACY;

        // Unconditionally Elo-scale, regardless of balancer_skill_source -- exist only so
        // LogShadowSkillComparison has something on the right scale to print next to elo_gap.
        // The *_LEGACY properties above are branch-correct for real decisions (literal in
        // gamestats/shadow, ratio*spread only in elo), which means in shadow mode they report
        // the GameStats-scale numbers -- exactly the wrong thing to compare an Elo-scale gap
        // against. Same category of bug as the one just fixed in the decision path, caught
        // one level in: a shadow log nobody can trust is worse than no shadow log, because it
        // still looks like data.
        private float EloWarmupTargetDeviation => WARMUP_TARGET_DEVIATION_RATIO * currentRosterSpread;
        private float EloMidSwapThreshold => MID_SWAP_THRESHOLD_RATIO * currentRosterSpread;
        private float EloLateSwapThreshold => LATE_SWAP_THRESHOLD_RATIO * currentRosterSpread;
        private float EloLateHysteresis => LATE_HYSTERESIS_RATIO * currentRosterSpread;
        private float EloMinProjectedGain => MIN_PROJECTED_GAIN_RATIO * currentRosterSpread;
        private float EloEmergencyGap => EMERGENCY_GAP_RATIO * currentRosterSpread;
        private float EloCompPenaltyScale => COMP_PENALTY_RATIO * currentRosterSpread;

        private int lastSwapRound = -999;
        private int lateSwapsThisHalf = 0;
        private int swapsThisMap = 0;
        private int currentHalfIndex = 0; // 0 first half, 1 second half

        // Per-player cooldown
        private readonly Dictionary<int, int> playerSwapRound = new();

        // 3-player halftime latch
        private bool threePlayerHalftimeMode = false;

        protected override void OnLoad() {
            gameStats = osbase?.GetGameStats();
            eloRating = osbase?.GetModule<EloRating>();
            CreateCustomConfigs();
            LoadConfig();
            LoadMapInfo();
        }

        protected override void OnUnload() {
            warmupBalanceTimer?.Kill();
            warmupBalanceTimer = null;

            gameStats = null;
            eloRating = null;

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
        }

        protected override void OnReloadConfig() {
            gameStats = osbase?.GetGameStats();
            eloRating = osbase?.GetModule<EloRating>();
            CreateCustomConfigs();
            LoadConfig();
            LoadMapInfo();
        }

        private void CreateCustomConfigs() {
            config?.CreateCustomConfig(
                $"{ModuleName}.cfg",
                "// TeamBalancer Configuration\n" +
                "// balancer_skill_source: gamestats (default) | elo | shadow.\n" +
                "// gamestats: unchanged behaviour, GameStats.calcSkill() drives balancing.\n" +
                "// elo: EloRating.rating drives balancing instead.\n" +
                "// shadow: balances on gamestats as usual, but also computes and logs what\n" +
                "//   elo would have shown for the same teams, plus how many of the roster\n" +
                "//   have cleared min_rated_matches -- use this to observe whether ratings\n" +
                "//   are ready before actually switching to elo.\n" +
                "balancer_skill_source gamestats\n" +
                "// Below this many rated Elo duels, a player's own rating is too noisy to\n" +
                "// trust for balancing -- treated as the roster median instead (see\n" +
                "// SkillResolver.GetEffectiveSkill). Nobody knows the right number until real\n" +
                "// rating data exists.\n" +
                "min_rated_matches 10\n" +
                "// Elo-mode balancing (osbase-order-2026Q4.md section 13). Start values.\n" +
                "// Balance only when the stronger team's win chance exceeds trigger, and only\n" +
                "// with a swap that brings it under target.\n" +
                "balance_trigger_pct 60\n" +
                "balance_target_pct 55\n" +
                "// The current map's kills/deaths weigh in by min(rounds / map_weight_rounds,\n" +
                "// map_weight_cap): ~8% after 5 rounds, never more than 30%.\n" +
                "map_weight_rounds 20\n" +
                "map_weight_cap 0.3\n" +
                "min_rounds_between_moves 3\n"
            );
        }

        private void LoadConfig() {
            minRatedMatches = 10;
            balancerSkillSource = "gamestats";
            balanceTriggerPct = 60.0;
            balanceTargetPct = 55.0;
            mapWeightRounds = 20;
            mapWeightCap = 0.3;
            minRoundsBetweenMoves = 3;

            List<string> cfg = config?.FetchCustomConfig($"{ModuleName}.cfg") ?? new List<string>();

            foreach (var rawLine in cfg) {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) {
                    continue;
                }

                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) {
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Invalid config line skipped: {line}");
                    continue;
                }

                string key = parts[0].Trim();
                string value = parts[1].Trim();

                switch (key.ToLowerInvariant()) {
                    case "min_rated_matches":
                        if (int.TryParse(value, out int parsed)) {
                            minRatedMatches = Math.Clamp(parsed, 0, 10000);
                        }
                        break;
                    case "balance_trigger_pct":
                        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double trig)) {
                            balanceTriggerPct = Math.Clamp(trig, 50.0, 100.0);
                        }
                        break;
                    case "balance_target_pct":
                        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double tgt)) {
                            balanceTargetPct = Math.Clamp(tgt, 50.0, 100.0);
                        }
                        break;
                    case "map_weight_rounds":
                        if (int.TryParse(value, out int r0)) {
                            mapWeightRounds = Math.Clamp(r0, 1, 1000);
                        }
                        break;
                    case "map_weight_cap":
                        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double cap)) {
                            mapWeightCap = Math.Clamp(cap, 0.0, 1.0);
                        }
                        break;
                    case "min_rounds_between_moves":
                        if (int.TryParse(value, out int mrb)) {
                            minRoundsBetweenMoves = Math.Clamp(mrb, 0, 100);
                        }
                        break;
                    case "balancer_skill_source":
                        string src = value.Trim().ToLowerInvariant();
                        if (src == "gamestats" || src == "elo" || src == "shadow") {
                            balancerSkillSource = src;
                        } else {
                            Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Invalid balancer_skill_source '{value}' -- defaulting to gamestats.");
                            balancerSkillSource = "gamestats";
                        }
                        break;
                    default:
                        Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Unknown config key {key}:{value}");
                        break;
                }
            }
        }

        // Recomputed at the top of every balance pass (not cached longer than that) so
        // WARMUP_TARGET_DEVIATION/MID_SWAP_THRESHOLD/etc above -- all properties reading
        // currentRosterSpread -- reflect who's actually connected right now.
        private void RefreshRosterStats() {
            var userIds = Utilities.GetPlayers()
                .Where(p => IsHumanPlayer(p) && IsPlayingTeam(p))
                .Select(p => p!.UserId!.Value)
                .ToList();

            currentRosterMedian = SkillResolver.ComputeRosterMedian(eloRating, userIds, minRatedMatches, FallbackSkill);
            currentRosterSpread = SkillResolver.ComputeRosterSpread(eloRating, userIds, minRatedMatches, MIN_ROSTER_SPREAD);

            // The ratio->absolute conversion is a first cut, not verified against real Elo
            // data (none exists yet) -- logging the actual computed thresholds every pass is
            // the only way to calibrate them later. Without this, a silently near-inert
            // balancer looks identical to a working one until someone notices teams feel off.
            // These are the thresholds actually governing decisions in the active
            // balancer_skill_source -- legacy literal values in gamestats/shadow (correct as-is
            // for that scale), ratio*spread only in elo mode. currentRosterMedian/Spread
            // themselves are always the Elo-side numbers (needed by shadow's comparison log
            // regardless of mode), which is why they're worth logging here even outside elo mode.
            Console.WriteLine(
                $"[DEBUG] OSBase[{ModuleName}] roster stats (source={balancerSkillSource}): " +
                $"elo_median={currentRosterMedian:0} elo_spread={currentRosterSpread:0} | " +
                $"active thresholds: warmup_target={WARMUP_TARGET_DEVIATION:0} mid_swap={MID_SWAP_THRESHOLD:0} " +
                $"late_swap={LATE_SWAP_THRESHOLD:0} late_hysteresis={LATE_HYSTERESIS:0} " +
                $"min_gain={MIN_PROJECTED_GAIN:0} emergency_gap={EMERGENCY_GAP:0} comp_penalty_scale={CompPenaltyScale:0}"
            );
        }

        // balancer_skill_source=shadow only: the real decision this pass used GameStats (the
        // gameStatsTAvg/gameStatsCAvg the caller already computed for that purpose -- no
        // second GameStats computation here). This adds the one number GameStats mode doesn't
        // otherwise produce: what Elo would have shown for the exact same two teams right now,
        // plus how many of them have cleared min_rated_matches. Cheap -- both source values are
        // already in memory, this is just a second average over a roster of a handful of
        // players, not a second swap search.
        private void LogShadowSkillComparison(TeamStats tStats, TeamStats cStats, float gameStatsTAvg, float gameStatsCAvg, string source) {
            if (balancerSkillSource != "shadow") {
                return;
            }

            float eloTAvg = ComputeEloTeamAverage(tStats);
            float eloCAvg = ComputeEloTeamAverage(cStats);
            float gameStatsGap = MathF.Abs(gameStatsTAvg - gameStatsCAvg);
            float eloGap = MathF.Abs(eloTAvg - eloCAvg);

            var allUserIds = tStats.playerList.Keys.Concat(cStats.playerList.Keys).ToList();
            int ready = 0;
            foreach (int uid in allUserIds) {
                var p = Utilities.GetPlayerFromUserid(uid);
                if (p != null && p.IsValid && eloRating != null && eloRating.TryGetRating(p.SteamID, out _, out int matches) && matches >= minRatedMatches) {
                    ready++;
                }
            }

            // elo_gap is meaningless without something on the same scale to compare it
            // against -- print the would-be Elo thresholds here, not the active ones
            // (WARMUP_TARGET_DEVIATION etc. are GameStats-scale literals in shadow mode,
            // since that's what's actually deciding; comparing elo_gap against those would
            // reproduce the exact bug this whole property split exists to avoid).
            Console.WriteLine(
                $"[INFO] OSBase[{ModuleName}] shadow ({source}): " +
                $"gamestats_gap={gameStatsGap:0} (T={gameStatsTAvg:0} CT={gameStatsCAvg:0}) | " +
                $"elo_gap={eloGap:0} (T={eloTAvg:0} CT={eloCAvg:0}) | " +
                $"elo thresholds (would-be, not active): warmup_target={EloWarmupTargetDeviation:0} " +
                $"mid_swap={EloMidSwapThreshold:0} late_swap={EloLateSwapThreshold:0} " +
                $"late_hysteresis={EloLateHysteresis:0} min_gain={EloMinProjectedGain:0} " +
                $"emergency_gap={EloEmergencyGap:0} comp_penalty_scale={EloCompPenaltyScale:0} | " +
                $"rated_ready={ready}/{allUserIds.Count} (min_rated_matches={minRatedMatches})"
            );
        }

        private float ComputeEloTeamAverage(TeamStats team) {
            if (team.playerList.Count == 0) {
                return 0f;
            }

            double sum = 0d;
            foreach (var kv in team.playerList) {
                sum += SkillResolver.GetEffectiveSkill(eloRating, kv.Key, currentRosterMedian, minRatedMatches);
            }

            return (float)(sum / team.playerList.Count);
        }

        protected override void RegisterHandlers() {
            osbase?.RegisterListener<Listeners.OnMapStart>(OnMapStart);
            osbase?.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

            // Use new EventBus system (no HookMode support yet, Post hook will be handled later)
            osbase?.SubscribeToEvent<EventWarmupEnd>(OnWarmupEnd);
            osbase?.SubscribeToEvent<EventRoundEnd>(OnRoundEnd);
            osbase?.SubscribeToEvent<EventStartHalftime>(OnStartHalftime);
            osbase?.SubscribeToEvent<EventRoundPrestart>(OnRoundPrestart);
        }

        protected override void UnregisterHandlers() {
            osbase?.RemoveListener<Listeners.OnMapStart>(OnMapStart);
            osbase?.RemoveListener<Listeners.OnMapEnd>(OnMapEnd);

            // Use new EventBus system
            osbase?.UnsubscribeFromEvent<EventWarmupEnd>(OnWarmupEnd);
            osbase?.UnsubscribeFromEvent<EventRoundEnd>(OnRoundEnd);
            osbase?.UnsubscribeFromEvent<EventStartHalftime>(OnStartHalftime);
            osbase?.UnsubscribeFromEvent<EventRoundPrestart>(OnRoundPrestart);
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
            movedThisMap.Clear();

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
            // Called ad hoc (e.g. from WeaponRestrict), not necessarily during a balance
            // pass -- refresh so the median/spread aren't stale from whenever the last one ran.
            RefreshRosterStats();

            if (balancerSkillSource == "elo") {
                return SkillResolver.GetEffectiveSkillForPlayer(eloRating, player, currentRosterMedian, minRatedMatches);
            }

            return SkillResolver.GetEffectiveSkillForPlayer(gameStats, player);
        }

        public float GetEffectiveSkillForPriority(int userId) {
            RefreshRosterStats();

            if (balancerSkillSource == "elo") {
                return SkillResolver.GetEffectiveSkill(eloRating, userId, currentRosterMedian, minRatedMatches);
            }

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

        private HookResult OnWarmupEnd(EventWarmupEnd ev) {
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

        private HookResult OnStartHalftime(EventStartHalftime ev) {
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

        private HookResult OnRoundEnd(EventRoundEnd ev) {
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

        private HookResult OnRoundPrestart(EventRoundPrestart ev) {
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
            RefreshRosterStats();

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
            RefreshRosterStats();

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

            // With exactly 2 humans, split them across teams and let it settle this round.
            if (EnsureHumansSplit(gs)) {
                return;
            }

            // Skill swaps below need 2+ humans.
            if (SkipBalanceDueToTooFewHumans("warmup_final_skill")) {
                return;
            }

            if (total < 4) {
                EnsureBestIsSoloIf2v1(gs);
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] WarmupFinalBalance DONE total<{4}. skill_swaps_skipped=true");
                return;
            }

            LogShadowSkillComparison(tStats, cStats, TeamWarmupAverage90d(gs, tStats), TeamWarmupAverage90d(gs, cStats), "warmup_final");

            if (balancerSkillSource == "elo") {
                int eloSwaps = EloBalanceSwaps(gs, "warmup_final", WARMUP_FINAL_MAX_SWAPS, enforceHysteresis: false);
                EnsureBestIsSoloIf2v1(gs);
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] WarmupFinalBalance DONE swaps={eloSwaps} (elo)");
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
            RefreshRosterStats();

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

            // With exactly 2 humans, split them across teams and let it settle this round.
            if (EnsureHumansSplit(gs)) {
                return;
            }

            // Skill swaps below need 2+ humans.
            if (SkipBalanceDueToTooFewHumans("roundend_skill")) {
                return;
            }

            if (total < 4) {
                EnsureBestIsSoloIf2v1(gs);
                return;
            }

            if (balancerSkillSource == "elo") {
                EloBalanceSwaps(gs, "roundend_live", 1, enforceHysteresis: true);
                EnsureBestIsSoloIf2v1(gs);
                return;
            }

            float tAvg = TeamSignalAverage(gs, tStats);
            float cAvg = TeamSignalAverage(gs, cStats);
            float gap = MathF.Abs(tAvg - cAvg);

            LogShadowSkillComparison(tStats, cStats, tAvg, cAvg, "roundend_live");

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

        // ----- Elo-mode balancing (order section 13) -----

        // Per-player strength for this pass. R comes from SkillResolver (this season once past
        // the provisional gate, else last season's final, else the roster median); the
        // current map's performance rating P is blended in by rounds played here, capped.
        private double StrengthOf(int userId, PlayerStats ps, double opponentsAverageRating) {
            double r = SkillResolver.GetEffectiveSkill(eloRating, userId, currentRosterMedian, minRatedMatches);
            int rounds = ps.rounds;
            if (rounds <= 0 || mapWeightRounds <= 0 || mapWeightCap <= 0) {
                return r;
            }

            double performance = opponentsAverageRating + 400.0 * Math.Log10((ps.kills + 0.5) / (ps.deaths + 0.5));
            double w = Math.Min(rounds / (double)mapWeightRounds, mapWeightCap);
            return r + w * (performance - r);
        }

        private static double WinChance(double strongerAverage, double weakerAverage) {
            return 1.0 / (1.0 + Math.Pow(10.0, -(strongerAverage - weakerAverage) / 400.0));
        }

        private List<(int uid, double strength, bool isT, bool alive)> EloRoster(GameStats gs, TeamStats tStats, TeamStats cStats) {
            double tRating = 0, cRating = 0;
            foreach (var kv in tStats.playerList) tRating += SkillResolver.GetEffectiveSkill(eloRating, kv.Key, currentRosterMedian, minRatedMatches);
            foreach (var kv in cStats.playerList) cRating += SkillResolver.GetEffectiveSkill(eloRating, kv.Key, currentRosterMedian, minRatedMatches);
            double tAvgRating = tStats.numPlayers() > 0 ? tRating / tStats.numPlayers() : currentRosterMedian;
            double cAvgRating = cStats.numPlayers() > 0 ? cRating / cStats.numPlayers() : currentRosterMedian;

            var all = new List<(int uid, double strength, bool isT, bool alive)>();
            foreach (var kv in tStats.playerList) {
                var p = Utilities.GetPlayerFromUserid(kv.Key);
                all.Add((kv.Key, StrengthOf(kv.Key, kv.Value, cAvgRating), true, p?.PawnIsAlive ?? false));
            }
            foreach (var kv in cStats.playerList) {
                var p = Utilities.GetPlayerFromUserid(kv.Key);
                all.Add((kv.Key, StrengthOf(kv.Key, kv.Value, tAvgRating), false, p?.PawnIsAlive ?? false));
            }
            return all;
        }

        // Stronger side's win chance for a roster, optionally with two players' sides flipped.
        private static (double chance, double tAvg, double cAvg) Evaluate(List<(int uid, double strength, bool isT, bool alive)> all, int flipA = -1, int flipB = -1) {
            double tSum = 0, cSum = 0;
            int tn = 0, cn = 0;
            foreach (var x in all) {
                bool isT = x.isT;
                if (x.uid == flipA || x.uid == flipB) isT = !isT;
                if (isT) { tSum += x.strength; tn++; } else { cSum += x.strength; cn++; }
            }
            double tAvg = tn > 0 ? tSum / tn : 0;
            double cAvg = cn > 0 ? cSum / cn : 0;
            return (WinChance(Math.Max(tAvg, cAvg), Math.Min(tAvg, cAvg)), tAvg, cAvg);
        }

        // Runs up to maxSwaps swap rounds. With hysteresis on (live play): only when the
        // stronger side's chance is over the trigger, only a swap that lands under the target,
        // only after min_rounds_between_moves, never a player already moved this map.
        // Returns the number of swaps made.
        private int EloBalanceSwaps(GameStats gs, string source, int maxSwaps, bool enforceHysteresis) {
            int swaps = 0;
            double trigger = balanceTriggerPct / 100.0;
            double target = balanceTargetPct / 100.0;

            while (swaps < maxSwaps) {
                var tStats = gs.getTeam(TEAM_T);
                var cStats = gs.getTeam(TEAM_CT);
                if (tStats.numPlayers() == 0 || cStats.numPlayers() == 0) {
                    break;
                }

                var all = EloRoster(gs, tStats, cStats);
                var (chance, tAvg, cAvg) = Evaluate(all);
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] elo balance ({source}): round={gs.roundNumber} T={tAvg:0} CT={cAvg:0} stronger_win={chance * 100:0.0}% trigger={balanceTriggerPct:0}% target={balanceTargetPct:0}%");

                if (chance <= trigger) {
                    break;
                }

                if (enforceHysteresis && gs.roundNumber - lastSwapRound < minRoundsBetweenMoves) {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] elo balance ({source}): holding, last swap round {lastSwapRound}.");
                    break;
                }

                int bestA = -1, bestB = -1;
                double bestChance = double.MaxValue;
                int bestAlive = int.MaxValue;

                foreach (var a in all) {
                    if (enforceHysteresis && movedThisMap.Contains(a.uid)) continue;
                    foreach (var b in all) {
                        if (a.isT == b.isT) continue;
                        if (enforceHysteresis && movedThisMap.Contains(b.uid)) continue;

                        var (newChance, _, _) = Evaluate(all, a.uid, b.uid);
                        int alive = (a.alive ? 1 : 0) + (b.alive ? 1 : 0);
                        // Lower chance wins; among equals, prefer moving players who are dead.
                        if (newChance < bestChance - 1e-9 || (Math.Abs(newChance - bestChance) <= 1e-9 && alive < bestAlive)) {
                            bestChance = newChance;
                            bestAlive = alive;
                            bestA = a.uid;
                            bestB = b.uid;
                        }
                    }
                }

                if (bestA == -1 || bestChance >= target) {
                    Console.WriteLine($"[INFO] OSBase[{ModuleName}] elo balance ({source}): no swap reaches target (best={(bestA == -1 ? "none" : (bestChance * 100).ToString("0.0") + "%")}).");
                    break;
                }

                var pA = Utilities.GetPlayerFromUserid(bestA);
                var pB = Utilities.GetPlayerFromUserid(bestB);
                if (pA == null || pB == null || !pA.UserId.HasValue || !pB.UserId.HasValue) {
                    break;
                }

                Console.WriteLine($"[INFO] OSBase[{ModuleName}] elo swap ({source}): {pA.PlayerName}({bestA})[{TeamName(pA.TeamNum)}] <-> {pB.PlayerName}({bestB})[{TeamName(pB.TeamNum)}] {chance * 100:0.0}% -> {bestChance * 100:0.0}%");

                if (IsWarmup(gs)) {
                    RawMove(gs, pA, (pA.TeamNum == TEAM_T) ? TEAM_CT : TEAM_T, announce: false, reason: "elo_" + source);
                    RawMove(gs, pB, (pB.TeamNum == TEAM_T) ? TEAM_CT : TEAM_T, announce: false, reason: "elo_" + source);
                } else {
                    MoveWithImmunity(gs, pA, (pA.TeamNum == TEAM_T) ? TEAM_CT : TEAM_T, announce: false, reason: "elo_" + source);
                    MoveWithImmunity(gs, pB, (pB.TeamNum == TEAM_T) ? TEAM_CT : TEAM_T, announce: false, reason: "elo_" + source);
                }
                AnnounceSwap(pA, pB);

                swaps++;
                lastSwapRound = gs.roundNumber;
                swapsThisMap++;
                playerSwapRound[bestA] = gs.roundNumber;
                playerSwapRound[bestB] = gs.roundNumber;
                if (!IsWarmup(gs)) {
                    movedThisMap.Add(bestA);
                    movedThisMap.Add(bestB);
                }
            }

            return swaps;
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
                movedThisMap.Add(player.UserId.Value);

                tStats = gs.getTeam(TEAM_T);
                cStats = gs.getTeam(TEAM_CT);
            }
        }

        // With exactly 2 humans on the same team, swap one with a bot on the other team so
        // each team has one human (keeps sizes intact). Returns true if it moved anyone.
        private bool EnsureHumansSplit(GameStats gs) {
            var humans = Utilities.GetPlayers()
                .Where(p => IsHumanPlayer(p) && IsPlayingTeam(p))
                .ToList();

            if (humans.Count != 2 || humans[0].TeamNum != humans[1].TeamNum) {
                return false;
            }

            int fromTeam = humans[0].TeamNum;
            int toTeam = fromTeam == TEAM_T ? TEAM_CT : TEAM_T;

            var bot = Utilities.GetPlayers().FirstOrDefault(p =>
                p != null && p.IsValid && p.IsBot && !p.IsHLTV && p.TeamNum == toTeam);
            if (bot == null) {
                return false;
            }

            var human = humans[1];
            Console.WriteLine($"[INFO] OSBase[{ModuleName}] Human split: {human.PlayerName} {TeamName(fromTeam)} <-> bot {bot.PlayerName} {TeamName(toTeam)}");
            MoveWithImmunity(gs, human, toTeam, announce: true, reason: "human_split");
            RawMove(gs, bot, fromTeam, announce: false, reason: "human_split");

            SyncTeams(gs);
            return true;
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

            float compPenalty = CompPenaltyScale * (MathF.Abs(strongT - strongCT) + MathF.Abs(weakT - weakCT));
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
            float compPenalty = CompPenaltyScale * (MathF.Abs(strongT - strongCT) + MathF.Abs(weakT - weakCT));
            return meanGap + compPenalty;
        }

        // "gamestats"/"shadow" both balance on GameStats -- gs/ps only matter in that branch.
        // Elo has no separate warmup-vs-live signal (see SkillResolver.cs), so in "elo" mode
        // this is the same lookup as SignalSkill below.
        private float WarmupSignalForPlayer(GameStats gs, int userId, PlayerStats ps) {
            if (balancerSkillSource == "elo") {
                return SkillResolver.GetEffectiveSkill(eloRating, userId, currentRosterMedian, minRatedMatches);
            }

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
            if (balancerSkillSource == "elo") {
                return SkillResolver.GetEffectiveSkill(eloRating, userId, currentRosterMedian, minRatedMatches);
            }

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