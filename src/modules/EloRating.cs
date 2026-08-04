using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using MySqlConnector;
using OSBase.Helpers;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace OSBase.Modules;

// Two-part system covering ALL play, not just tournament matches -- see ELO-MODULE.md and
// STATS-MODULE.md/traffkarta-hit-stats.md (asks 4, 19, 20) for the full design and the
// reversal history: this module was tournament-scoped until it became clear the server runs
// roughly one tournament a year, which makes a tournament-scoped rating an annual event
// result, not a ranking. Ask 11's gates (no bots, no warmup, min_players) replace that
// tournament window as the reason a kill counts, decided once per round in OnRoundStart.
//
// Part one -- RATING (elo_rating, no season, one row per player, never reset). A
// continuously-updated skill estimate, Elo-style: kills/deaths move it via the existing
// duel formula, headshots add a bonus proportional to the delta already earned, assists add
// a small flat reward. "How good is this player" doesn't stop being true in January.
//
// Part two -- POINTS (elo_points, keyed by steamid64+season, reset every quarter by simply
// starting a new season string -- no archiving step, same reason every other table in this
// system uses season-in-the-key instead of cs2rank's table-rename approach: a reset that
// requires a step is a reset that one day skips the step, and this community's rule is
// nothing gets deleted). Classic HLstatsX-style: opponent-ratio-scaled points for kills,
// assists, and round wins. Earned only by doing things, never by being connected.
//
// Rating math runs live and synchronously on the game thread (Elo is order-dependent,
// unlike EventWeekend's commutative point tally, so it can't be queued and applied out of
// order). Points are a straightforward additive accumulator and don't share that constraint,
// but are computed at the same time since they need the live rating for opponent-scaling.
// The resulting DB writes are buffered in memory and only sent between rounds, on
// EventRoundEnd, to keep the write path off the server during live rounds -- same pattern
// as EventWeekend's FlushPendingWrites.
//
// tournament_match is no longer the gate -- it's a tag. currentMatchId (when a window
// happens to be open) still rides along on elo_kill_event.match_id (now nullable) purely as
// a data point, so a Tuesday night and this year's one tournament stay distinguishable in
// the history even though both now score.
public class EloRating : ModuleBase {
    public override string ModuleName => "elorating";
    protected override string DefaultEnabled => "0";

    private const string RatingTable = "elo_rating";
    private const string PointsTable = "elo_points";
    private const string KillEventTable = "elo_kill_event";
    private const string BonusEventTable = "elo_bonus_event";
    private const string MatchTable = "tournament_match"; // site-owned; never created here, PK is `id`
    private const float MatchWindowRefreshIntervalSeconds = 30.0f;

    private GameStats? gameStats;
    private Database? db;

    // cfg (elorating.cfg) -- host/port identify this server so it can be matched against
    // tournament_match.server_address, which is free text an admin typed (DNS name or IP,
    // with or without port). See IsThisServer() for the canonicalization.
    private string host = "";
    private int port = 0;
    private string chatPrefix = "[Elo]";

    // Player-facing chat commands (distinct from the admin-facing css_elo_top/
    // css_elo_points_top console commands above -- these are typed text, matched in
    // OnPlayerChat, same mechanism as TeamBets' "bet" command). Names are config so they can
    // run as !elorank/!elotop during the 2026Q3 trial season (cs2rank still owns !rank/!top)
    // and get renamed here on Oct 1 when cs2rank unloads -- no rebuild either time.
    private string rankCommand = "!elorank";
    private string topCommand = "!elotop";
    private int kFactor = 32;
    private int kFactorProvisional = 50;
    private int provisionalMatches = 30;
    private int startRating = 1000;
    private int topLimit = 10;
    private string adminPermission = "@css/generic";

    // Ask 11: no bots (IsRealPlayer, unchanged), no warmup (hard rule, not configurable --
    // same as DamageReport/TeamBets), min_players (configurable -- nobody knows the right
    // number until real data exists). Decided once per round in OnRoundStart, held for the
    // whole round so people logging off late in the evening don't retroactively disqualify a
    // round that started at full strength.
    private int minPlayers = 4;
    private bool statsGateOpen;

    // Rating formula extensions -- calibration guesses, config values because nobody has
    // real numbers yet (same reasoning as min_players).
    private double headshotBonusPct = 0.20;
    private int assistReward = 5;

    // Found 2026-08-04, per direct user ask (old CS:Source gave -1 on the scoreboard for
    // these): teamkill/suicide penalties. Corrected same day after agent-chat #18: NOT
    // rating -- elo_rating feeds LAN team-balancing (TeamBalancer, balancer_skill_source
    // "elo"), so docking it for a teamkill would make the balancer think the player is
    // worse than they are and build lopsided teams from a punishment that has nothing to do
    // with skill. Points instead: this is now a deliberate, documented exception to "points
    // never go down" (see the Points section below), not a silent violation of it -- teamkill
    // costs the team a player AND is someone else's fault; a suicide already punishes itself
    // (dead, team a man short) and costs less. Negative by convention -- stored and applied
    // as-is, never Math.Abs'd, so a config typo (a positive value here) fails loudly as a
    // reward instead of silently doing nothing.
    private int teamkillPointsPenalty = -10;
    private int suicidePointsPenalty = -5;

    // Points formula -- classic HLstatsX ratio scaling: points = pointsPerKill * clamp(
    // victimRating / attackerRating, pointsRatioMin, pointsRatioMax). Beating a stronger
    // opponent multiplies up, beating a weaker one multiplies down, bounded so neither an
    // extreme mismatch nor a near-zero rating produces an absurd swing.
    private int pointsPerKill = 10;
    private double pointsRatioMin = 0.5;
    private double pointsRatioMax = 2.0;
    private double pointsAssistFraction = 0.3;
    private int pointsPerRoundWin = 2;

    private Timer? matchWindowTimer;
    private int? currentMatchId;
    private bool flushInProgress;

    // osbase-stat-contracts.md section 5, same fix as DamageReport.cs: delay the flush's
    // transaction off the exact round-end tick instead of firing synchronously with it.
    private const float RoundEndFlushDelaySeconds = 2.0f;
    private Timer? pendingFlushTimer;

    // Authoritative live cache: mirrors elo_rating for whoever has duelled this session,
    // seeded lazily (once per player) straight from the kill path, kept in sync on every
    // kill. The DB is a durable mirror of this, not the other way around, while the module
    // is active.
    // decimal, not int -- see the DECIMAL(12,4) comment on ratingTable above. Every duel
    // delta accumulates here at full precision; rounding only happens where a value is
    // about to be displayed (TryGetRating, the leaderboard queries, ShowRankCommand).
    private readonly Dictionary<ulong, decimal> liveRating = new();
    private readonly Dictionary<ulong, int> liveMatches = new();

    // Same idea as liveRating -- an authoritative in-memory running total, seeded once per
    // (player, season) straight from the DB, kept in sync on every award. Needed so
    // TryGetPoints (used by DamageReport for ask 22's player_daily_stat snapshot) reads a
    // value that's always instantly correct regardless of when either module's own flush
    // happens to run -- two modules independently subscribed to EventRoundEnd have no
    // guaranteed ordering relative to each other, so a snapshot can't wait on a flush.
    // decimal, not int -- see the DECIMAL(12,2) comment on pointsTable above. Same
    // "accumulate exact, round only at display" rule as liveRating.
    private readonly Dictionary<(ulong SteamId64, string Season), decimal> livePoints = new();

    // Found 2026-08-04 (agent-chat #13/#15): weapon-dependent POINTS (not rating -- that
    // stays rejected, see "not per weapon" above) need to know what the VICTIM had, which
    // elo_kill_event's own `weapon` column never captured (it's the killer's weapon only).
    // Two different, both-useful signals, tracked live rather than read off pawn state at
    // the moment of death (unverified whether that's even reliable then -- this sidesteps
    // the question entirely by never depending on it):
    //   lastEquippedWeapon   -- "what could this player do right now", updated on every
    //                           EventItemEquip. Reflects a weapon switch instantly, no pawn
    //                           lookup needed at kill time.
    //   bestWeaponThisRound  -- "what kind of player was this, this round", the highest-
    //                           priced weapon touched (bought or picked up) since round
    //                           start, regardless of what's currently equipped.
    // Both are read out, not computed, at the moment of death -- same "capture live, decide
    // later" shape as everything else already buffered in this module.
    private readonly Dictionary<ulong, string> lastEquippedWeapon = new();
    private readonly Dictionary<ulong, (string Weapon, int Price)> bestWeaponThisRound = new();

    // Static, public CS2 economy data (buy-menu prices) -- not a guess, not something that
    // needs verifying against a live server the way pawn-state reliability does. Keyed on
    // the raw "weapon_x" classname (EventItemPurchase/EventItemPickup's own format), not the
    // NormalizeWeapon-collapsed form, so "which AWP-tier weapon" stays distinguishable.
    // Anything absent (default pistols, knife, C4) prices at 0 -- correct, not a gap.
    private static readonly Dictionary<string, int> WeaponPrices = new(StringComparer.OrdinalIgnoreCase) {
        ["weapon_deagle"] = 700, ["weapon_elite"] = 300, ["weapon_fiveseven"] = 500,
        ["weapon_tec9"] = 500, ["weapon_cz75a"] = 500, ["weapon_p250"] = 300, ["weapon_revolver"] = 600,
        ["weapon_mac10"] = 1050, ["weapon_mp9"] = 1250, ["weapon_mp7"] = 1500, ["weapon_mp5sd"] = 1500,
        ["weapon_ump45"] = 1200, ["weapon_p90"] = 2350, ["weapon_bizon"] = 1400,
        ["weapon_galilar"] = 1800, ["weapon_famas"] = 2050, ["weapon_ak47"] = 2700,
        ["weapon_m4a1"] = 2900, ["weapon_m4a1_silencer"] = 2900, ["weapon_sg556"] = 3000, ["weapon_aug"] = 3300,
        ["weapon_ssg08"] = 1700, ["weapon_awp"] = 4750, ["weapon_scar20"] = 5000, ["weapon_g3sg1"] = 5000,
        ["weapon_nova"] = 1050, ["weapon_xm1014"] = 2000, ["weapon_sawedoff"] = 1100, ["weapon_mag7"] = 1300,
        ["weapon_m249"] = 5200, ["weapon_negev"] = 1700,
        ["weapon_hegrenade"] = 300, ["weapon_flashbang"] = 200, ["weapon_smokegrenade"] = 300,
        ["weapon_molotov"] = 400, ["weapon_incgrenade"] = 600, ["weapon_decoy"] = 50,
        ["weapon_taser"] = 200
    };

    private readonly List<PendingKillEvent> pendingKillEvents = new();
    private readonly List<PendingBonusEvent> pendingBonusEvents = new();
    private readonly Dictionary<ulong, PendingRating> pendingRatings = new();
    private readonly Dictionary<(ulong SteamId64, string Season), PendingPoints> pendingPoints = new();

    private sealed class PendingKillEvent {
        public int? MatchId { get; init; }
        public string MapName { get; init; } = "";
        public string AttackerName { get; init; } = "Unknown";
        public ulong AttackerSteamId64 { get; init; }
        public decimal AttackerRatingBefore { get; init; }
        public decimal AttackerDelta { get; init; }
        public decimal AttackerPointsDelta { get; init; }
        public string VictimName { get; init; } = "Unknown";
        public ulong VictimSteamId64 { get; init; }
        public decimal VictimRatingBefore { get; init; }
        public decimal VictimDelta { get; init; }
        public string Weapon { get; init; } = "";
        public bool Headshot { get; init; }
        // Found 2026-08-04 (agent-chat #13/#15): what the victim held, not just the murder
        // weapon -- Weapon above. Nullable: a victim who spawned and died before their first
        // EventItemEquip/Purchase/Pickup has no tracked value yet, which is a real "unknown",
        // not a bug to paper over with a default.
        public string? VictimActiveWeapon { get; init; }
        public string? VictimBestWeapon { get; init; }
    }

    // Found 2026-08-04 (agent-chat #10): elo_kill_event's own "rebuild the whole ladder"
    // purpose was never actually met -- assist and round-win deltas were applied straight
    // to elo_rating/elo_points as counters, with no replayable row anywhere. This is the
    // fix: every rating/points award that doesn't already land in elo_kill_event gets one
    // of these instead. Kind is "assist" or "round_win" -- round_win never touches rating,
    // so RatingDelta is 0 for it. RelatedAttacker/VictimSteamId64 are null for round_win
    // (no duel to point back to); populated for assist so the row it grew out of is
    // traceable, same "save what it was built on" precedent as PendingKillEvent.
    private sealed class PendingBonusEvent {
        public required string Kind;
        public required ulong SteamId64;
        public required string Name;
        public required int RatingDelta;
        public required decimal PointsDelta;
        public required string Season;
        public required string MapName;
        public int? MatchId;
        public ulong? RelatedAttackerSteamId64;
        public ulong? RelatedVictimSteamId64;
        public required DateTime Stamp;
    }

    private sealed class PendingRating {
        public string Name { get; set; } = "Unknown";
        public decimal Rating { get; set; }
        public int MatchesDelta { get; set; }
    }

    private sealed class PendingPoints {
        public string Name { get; set; } = "Unknown";
        public decimal Points { get; set; }
    }

    protected override void OnLoad() {
        gameStats = osbase?.GetGameStats();

        CreateCustomConfigs();
        LoadConfig();

        db = new Database(osbase!, config!);
        CreateTables();
        StartMatchWindowTimer();
    }

    protected override void OnUnload() {
        StopMatchWindowTimer();
        pendingFlushTimer?.Kill();
        pendingFlushTimer = null;
        FlushPendingWrites("Unload");

        liveRating.Clear();
        liveMatches.Clear();
        currentMatchId = null;
        db = null;
        gameStats = null;
    }

    protected override void OnReloadConfig() {
        gameStats = osbase?.GetGameStats();
        CreateCustomConfigs();
        LoadConfig();
        CreateTables();
    }

    protected override void RegisterHandlers() {
        osbase?.SubscribeToEvent<EventRoundStart>(OnRoundStart);
        osbase?.SubscribeToEvent<EventPlayerDeath>(OnPlayerDeath);
        osbase?.SubscribeToEvent<EventRoundEnd>(OnRoundEnd);
        osbase?.SubscribeToEvent<EventPlayerChat>(OnPlayerChat);
        osbase?.SubscribeToEvent<EventItemEquip>(OnItemEquip);
        osbase?.SubscribeToEvent<EventItemPurchase>(OnItemPurchase);
        osbase?.SubscribeToEvent<EventItemPickup>(OnItemPickup);
        osbase?.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        osbase?.AddCommand("css_elo_top", "Shows the Elo rating leaderboard", OnEloTopCommand);
        osbase?.AddCommand("css_elo_points_top", "Shows this season's Elo points leaderboard", OnEloPointsTopCommand);
        osbase?.AddCommand("css_elo_match_start", "Admin: opens the Elo scoring window for a tournament match", OnEloMatchStartCommand);
        osbase?.AddCommand("css_elo_match_stop", "Admin: closes the Elo scoring window for a tournament match", OnEloMatchStopCommand);
    }

    protected override void UnregisterHandlers() {
        osbase?.UnsubscribeFromEvent<EventRoundStart>(OnRoundStart);
        osbase?.UnsubscribeFromEvent<EventPlayerDeath>(OnPlayerDeath);
        osbase?.UnsubscribeFromEvent<EventRoundEnd>(OnRoundEnd);
        osbase?.UnsubscribeFromEvent<EventPlayerChat>(OnPlayerChat);
        osbase?.UnsubscribeFromEvent<EventItemEquip>(OnItemEquip);
        osbase?.UnsubscribeFromEvent<EventItemPurchase>(OnItemPurchase);
        osbase?.UnsubscribeFromEvent<EventItemPickup>(OnItemPickup);
        osbase?.RemoveListener<Listeners.OnMapStart>(OnMapStart);
        osbase?.RemoveCommand("css_elo_top", OnEloTopCommand);
        osbase?.RemoveCommand("css_elo_points_top", OnEloPointsTopCommand);
        osbase?.RemoveCommand("css_elo_match_start", OnEloMatchStartCommand);
        osbase?.RemoveCommand("css_elo_match_stop", OnEloMatchStopCommand);
    }

    private HookResult OnRoundStart(EventRoundStart _) {
        if (!isActive) {
            return HookResult.Continue;
        }

        // Ask 11's gate, decided here and held for the whole round -- see the field comment
        // on statsGateOpen. Warmup is a hard rule, not configurable.
        bool warmup = gameStats?.IsWarmup ?? true;
        int humans = CountConnectedHumans();
        statsGateOpen = !warmup && humans >= minPlayers;
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: round gate {(statsGateOpen ? "open" : "closed")} (humans={humans} min={minPlayers} warmup={warmup})");

        // "Best weapon this round" resets every round on purpose -- it answers "what kind of
        // player was this THIS round", not across the whole map. lastEquippedWeapon is NOT
        // cleared here: a player who hasn't touched their loadout since last round is still
        // holding the same thing, and clearing it would read as "unknown" for a kill that
        // happens before their first EventItemEquip of the new round.
        bestWeaponThisRound.Clear();

        // osbase-stat-contracts.md section 5's flush-on-the-way-out requirement: guarantees
        // a fast round never lets more than one round's worth of writes queue up behind the
        // round-end delay timer below. No-op if the timer already fired.
        pendingFlushTimer?.Kill();
        pendingFlushTimer = null;
        FlushPendingWrites("RoundStart");

        return HookResult.Continue;
    }

    private HookResult OnItemEquip(EventItemEquip e) {
        if (!isActive || !IsRealPlayer(e.Userid)) {
            return HookResult.Continue;
        }

        lastEquippedWeapon[e.Userid!.SteamID] = RawItemName(e.Item);
        TrackBestWeapon(e.Userid.SteamID, e.Item);

        return HookResult.Continue;
    }

    private HookResult OnItemPurchase(EventItemPurchase e) {
        if (!isActive || !IsRealPlayer(e.Userid)) {
            return HookResult.Continue;
        }

        TrackBestWeapon(e.Userid!.SteamID, e.Weapon);
        return HookResult.Continue;
    }

    private HookResult OnItemPickup(EventItemPickup e) {
        if (!isActive || !IsRealPlayer(e.Userid)) {
            return HookResult.Continue;
        }

        TrackBestWeapon(e.Userid!.SteamID, e.Item);
        return HookResult.Continue;
    }

    // "Best" = highest buy-menu price seen this round -- a running max, never displaced by
    // something cheaper touched afterward. Absent from WeaponPrices (default pistols, knife)
    // prices at 0, which only overwrites an existing entry if nothing better has been seen
    // yet -- correct, since a bare-pistol round is genuinely "best weapon: pistol".
    private void TrackBestWeapon(ulong steamId64, string rawItem) {
        string weapon = RawItemName(rawItem);
        int price = WeaponPrices.GetValueOrDefault(weapon, 0);

        if (!bestWeaponThisRound.TryGetValue(steamId64, out var current) || price >= current.Price) {
            bestWeaponThisRound[steamId64] = (weapon, price);
        }
    }

    // EventItemEquip/Purchase/Pickup's Item/Weapon fields carry the raw "weapon_x" classname
    // -- unlike DamageReport.NormalizeWeapon, deliberately not collapsed here, same reasoning
    // as knife_taser_kill_event's RawWeaponName: the site asked what the victim held, not a
    // category.
    private static string RawItemName(string? item) {
        string normalized = (item ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.Length == 0 ? "unknown" : normalized;
    }

    private static int CountConnectedHumans() {
        return Utilities.GetPlayers().Count(p =>
            p != null && p.IsValid && !p.IsHLTV && !p.IsBot && p.Connected == PlayerConnectedState.Connected
        );
    }

    // Extracted to SeasonHelper 2026-08-04 (agent-chat #33) -- see that helper's comment.
    private static string CurrentSeason() => SeasonHelper.CurrentSeason();

    // ----- config -----

    private void CreateCustomConfigs() {
        config?.CreateCustomConfig(
            $"{ModuleName}.cfg",
            "// EloRating Configuration\n" +
            "// This module scores ALL play, gated by ask 11 (min_players below; warmup and\n" +
            "// bots are hard rules, not configurable). host/port and tournament_match are no\n" +
            "// longer a gate -- they're only still used to tag elo_kill_event.match_id when a\n" +
            "// tournament window happens to be open, so a Tuesday night stays distinguishable\n" +
            "// from this year's one tournament in the history. See ELO-MODULE.md.\n" +
            "host \"\"\n" +
            "port 0\n" +
            "chat_prefix \"[Elo]\"\n" +
            "// Player-facing chat commands, typed in all-chat. Trial-season names below --\n" +
            "// rename to \"!rank\"/\"!top\" here on Oct 1 once cs2rank unloads, no rebuild needed.\n" +
            "rank_command \"!elorank\"\n" +
            "top_command \"!elotop\"\n" +
            "min_players 4\n" +
            "k_factor 32\n" +
            "k_factor_provisional 50\n" +
            "provisional_matches 30\n" +
            "start_rating 1000\n" +
            "top_limit 10\n" +
            "// Rating formula extensions -- calibration guesses, nobody has real numbers yet.\n" +
            "headshot_bonus_pct 0.20\n" +
            "assist_reward 5\n" +
            "// Teamkill/suicide POINTS penalties (old CS:Source gave -1 on the scoreboard for\n" +
            "// these; this is the Elo-side equivalent). Deliberately not rating -- see the\n" +
            "// field comment. Negative values -- do not flip the sign here, that would turn a\n" +
            "// penalty into a reward.\n" +
            "teamkill_points_penalty -10\n" +
            "suicide_points_penalty -5\n" +
            "// Points formula (elo_points, resets every season) -- classic HLstatsX-style\n" +
            "// opponent-ratio scaling: points = points_per_kill * clamp(victimRating /\n" +
            "// attackerRating, points_ratio_min, points_ratio_max).\n" +
            "points_per_kill 10\n" +
            "points_ratio_min 0.5\n" +
            "points_ratio_max 2.0\n" +
            "points_assist_fraction 0.3\n" +
            "points_per_round_win 2\n" +
            "admin_permission \"@css/generic\"\n"
        );
    }

    private void LoadConfig() {
        host = "";
        port = 0;
        chatPrefix = "[Elo]";
        rankCommand = "!elorank";
        topCommand = "!elotop";
        minPlayers = 4;
        kFactor = 32;
        kFactorProvisional = 50;
        provisionalMatches = 30;
        startRating = 1000;
        topLimit = 10;
        headshotBonusPct = 0.20;
        assistReward = 5;
        teamkillPointsPenalty = -10;
        suicidePointsPenalty = -5;
        pointsPerKill = 10;
        pointsRatioMin = 0.5;
        pointsRatioMax = 2.0;
        pointsAssistFraction = 0.3;
        pointsPerRoundWin = 2;
        adminPermission = "@css/generic";

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
            string value = Unquote(parts[1].Trim());

            switch (key.ToLowerInvariant()) {
                case "host":
                    host = value;
                    break;
                case "port":
                    port = ParseInt(value, 0, 0, 65535);
                    break;
                case "chat_prefix":
                    chatPrefix = string.IsNullOrWhiteSpace(value) ? "[Elo]" : value;
                    break;
                case "rank_command":
                    rankCommand = string.IsNullOrWhiteSpace(value) ? "!elorank" : value;
                    break;
                case "top_command":
                    topCommand = string.IsNullOrWhiteSpace(value) ? "!elotop" : value;
                    break;
                case "k_factor":
                    kFactor = ParseInt(value, 32, 1, 200);
                    break;
                case "k_factor_provisional":
                    kFactorProvisional = ParseInt(value, 50, 1, 200);
                    break;
                case "provisional_matches":
                    provisionalMatches = ParseInt(value, 30, 0, 1000);
                    break;
                case "start_rating":
                    startRating = ParseInt(value, 1000, 0, 5000);
                    break;
                case "top_limit":
                    topLimit = ParseInt(value, 10, 1, 50);
                    break;
                case "min_players":
                    minPlayers = ParseInt(value, 4, 0, 64);
                    break;
                case "headshot_bonus_pct":
                    headshotBonusPct = ParseDouble(value, 0.20, 0.0, 5.0);
                    break;
                case "assist_reward":
                    assistReward = ParseInt(value, 5, 0, 1000);
                    break;
                case "teamkill_points_penalty":
                    teamkillPointsPenalty = ParseInt(value, -10, -1000, 0);
                    break;
                case "suicide_points_penalty":
                    suicidePointsPenalty = ParseInt(value, -5, -1000, 0);
                    break;
                case "points_per_kill":
                    pointsPerKill = ParseInt(value, 10, 0, 10000);
                    break;
                case "points_ratio_min":
                    pointsRatioMin = ParseDouble(value, 0.5, 0.0, 10.0);
                    break;
                case "points_ratio_max":
                    pointsRatioMax = ParseDouble(value, 2.0, 0.0, 10.0);
                    break;
                case "points_assist_fraction":
                    pointsAssistFraction = ParseDouble(value, 0.3, 0.0, 5.0);
                    break;
                case "points_per_round_win":
                    pointsPerRoundWin = ParseInt(value, 2, 0, 10000);
                    break;
                case "admin_permission":
                    adminPermission = string.IsNullOrWhiteSpace(value) ? "@css/generic" : value;
                    break;
                default:
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Unknown config key {key}:{value}");
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(host)) {
            Console.WriteLine($"[WARN] OSBase[{ModuleName}]: host is empty -- no tournament_match row can ever match this server, module will stay inert until it's set.");
        }
    }

    // ----- tables (OSBase-owned; tournament_match is not created here, it belongs to the site) -----

    private void CreateTables() {
        if (db == null) {
            return;
        }

        // Part one: rating. No season -- a continuously-updated skill estimate that never
        // resets, unlike points below. Season would belong here only if rating itself reset
        // quarterly, and it deliberately doesn't ("how good is this player" doesn't stop
        // being true in January).
        //
        // DECIMAL(12,4), not INT (found 2026-08-04, user's own review of ELO-MODULE.md):
        // rounding each duel's delta to a whole number before accumulating silently floors
        // small deltas to 0 for lopsided matchups, discarding real (if small) skill signal
        // every time it happens, not just once. The INT-vs-DECIMAL question was never about
        // display -- rating is still shown to players as a whole number everywhere (see
        // TryGetRating, which rounds on the way out) -- it was about STORAGE: store exact,
        // round only at the point something is printed. Same principle as the still-unbuilt
        // elo_points DECIMAL migration in the HLstatsX backlog, applied here first because
        // this one was a live, confirmed bug rather than a proposal.
        string ratingTable = $"""
        TABLE IF NOT EXISTS {RatingTable} (
            steamid64  VARCHAR(32) NOT NULL,
            name       VARCHAR(64) NOT NULL,
            rating     DECIMAL(12,4) NOT NULL,
            matches    INT NOT NULL DEFAULT 0,
            updated_at DATETIME NOT NULL,
            PRIMARY KEY (steamid64)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        // Part two: points. season IS in the key here, on purpose -- the opposite lifecycle
        // from rating. A reset is just a new season string; no archiving step exists to skip,
        // unlike cs2rank's table-rename approach, so nothing here can be reset by accident
        // into a silent deletion.
        //
        // DECIMAL(12,2), not INT (found 2026-08-04, same review that caught the rating
        // rounding-to-zero bug, agent-chat #63): this was left as backlog when rating got
        // fixed, on the reasoning "rating is shown as a whole number, points wasn't, so it
        // needs the fix more urgently" -- backwards. Points is the MORE visible failure, not
        // the less: a player who kills someone and sees the number not move notices in the
        // same second, where a stalled rating is invisible from one duel. It's also easier to
        // hit -- the ratio clamp's floor (points_ratio_min, default 0.5, dropping to 0.05 once
        // weapon-weighting lands per the HLstatsX backlog) times points_per_kill can land well
        // under 1 point long before rating's much larger skill gap requirement does. Same
        // fix, same shape: store exact, round only at display (TryGetPoints, css_elo_points_top,
        // !elorank/!elotop, ShowRankCommand).
        string pointsTable = $"""
        TABLE IF NOT EXISTS {PointsTable} (
            steamid64  VARCHAR(32) NOT NULL,
            season     VARCHAR(8) NOT NULL,
            name       VARCHAR(64) NOT NULL,
            points     DECIMAL(12,2) NOT NULL DEFAULT 0,
            updated_at DATETIME NOT NULL,
            PRIMARY KEY (steamid64, season)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        // Durable, ordered log -- kept so the ladder can be rebuilt from scratch when the
        // formula changes (K-factor, start rating, provisional handling). See ELO-MODULE.md
        // for why this module keeps raw events instead of only counters. match_id is now
        // NULLABLE: tournament_match is a tag, not a gate, since this module scores all play
        // -- NULL means an ordinary kill, a real id means a tournament window happened to be
        // open at the time. attacker_points_delta rides along on the same row for the same
        // reason attacker_rating_before/attacker_delta do: save what was awarded and what it
        // was built on, so a later formula change can be explained instead of silently
        // rewriting history. Assist and round-win points aren't logged here (they aren't
        // kills, there's no attacker/victim pair to hang them on) -- they go into
        // elo_bonus_event instead (below), which exists for exactly this reason: this
        // table's own "rebuild the whole ladder" claim wasn't true without it.
        //
        // victim_active_weapon/victim_best_weapon added 2026-08-04 (agent-chat #13/#15): the
        // owner wants weapon-dependent POINTS (not rating -- stays rejected, see "not per
        // weapon" above), and his own examples ("AWPing pistols", "meeting ARs with a
        // pistol") are about what the VICTIM had, not just `weapon` above (the killer's).
        // That dimension didn't exist anywhere in this schema and, like everything else in
        // this file, is gone the instant a kill happens if it isn't captured then -- no
        // weighting scheme invented later could tell an AK-vs-pistol kill apart from an
        // AK-vs-AK one after the fact. Both nullable: a victim who dies before their first
        // tracked weapon event has a real "unknown", not a value to fake.
        string killEventTable = $"""
        TABLE IF NOT EXISTS {KillEventTable} (
            id                     BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
            match_id               INT NULL,
            stamp                  DATETIME NOT NULL,
            mapname                VARCHAR(64) NOT NULL,
            attacker               VARCHAR(64) NOT NULL,
            attackerid64           VARCHAR(32) NOT NULL,
            attacker_rating_before DECIMAL(12,4) NOT NULL,
            attacker_delta         DECIMAL(12,4) NOT NULL,
            attacker_points_delta  DECIMAL(12,2) NOT NULL DEFAULT 0,
            victim                 VARCHAR(64) NOT NULL,
            victimid64             VARCHAR(32) NOT NULL,
            victim_rating_before   DECIMAL(12,4) NOT NULL,
            victim_delta           DECIMAL(12,4) NOT NULL,
            weapon                 VARCHAR(32) NULL,
            headshot               TINYINT(1) NOT NULL DEFAULT 0,
            victim_active_weapon   VARCHAR(32) NULL,
            victim_best_weapon     VARCHAR(32) NULL,
            PRIMARY KEY (id),
            INDEX idx_elo_kill_event (match_id, stamp)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        // Found 2026-08-04: elo_kill_event's stated purpose ("rebuild the whole ladder from
        // scratch") wasn't actually met -- assist and round-win awards touched
        // elo_rating/elo_points directly with no replayable row. This table is that missing
        // half of the ledger. kind is "assist" or "round_win"; rating_delta is always 0 for
        // round_win (round wins never touch rating). related_attacker/victim id64 are NULL
        // for round_win (no duel to point back to) and populated for assist, so an assist
        // row stays traceable to the kill it grew out of.
        string bonusEventTable = $"""
        TABLE IF NOT EXISTS {BonusEventTable} (
            id                       BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
            kind                     VARCHAR(16) NOT NULL,
            match_id                 INT NULL,
            stamp                    DATETIME NOT NULL,
            mapname                  VARCHAR(64) NOT NULL,
            season                   VARCHAR(8) NOT NULL,
            name                     VARCHAR(64) NOT NULL,
            steamid64                VARCHAR(32) NOT NULL,
            rating_delta             INT NOT NULL DEFAULT 0,
            points_delta             DECIMAL(12,2) NOT NULL DEFAULT 0,
            related_attacker_id64    VARCHAR(32) NULL,
            related_victim_id64      VARCHAR(32) NULL,
            PRIMARY KEY (id),
            INDEX idx_elo_bonus_event (match_id, stamp)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        try {
            db.create(ratingTable);
            db.create(pointsTable);
            db.create(killEventTable);
            db.create(bonusEventTable);
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] tables ensured.");
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed creating tables: {e.Message}");
        }
    }

    // ----- match window: site-owned tournament_match -> is a match live on this server right now -----

    private void StartMatchWindowTimer() {
        if (!isActive || osbase == null) {
            return;
        }

        StopMatchWindowTimer();
        RefreshMatchWindow("Load");
        matchWindowTimer = osbase.AddTimer(MatchWindowRefreshIntervalSeconds, () => RefreshMatchWindow("Timer"), TimerFlags.REPEAT);
    }

    private void StopMatchWindowTimer() {
        matchWindowTimer?.Kill();
        matchWindowTimer = null;
    }

    private void RefreshMatchWindow(string source) {
        var database = db;
        if (!isActive || database == null || string.IsNullOrWhiteSpace(host)) {
            return;
        }

        Task.Run(() => {
            // starts_at/ends_at are nullable unix-second INTs (NULL until someone opens the
            // window); a missing/never-started row and a genuine DB outage both fall through
            // to "no active match" here, which is the safe default either way.
            bool ok = database.trySelect(
                $"id, server_address FROM {MatchTable} WHERE starts_at IS NOT NULL AND ends_at IS NOT NULL " +
                "AND UNIX_TIMESTAMP() BETWEEN starts_at AND ends_at",
                out DataTable table
            );

            int? matchId = null;
            if (ok) {
                // server_address is free text an admin typed (DNS name or IP, with or
                // without port) -- can't compare it as a literal string. See IsThisServer().
                foreach (DataRow row in table.Rows) {
                    if (IsThisServer(row["server_address"]?.ToString())) {
                        matchId = Convert.ToInt32(row["id"]);
                        break;
                    }
                }
            }

            Server.NextFrame(() => ApplyMatchWindow(matchId, source));
        });
    }

    // Best-effort mirror of OSWeb's ServerCredentials::canonicalHost + HostResolver (not a
    // call to it -- OSBase has no access to that PHP code). Resolves both sides to IP
    // addresses so a DNS name and a bare IP for the same machine are recognized as equal.
    // A resolution failure or any doubt falls back to "not a match" rather than risking a
    // false positive that scores someone else's match.
    private bool IsThisServer(string? candidateAddress) {
        if (string.IsNullOrWhiteSpace(candidateAddress) || string.IsNullOrWhiteSpace(host)) {
            return false;
        }

        var (candidateHost, candidatePort) = ParseAddress(candidateAddress);

        if (candidatePort.HasValue && port > 0 && candidatePort.Value != port) {
            return false;
        }

        if (string.Equals(candidateHost, host, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        try {
            IPAddress[] ourIps = Dns.GetHostAddresses(host);
            IPAddress[] candidateIps = Dns.GetHostAddresses(candidateHost);
            return ourIps.Any(a => candidateIps.Any(b => a.Equals(b)));
        } catch (Exception e) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] DNS resolution failed comparing '{host}' to '{candidateHost}': {e.Message}");
            return false;
        }
    }

    private static (string Host, int? Port) ParseAddress(string address) {
        string trimmed = address.Trim();

        int idx = trimmed.LastIndexOf(':');
        if (idx > 0 && idx < trimmed.Length - 1 && int.TryParse(trimmed[(idx + 1)..], out int parsedPort)) {
            return (trimmed[..idx], parsedPort);
        }

        return (trimmed, null);
    }

    private void ApplyMatchWindow(int? matchId, string source) {
        if (!isActive || matchId == currentMatchId) {
            return;
        }

        if (matchId.HasValue) {
            Console.WriteLine($"[INFO] OSBase[{ModuleName}] match {matchId.Value} is live, Elo scoring on ({source}).");
        } else {
            Console.WriteLine($"[INFO] OSBase[{ModuleName}] no active match, Elo scoring off ({source}).");
        }

        currentMatchId = matchId;
    }

    // ----- rating seed: synchronous, once per player, only on the kill path that needs it -----

    private void SeedRating(ulong steamId64) {
        if (db == null || liveRating.ContainsKey(steamId64)) {
            return;
        }

        try {
            DataTable table = db.select(
                $"rating, matches FROM {RatingTable} WHERE steamid64=@id",
                new MySqlParameter("@id", steamId64.ToString())
            );

            if (table.Rows.Count > 0) {
                liveRating[steamId64] = Convert.ToDecimal(table.Rows[0]["rating"]);
                liveMatches[steamId64] = Convert.ToInt32(table.Rows[0]["matches"]);
            } else {
                liveRating[steamId64] = startRating;
                liveMatches[steamId64] = 0;
            }
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed seeding rating for {steamId64}: {e.Message}");
            liveRating[steamId64] = startRating;
            liveMatches[steamId64] = 0;
        }
    }

    // Public read for other modules (TeamBets, for its bet log's match_id column) -- tag
    // only, same nullable meaning as PendingKillEvent.MatchId: a real id while an admin's
    // css_elo_match_start/stop window is open, null otherwise. Never a gate on its own.
    public int? CurrentMatchId => currentMatchId;

    // Public read for other modules (TeamBalancer via SkillResolver, see src/helpers/
    // SkillResolver.cs) -- part one (rating) only, never part two (points). Reuses
    // SeedRating so a player who hasn't duelled yet this session but has a DB row still
    // resolves correctly, and gets cached for next time instead of re-querying every call.
    // Still returns an int: this is a display/consumption read, not the accumulator itself
    // (that's liveRating, now decimal -- see its field comment), so rounding here is the
    // "round only at the point something is printed" rule doing exactly its job, not a
    // regression of the DECIMAL fix.
    public bool TryGetRating(ulong steamId64, out int rating, out int matches) {
        rating = 0;
        matches = 0;

        if (steamId64 == 0) {
            return false;
        }

        SeedRating(steamId64);

        if (!liveRating.TryGetValue(steamId64, out decimal exact)) {
            return false;
        }

        rating = (int)Math.Round(exact, MidpointRounding.AwayFromZero);
        matches = liveMatches.GetValueOrDefault(steamId64, 0);
        return true;
    }

    // ----- scoring -----

    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo) {
        // Ask 11's gate replaces the old tournament-window gate -- see the class comment and
        // OnRoundStart. tournament_match still matters, just as a tag on the row (below), not
        // as the reason a kill counts.
        if (!isActive || !statsGateOpen) {
            return HookResult.Continue;
        }

        var attacker = eventInfo.Attacker;
        var victim = eventInfo.Userid;

        // Found 2026-08-04, per direct user ask: world-damage suicide (fall, drowning, ...)
        // reports no attacker at all, so it has to be caught here -- the real-player check
        // just below would otherwise drop it silently, same as it always has.
        if (attacker == null && IsRealPlayer(victim)) {
            ApplyPenalty(victim!.SteamID, CleanName(victim.PlayerName), "suicide_penalty",
                suicidePointsPenalty, CurrentSeason(), osbase?.currentMap ?? Server.MapName ?? "", null);
            return HookResult.Continue;
        }

        if (!IsRealPlayer(attacker) || !IsRealPlayer(victim)) {
            return HookResult.Continue;
        }

        ulong attackerSteamId64 = attacker!.SteamID;
        ulong victimSteamId64 = victim!.SteamID;

        if (attackerSteamId64 == victimSteamId64) {
            // Self-inflicted (own grenade, the "kill" command) -- same penalty as the
            // world-damage case above, just with a real attacker==victim controller.
            ApplyPenalty(victimSteamId64, CleanName(victim.PlayerName), "suicide_penalty",
                suicidePointsPenalty, CurrentSeason(), osbase?.currentMap ?? Server.MapName ?? "", null);
            return HookResult.Continue;
        }

        if (attacker.TeamNum == victim.TeamNum) {
            // Still doesn't score a duel -- an Elo duel needs an opposed outcome, unchanged
            // -- but does apply a penalty now, per direct user ask.
            ApplyPenalty(attackerSteamId64, CleanName(attacker.PlayerName), "teamkill_penalty",
                teamkillPointsPenalty, CurrentSeason(), osbase?.currentMap ?? Server.MapName ?? "", victimSteamId64);
            return HookResult.Continue;
        }

        SeedRating(attackerSteamId64);
        SeedRating(victimSteamId64);

        decimal attackerRating = liveRating[attackerSteamId64];
        decimal victimRating = liveRating[victimSteamId64];
        int attackerMatches = liveMatches[attackerSteamId64];
        int victimMatches = liveMatches[victimSteamId64];

        // Chess-style: each side has its own K, so the two deltas are not forced to be
        // equal and opposite -- a provisional attacker gaining fast off an established
        // victim who barely moves is correct, not a bug. See ELO-MODULE.md. The expected-
        // score curve itself stays double (Math.Pow has no decimal overload, and this value
        // is transient -- never stored, never accumulated -- so binary float precision here
        // is irrelevant; only the delta that gets added to liveRating below needs to be exact).
        double expectedAttacker = 1.0 / (1.0 + Math.Pow(10.0, (double)(victimRating - attackerRating) / 400.0));
        double expectedVictim = 1.0 - expectedAttacker;

        int kAttacker = attackerMatches < provisionalMatches ? kFactorProvisional : kFactor;
        int kVictim = victimMatches < provisionalMatches ? kFactorProvisional : kFactor;

        // Found 2026-08-04, user's own review: rounding this to an int before accumulating
        // silently floors small deltas (a dominant kill against a much weaker opponent) to
        // 0, discarding real skill signal every time it happens. Rounded to 4 decimal places
        // instead of 0 -- keeps the value exact enough that no genuine (nonzero) delta can
        // ever floor away, while still discarding the binary-float noise a raw double-to-
        // decimal cast would otherwise bake in past the digits that matter.
        decimal attackerDelta = Math.Round((decimal)(kAttacker * (1.0 - expectedAttacker)), 4, MidpointRounding.AwayFromZero);
        decimal victimDelta = Math.Round((decimal)(kVictim * (0.0 - expectedVictim)), 4, MidpointRounding.AwayFromZero);

        // Headshot bonus: proportional to the delta already earned, not a flat add-on --
        // beating a strong opponent with a headshot is still worth more than headshotting a
        // weak one, which preserves the Elo self-calibration property instead of adding an
        // opponent-blind bonus on top of it.
        if (eventInfo.Headshot && attackerDelta > 0) {
            attackerDelta += Math.Round(attackerDelta * (decimal)headshotBonusPct, 4, MidpointRounding.AwayFromZero);
        }

        liveRating[attackerSteamId64] = attackerRating + attackerDelta;
        liveRating[victimSteamId64] = victimRating + victimDelta;
        liveMatches[attackerSteamId64] = attackerMatches + 1;
        liveMatches[victimSteamId64] = victimMatches + 1;

        string attackerName = CleanName(attacker.PlayerName);
        string victimName = CleanName(victim.PlayerName);
        string season = CurrentSeason();

        // Points: classic HLstatsX-style, scaled by how much stronger the victim was --
        // beating a better player is worth more, beating a worse one worth less, clamped so
        // neither an extreme rating gap nor a near-zero rating produces an absurd swing.
        // Unlike rating, points from normal play never go down -- earned only by doing
        // things, never lost by dying, matching "poängen ackumuleras genom att man gör
        // saker". ApplyPenalty (below) is the one deliberate, documented exception -- scoped
        // to teamkill/suicide specifically, not a general reopening of this rule.
        double ratio = attackerRating > 0
            ? Math.Clamp((double)victimRating / (double)attackerRating, pointsRatioMin, pointsRatioMax)
            : pointsRatioMax;
        // Found 2026-08-04, agent-chat #63: same rounding-to-zero risk as the rating deltas,
        // and easier to hit -- the ratio clamp's floor (0.5 today, dropping to 0.05 once
        // weapon-weighting lands) times points_per_kill can land under 1 point well before
        // rating's much larger skill-gap requirement does. Rounded to 2 decimals, not 0.
        decimal killPoints = Math.Round((decimal)(pointsPerKill * ratio), 2, MidpointRounding.AwayFromZero);
        AddPoints(attackerSteamId64, attackerName, season, killPoints);

        string mapName = osbase?.currentMap ?? Server.MapName ?? "";

        // Assist: a small, flat, opponent-blind reward for both rating and points -- an
        // assist contributed to the duel, it didn't win it, and shouldn't be able to move a
        // rating or a points total the way a kill does.
        var assister = eventInfo.Assister;
        if (IsRealPlayer(assister) && assister!.SteamID != attackerSteamId64 && assister.SteamID != victimSteamId64) {
            ulong assisterSteamId64 = assister.SteamID;
            string assisterName = CleanName(assister.PlayerName);

            SeedRating(assisterSteamId64);
            liveRating[assisterSteamId64] += assistReward;
            SetPendingRating(assisterSteamId64, assisterName, liveRating[assisterSteamId64]);

            decimal assistPoints = Math.Round(killPoints * (decimal)pointsAssistFraction, 2, MidpointRounding.AwayFromZero);
            AddPoints(assisterSteamId64, assisterName, season, assistPoints);

            // Found 2026-08-04 (agent-chat #10): this award used to touch only the current-
            // state tables, with no replayable row -- elo_kill_event's "rebuild the whole
            // ladder" claim didn't hold for assists. RelatedAttacker/Victim tie this row back
            // to the duel it grew out of, same "save what it was built on" reasoning as
            // PendingKillEvent itself.
            pendingBonusEvents.Add(new PendingBonusEvent {
                Kind = "assist",
                SteamId64 = assisterSteamId64,
                Name = assisterName,
                RatingDelta = assistReward,
                PointsDelta = assistPoints,
                Season = season,
                MapName = mapName,
                MatchId = currentMatchId,
                RelatedAttackerSteamId64 = attackerSteamId64,
                RelatedVictimSteamId64 = victimSteamId64,
                Stamp = DateTime.UtcNow
            });
        }

        pendingKillEvents.Add(new PendingKillEvent {
            MatchId = currentMatchId, // tag only now, nullable -- see class comment
            MapName = mapName,
            AttackerName = attackerName,
            AttackerSteamId64 = attackerSteamId64,
            AttackerRatingBefore = attackerRating,
            AttackerDelta = attackerDelta,
            AttackerPointsDelta = killPoints,
            VictimName = victimName,
            VictimSteamId64 = victimSteamId64,
            VictimRatingBefore = victimRating,
            VictimDelta = victimDelta,
            Weapon = NormalizeWeapon(eventInfo.Weapon),
            Headshot = eventInfo.Headshot,
            VictimActiveWeapon = lastEquippedWeapon.GetValueOrDefault(victimSteamId64),
            VictimBestWeapon = bestWeaponThisRound.TryGetValue(victimSteamId64, out var best) ? best.Weapon : null
        });

        SetPendingRating(attackerSteamId64, attackerName, liveRating[attackerSteamId64]);
        SetPendingRating(victimSteamId64, victimName, liveRating[victimSteamId64]);

        return HookResult.Continue;
    }

    private void AddPoints(ulong steamId64, string name, string season, decimal points) {
        if (steamId64 == 0 || points == 0) {
            return;
        }

        SeedPoints(steamId64, season);
        var liveKey = (steamId64, season);
        livePoints[liveKey] = livePoints.GetValueOrDefault(liveKey, 0) + points;

        var key = (steamId64, season);
        if (!pendingPoints.TryGetValue(key, out var pending)) {
            pending = new PendingPoints();
            pendingPoints[key] = pending;
        }

        pending.Name = name;
        pending.Points += points;
    }

    // Found 2026-08-04, per direct user ask, corrected same day after agent-chat #18:
    // teamkill/suicide penalty, applied to POINTS (via the same AddPoints path a round-win
    // uses, just negative) and logged as a replayable elo_bonus_event row -- never rating,
    // see the field comment on teamkillPointsPenalty/suicidePointsPenalty for why.
    private void ApplyPenalty(ulong steamId64, string name, string kind, int pointsPenalty,
                               string season, string mapName, ulong? relatedVictimSteamId64) {
        if (steamId64 == 0 || pointsPenalty == 0) {
            return;
        }

        AddPoints(steamId64, name, season, pointsPenalty);

        pendingBonusEvents.Add(new PendingBonusEvent {
            Kind = kind,
            SteamId64 = steamId64,
            Name = name,
            RatingDelta = 0,
            PointsDelta = pointsPenalty,
            Season = season,
            MapName = mapName,
            MatchId = currentMatchId,
            RelatedVictimSteamId64 = relatedVictimSteamId64,
            Stamp = DateTime.UtcNow
        });
    }

    private void SeedPoints(ulong steamId64, string season) {
        var key = (steamId64, season);
        if (db == null || livePoints.ContainsKey(key)) {
            return;
        }

        try {
            DataTable table = db.select(
                $"points FROM {PointsTable} WHERE steamid64=@id AND season=@season",
                new MySqlParameter("@id", steamId64.ToString()),
                new MySqlParameter("@season", season)
            );

            livePoints[key] = table.Rows.Count > 0 ? Convert.ToDecimal(table.Rows[0]["points"]) : 0m;
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed seeding points for {steamId64}/{season}: {e.Message}");
            livePoints[key] = 0m;
        }
    }

    // Public read for other modules (DamageReport's player_daily_stat rating/points
    // snapshot, ask 22) -- part two (points) only, current season, always live. Still an
    // int out param, same reasoning as TryGetRating: this is a display/consumption read of
    // the DECIMAL(12,2) accumulator (livePoints), not the accumulator itself, so rounding
    // here is correct, not a regression of the 2026-08-04 points fix.
    public bool TryGetPoints(ulong steamId64, string season, out int points) {
        points = 0;

        if (steamId64 == 0) {
            return false;
        }

        SeedPoints(steamId64, season);

        if (!livePoints.TryGetValue((steamId64, season), out decimal exact)) {
            return false;
        }

        points = (int)Math.Round(exact, MidpointRounding.AwayFromZero);
        return true;
    }

    private void SetPendingRating(ulong steamId64, string name, decimal rating) {
        if (!pendingRatings.TryGetValue(steamId64, out var pending)) {
            pending = new PendingRating();
            pendingRatings[steamId64] = pending;
        }

        pending.Name = name;
        pending.Rating = rating;
        pending.MatchesDelta += 1;
    }

    // Hands the pending kill log + rating snapshots to a background task; rows the
    // database does not confirm are merged back and retried on a later flush, so a
    // temporary outage only delays persistence instead of losing it. liveRating stays
    // correct throughout -- this only decides when that becomes durable.
    private void FlushPendingWrites(string source) {
        var database = db;
        if (database == null || flushInProgress) {
            return;
        }

        if (pendingKillEvents.Count == 0 && pendingRatings.Count == 0 && pendingPoints.Count == 0
            && pendingBonusEvents.Count == 0) {
            return;
        }

        var killBatch = pendingKillEvents.ToList();
        pendingKillEvents.Clear();

        var ratingBatch = pendingRatings.ToList();
        pendingRatings.Clear();

        var pointsBatch = pendingPoints.ToList();
        pendingPoints.Clear();

        var bonusBatch = pendingBonusEvents.ToList();
        pendingBonusEvents.Clear();

        flushInProgress = true;

        Task.Run(() => {
            // Perf fix (osbase-stat-contracts.md section 5), same as DamageReport.cs: one
            // transaction instead of one connection/round-trip per row, and the caller now
            // delays invoking this flush so it lands off the exact round-end tick. All-or-
            // nothing per flush; on failure the whole batch goes back for retry.
            var writes = new List<(string query, MySqlParameter[] parameters)>();

            foreach (var kill in killBatch) {
                writes.Add(($"INTO {KillEventTable} (match_id, stamp, mapname, attacker, attackerid64, attacker_rating_before, attacker_delta, " +
                    "attacker_points_delta, victim, victimid64, victim_rating_before, victim_delta, weapon, headshot, " +
                    "victim_active_weapon, victim_best_weapon) " +
                    "VALUES (@match_id, NOW(), @mapname, @attacker, @attackerid64, @attacker_rb, @attacker_delta, " +
                    "@attacker_points_delta, @victim, @victimid64, @victim_rb, @victim_delta, @weapon, @headshot, " +
                    "@victim_active_weapon, @victim_best_weapon)",
                    new MySqlParameter[] {
                        new("@match_id", (object?)kill.MatchId ?? DBNull.Value),
                        new("@mapname", kill.MapName),
                        new("@attacker", kill.AttackerName),
                        new("@attackerid64", kill.AttackerSteamId64.ToString()),
                        new("@attacker_rb", kill.AttackerRatingBefore),
                        new("@attacker_delta", kill.AttackerDelta),
                        new("@attacker_points_delta", kill.AttackerPointsDelta),
                        new("@victim", kill.VictimName),
                        new("@victimid64", kill.VictimSteamId64.ToString()),
                        new("@victim_rb", kill.VictimRatingBefore),
                        new("@victim_delta", kill.VictimDelta),
                        new("@weapon", kill.Weapon),
                        new("@headshot", kill.Headshot ? 1 : 0),
                        new("@victim_active_weapon", (object?)kill.VictimActiveWeapon ?? DBNull.Value),
                        new("@victim_best_weapon", (object?)kill.VictimBestWeapon ?? DBNull.Value)
                    }));
            }

            foreach (var kv in ratingBatch) {
                ulong steamId64 = kv.Key;
                var pending = kv.Value;

                writes.Add(($"INTO {RatingTable} (steamid64, name, rating, matches, updated_at) " +
                    "VALUES (@steamid64, @name, @rating, @matches, NOW()) " +
                    "ON DUPLICATE KEY UPDATE name=@name, rating=@rating, matches=matches+@matches, updated_at=NOW()",
                    new MySqlParameter[] {
                        new("@steamid64", steamId64.ToString()),
                        new("@name", pending.Name),
                        new("@rating", pending.Rating),
                        new("@matches", pending.MatchesDelta)
                    }));
            }

            foreach (var kv in pointsBatch) {
                var (steamId64, season) = kv.Key;
                var pending = kv.Value;

                writes.Add(($"INTO {PointsTable} (steamid64, season, name, points, updated_at) " +
                    "VALUES (@steamid64, @season, @name, @points, NOW()) " +
                    "ON DUPLICATE KEY UPDATE name=@name, points=points+@points, updated_at=NOW()",
                    new MySqlParameter[] {
                        new("@steamid64", steamId64.ToString()),
                        new("@season", season),
                        new("@name", pending.Name),
                        new("@points", pending.Points)
                    }));
            }

            foreach (var bonus in bonusBatch) {
                writes.Add(($"INTO {BonusEventTable} (kind, match_id, stamp, mapname, season, name, steamid64, " +
                    "rating_delta, points_delta, related_attacker_id64, related_victim_id64) " +
                    "VALUES (@kind, @match_id, @stamp, @mapname, @season, @name, @steamid64, " +
                    "@rating_delta, @points_delta, @related_attacker, @related_victim)",
                    new MySqlParameter[] {
                        new("@kind", bonus.Kind),
                        new("@match_id", (object?)bonus.MatchId ?? DBNull.Value),
                        new("@stamp", bonus.Stamp),
                        new("@mapname", bonus.MapName),
                        new("@season", bonus.Season),
                        new("@name", bonus.Name),
                        new("@steamid64", bonus.SteamId64.ToString()),
                        new("@rating_delta", bonus.RatingDelta),
                        new("@points_delta", bonus.PointsDelta),
                        new("@related_attacker", (object?)bonus.RelatedAttackerSteamId64?.ToString() ?? DBNull.Value),
                        new("@related_victim", (object?)bonus.RelatedVictimSteamId64?.ToString() ?? DBNull.Value)
                    }));
            }

            bool ok = writes.Count == 0 || database.ExecuteTransaction(writes) > 0;

            var unwrittenKills = ok ? new() : killBatch;
            var unwrittenRatings = ok ? new() : ratingBatch;
            var unwrittenPoints = ok ? new() : pointsBatch;
            var unwrittenBonus = ok ? new() : bonusBatch;

            Server.NextFrame(() => {
                flushInProgress = false;

                // Put retried kills back in front so the log stays chronological.
                pendingKillEvents.InsertRange(0, unwrittenKills);
                pendingBonusEvents.InsertRange(0, unwrittenBonus);

                foreach (var kv in unwrittenRatings) {
                    MergePendingRating(kv.Key, kv.Value);
                }

                foreach (var kv in unwrittenPoints) {
                    if (!pendingPoints.TryGetValue(kv.Key, out var existing)) {
                        pendingPoints[kv.Key] = kv.Value;
                    } else {
                        existing.Name = kv.Value.Name;
                        existing.Points += kv.Value.Points;
                    }
                }

                int unwritten = unwrittenKills.Count + unwrittenRatings.Count + unwrittenPoints.Count + unwrittenBonus.Count;
                if (unwritten > 0) {
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}] database unavailable ({source}): kept {unwritten} rows cached for retry.");
                } else {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] flushed pending DB writes ({source}): kills={killBatch.Count}, ratingRows={ratingBatch.Count}, pointsRows={pointsBatch.Count}, bonusRows={bonusBatch.Count}");
                }
            });
        });
    }

    // A newer pending entry for the same player (from kills that happened after this
    // flush started) already carries the freshest rating -- only the retried match count
    // needs folding back in, never the rating itself.
    private void MergePendingRating(ulong steamId64, PendingRating rating) {
        if (pendingRatings.TryGetValue(steamId64, out var existing)) {
            existing.MatchesDelta += rating.MatchesDelta;
        } else {
            pendingRatings[steamId64] = rating;
        }
    }

    // ----- round / map hooks -----

    private void OnMapStart(string mapName) {
        if (!isActive) {
            return;
        }

        pendingFlushTimer?.Kill();
        pendingFlushTimer = null;
        FlushPendingWrites("MapStart");
        RefreshMatchWindow("MapStart");
    }

    private HookResult OnRoundEnd(EventRoundEnd eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        if (statsGateOpen && pointsPerRoundWin != 0) {
            string season = CurrentSeason();
            string mapName = osbase?.currentMap ?? Server.MapName ?? "";
            DateTime stamp = DateTime.UtcNow;

            foreach (var p in Utilities.GetPlayers()) {
                if (!IsRealPlayer(p) || p!.TeamNum != eventInfo.Winner) {
                    continue;
                }

                string name = CleanName(p.PlayerName);
                AddPoints(p.SteamID, name, season, pointsPerRoundWin);

                // Found 2026-08-04 (agent-chat #10): same gap as the assist award above --
                // round-win points touched elo_points directly with no replayable row. No
                // rating change and no duel to point back to, unlike assist, so RatingDelta
                // is 0 and both Related*SteamId64 stay null.
                pendingBonusEvents.Add(new PendingBonusEvent {
                    Kind = "round_win",
                    SteamId64 = p.SteamID,
                    Name = name,
                    RatingDelta = 0,
                    PointsDelta = pointsPerRoundWin,
                    Season = season,
                    MapName = mapName,
                    MatchId = currentMatchId,
                    Stamp = stamp
                });
            }
        }

        // Capture is already done above; only the flush's transaction is delayed off the
        // exact round-end tick (osbase-stat-contracts.md section 5).
        pendingFlushTimer?.Kill();
        pendingFlushTimer = osbase?.AddTimer(RoundEndFlushDelaySeconds, () => {
            pendingFlushTimer = null;
            FlushPendingWrites("RoundEnd");
        });

        return HookResult.Continue;
    }

    // ----- leaderboard -----

    private void OnEloTopCommand(CCSPlayerController? player, CommandInfo commandInfo) {
        if (!isActive || player == null || !player.IsValid || db == null) {
            return;
        }

        try {
            DataTable table = db.select(
                $"name, steamid64, rating FROM {RatingTable} ORDER BY rating DESC LIMIT @limit",
                new MySqlParameter("@limit", topLimit)
            );

            player.PrintToChat($" {ChatColors.Green}{chatPrefix}{ChatColors.Default}: Elo leaderboard:");

            int rank = 1;
            ulong self = player.SteamID;

            foreach (DataRow row in table.Rows) {
                string name = row["name"]?.ToString() ?? "Unknown";
                int rating = Convert.ToInt32(row["rating"]);
                TryGetUInt64(row["steamid64"], out ulong steamId64);

                string color = steamId64 == self ? ChatColors.Green.ToString() : ChatColors.Default.ToString();
                player.PrintToChat($"  {color}{rank}. {name}: {rating}{ChatColors.Default}");
                rank++;
            }

            if (table.Rows.Count == 0) {
                player.PrintToChat($" {ChatColors.Default}No Elo history yet.");
            }
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed showing leaderboard: {e.Message}");
            player.PrintToChat($" {ChatColors.Red}{chatPrefix}: Failed to load leaderboard.{ChatColors.Default}");
        }
    }

    private void OnEloPointsTopCommand(CCSPlayerController? player, CommandInfo commandInfo) {
        if (!isActive || player == null || !player.IsValid || db == null) {
            return;
        }

        try {
            string season = CurrentSeason();

            DataTable table = db.select(
                $"name, steamid64, points FROM {PointsTable} WHERE season=@season ORDER BY points DESC LIMIT @limit",
                new MySqlParameter("@season", season),
                new MySqlParameter("@limit", topLimit)
            );

            player.PrintToChat($" {ChatColors.Green}{chatPrefix}{ChatColors.Default}: Points leaderboard ({season}):");

            int rank = 1;
            ulong self = player.SteamID;

            foreach (DataRow row in table.Rows) {
                string name = row["name"]?.ToString() ?? "Unknown";
                int points = Convert.ToInt32(row["points"]);
                TryGetUInt64(row["steamid64"], out ulong steamId64);

                string color = steamId64 == self ? ChatColors.Green.ToString() : ChatColors.Default.ToString();
                player.PrintToChat($"  {color}{rank}. {name}: {points}{ChatColors.Default}");
                rank++;
            }

            if (table.Rows.Count == 0) {
                player.PrintToChat($" {ChatColors.Default}No points this season yet.");
            }
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed showing points leaderboard: {e.Message}");
            player.PrintToChat($" {ChatColors.Red}{chatPrefix}: Failed to load points leaderboard.{ChatColors.Default}");
        }
    }

    // ----- player-facing chat commands (!elorank / !elotop, see rankCommand/topCommand) -----
    //
    // Distinct from css_elo_top/css_elo_points_top above: those are admin-facing console
    // commands showing rating/points alone; these are the community's own !rank/!top,
    // reading across three tables this module doesn't own (player_duel_total,
    // player_round_stat, player_daily_stat, all DamageReport's) via plain read-only SQL on
    // this module's own Database instance -- same "shared table, no RPC" pattern OSWeb uses
    // against OSBase's tables. No write ever goes the other way.
    private HookResult OnPlayerChat(EventPlayerChat eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        if (eventInfo?.Userid == null || string.IsNullOrWhiteSpace(eventInfo.Text)) {
            return HookResult.Continue;
        }

        CCSPlayerController? player = Utilities.GetPlayerFromUserid(eventInfo.Userid);
        if (player == null || !player.IsValid) {
            return HookResult.Continue;
        }

        string text = eventInfo.Text.Trim();

        if (text.Equals(rankCommand, StringComparison.OrdinalIgnoreCase)) {
            ShowRankCommand(player);
        } else if (text.Equals(topCommand, StringComparison.OrdinalIgnoreCase)) {
            ShowTopCommand(player);
        }

        return HookResult.Continue;
    }

    // Field order here is deliberately one PrintToChat call per line, not one fused format
    // string -- reordering which stat leads (points vs. rating vs. something else, the
    // owner's call, still pending) is a cut-and-paste of a line, not a rewrite.
    private void ShowRankCommand(CCSPlayerController player) {
        if (db == null) {
            return;
        }

        ulong steamId64 = player.SteamID;
        string season = CurrentSeason();

        try {
            DataTable pointsRow = db.select(
                $"points FROM {PointsTable} WHERE steamid64=@id AND season=@season",
                new MySqlParameter("@id", steamId64.ToString()),
                new MySqlParameter("@season", season)
            );
            // Found while making points DECIMAL (2026-08-04): compare on the exact value, not
            // the rounded display one -- two players who both round to the same displayed
            // point total but differ underneath would otherwise miscount each other's rank at
            // the boundary. Rounding happens once, below, only for the printed line.
            decimal pointsExact = pointsRow.Rows.Count > 0 ? Convert.ToDecimal(pointsRow.Rows[0]["points"]) : 0m;
            int points = (int)Math.Round(pointsExact, MidpointRounding.AwayFromZero);

            DataTable rankRow = db.select(
                $"COUNT(*) + 1 AS rnk FROM {PointsTable} WHERE season=@season AND points > @points",
                new MySqlParameter("@season", season),
                new MySqlParameter("@points", pointsExact)
            );
            int rank = rankRow.Rows.Count > 0 ? Convert.ToInt32(rankRow.Rows[0]["rnk"]) : 1;

            DataTable totalRow = db.select(
                $"COUNT(*) AS cnt FROM {PointsTable} WHERE season=@season",
                new MySqlParameter("@season", season)
            );
            int total = totalRow.Rows.Count > 0 ? Convert.ToInt32(totalRow.Rows[0]["cnt"]) : 0;

            TryGetRating(steamId64, out int rating, out _);

            // player_duel_total: DamageReport-owned (ask 16a/24 roll-up). Read-only here.
            DataTable duelRow = db.select(
                "kills, deaths, headshots, assists FROM player_duel_total WHERE steamid64=@id AND season=@season",
                new MySqlParameter("@id", steamId64.ToString()),
                new MySqlParameter("@season", season)
            );
            int kills = 0, deaths = 0, headshots = 0, assists = 0;
            if (duelRow.Rows.Count > 0) {
                kills = Convert.ToInt32(duelRow.Rows[0]["kills"]);
                deaths = Convert.ToInt32(duelRow.Rows[0]["deaths"]);
                headshots = Convert.ToInt32(duelRow.Rows[0]["headshots"]);
                assists = Convert.ToInt32(duelRow.Rows[0]["assists"]);
            }

            // player_round_stat: DamageReport-owned, keyed by (steamid64, side, season, map)
            // -- summed across every side/map for a season total, since neither dimension is
            // wanted here.
            DataTable roundRow = db.select(
                "SUM(rounds) AS total_rounds, SUM(rounds_won) AS total_won FROM player_round_stat WHERE steamid64=@id AND season=@season",
                new MySqlParameter("@id", steamId64.ToString()),
                new MySqlParameter("@season", season)
            );
            int totalRounds = 0, wonRounds = 0;
            if (roundRow.Rows.Count > 0 && roundRow.Rows[0]["total_rounds"] != DBNull.Value) {
                totalRounds = Convert.ToInt32(roundRow.Rows[0]["total_rounds"]);
                wonRounds = Convert.ToInt32(roundRow.Rows[0]["total_won"]);
            }
            int lostRounds = totalRounds - wonRounds;

            // player_daily_stat: DamageReport-owned, keyed by calendar day -- summed over the
            // season's date range since the table itself carries no season column.
            var (seasonStart, seasonEnd) = SeasonDateRange(season);
            DataTable secondsRow = db.select(
                "SUM(seconds) AS total_seconds FROM player_daily_stat WHERE steamid64=@id AND day BETWEEN @start AND @end",
                new MySqlParameter("@id", steamId64.ToString()),
                new MySqlParameter("@start", seasonStart),
                new MySqlParameter("@end", seasonEnd)
            );
            long totalSeconds = 0;
            if (secondsRow.Rows.Count > 0 && secondsRow.Rows[0]["total_seconds"] != DBNull.Value) {
                totalSeconds = Convert.ToInt64(secondsRow.Rows[0]["total_seconds"]);
            }

            double hsPct = kills > 0 ? 100.0 * headshots / kills : 0.0;
            double kd = deaths > 0 ? (double)kills / deaths : kills;

            player.PrintToChat($" {ChatColors.Green}{chatPrefix}{ChatColors.Default}: Din placering: #{rank}/{total}");
            player.PrintToChat($"  Poäng: {FormatThousands(points)}          (Rating: {FormatThousands(rating)})");
            player.PrintToChat($"  Kills: {kills} (Headshots: {headshots}) | Deaths: {deaths} | Assists: {assists}");
            player.PrintToChat($"  Vunna rundor: {wonRounds} | Förlorade: {lostRounds}");
            player.PrintToChat($"  Headshot: {hsPct.ToString("F1", CultureInfo.InvariantCulture)}% | KD: {kd.ToString("F2", CultureInfo.InvariantCulture)}");
            player.PrintToChat($"  Speltid denna period: {FormatPlaytime(totalSeconds)}");
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed showing rank for {steamId64}: {e.Message}");
            player.PrintToChat($" {ChatColors.Red}{chatPrefix}: Kunde inte hämta din statistik.{ChatColors.Default}");
        }
    }

    private void ShowTopCommand(CCSPlayerController player) {
        if (db == null) {
            return;
        }

        try {
            string season = CurrentSeason();

            DataTable table = db.select(
                $"name, steamid64, points FROM {PointsTable} WHERE season=@season ORDER BY points DESC LIMIT @limit",
                new MySqlParameter("@season", season),
                new MySqlParameter("@limit", topLimit)
            );

            player.PrintToChat($" {ChatColors.Green}{chatPrefix}{ChatColors.Default}: Poängtoppen ({season}):");

            int rank = 1;
            ulong self = player.SteamID;

            foreach (DataRow row in table.Rows) {
                string name = row["name"]?.ToString() ?? "Unknown";
                int points = Convert.ToInt32(row["points"]);
                TryGetUInt64(row["steamid64"], out ulong steamId64);

                string color = steamId64 == self ? ChatColors.Green.ToString() : ChatColors.Default.ToString();
                player.PrintToChat($"  {color}{rank}. {name}: {FormatThousands(points)}{ChatColors.Default}");
                rank++;
            }

            if (table.Rows.Count == 0) {
                player.PrintToChat($" {ChatColors.Default}Inga poäng denna period ännu.");
            }
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed showing !top: {e.Message}");
            player.PrintToChat($" {ChatColors.Red}{chatPrefix}: Kunde inte hämta topplistan.{ChatColors.Default}");
        }
    }

    private static (DateTime Start, DateTime End) SeasonDateRange(string season) {
        int qIdx = season.IndexOf('Q');
        if (qIdx <= 0 || !int.TryParse(season.AsSpan(0, qIdx), out int year) || !int.TryParse(season.AsSpan(qIdx + 1), out int quarter)) {
            DateTime now = DateTime.UtcNow.Date;
            return (now, now);
        }

        int startMonth = ((quarter - 1) * 3) + 1;
        DateTime start = new DateTime(year, startMonth, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime end = start.AddMonths(3).AddDays(-1);
        return (start, end);
    }

    private static string FormatThousands(int n) {
        return n.ToString("N0", CultureInfo.InvariantCulture).Replace(",", " ");
    }

    private static string FormatPlaytime(long totalSeconds) {
        if (totalSeconds <= 0) {
            return "0 timmar";
        }

        TimeSpan ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.Days > 0) {
            return $"{ts.Days} dagar, {ts.Hours} timmar";
        }
        if (ts.Hours > 0) {
            return $"{ts.Hours} timmar, {ts.Minutes} minuter";
        }
        return $"{ts.Minutes} minuter";
    }

    // ----- admin: open/close the scoring window on the site's tournament_match row -----
    //
    // These SET the window, they are not their own source of truth -- the site's
    // tournament_match row is (id, server_address, starts_at, ends_at as unix seconds;
    // confirmed contract, see docs/osbase-elo-contract.md on the OSWeb side and
    // ELO-MODULE.md here). Every write is preceded by an IsThisServer() check so a typo'd
    // id can't open or close a match that belongs to a different server.

    private void OnEloMatchStartCommand(CCSPlayerController? player, CommandInfo commandInfo) {
        if (!isActive || db == null || !RequireAdmin(player)) {
            return;
        }

        if (!int.TryParse(commandInfo.GetArg(1), out int matchId)) {
            RespondTo(player, "Usage: css_elo_match_start <match_id>");
            return;
        }

        try {
            DataTable row = db.select(
                $"id, server_address FROM {MatchTable} WHERE id=@id",
                new MySqlParameter("@id", matchId)
            );

            if (row.Rows.Count == 0) {
                RespondTo(player, $"No tournament_match row with id {matchId}.");
                return;
            }

            string? addr = row.Rows[0]["server_address"]?.ToString();
            if (!IsThisServer(addr)) {
                RespondTo(player, $"Match {matchId} is assigned to a different server (server_address='{addr}').");
                return;
            }

            db.update(
                $"{MatchTable} SET starts_at=UNIX_TIMESTAMP() WHERE id=@id",
                new MySqlParameter("@id", matchId)
            );

            RespondTo(player, $"Match {matchId}: Elo scoring window opened.");
            RefreshMatchWindow("MatchStart");
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] css_elo_match_start failed: {e.Message}");
            RespondTo(player, "Failed to open the Elo scoring window -- see server console.");
        }
    }

    private void OnEloMatchStopCommand(CCSPlayerController? player, CommandInfo commandInfo) {
        if (!isActive || db == null || !RequireAdmin(player)) {
            return;
        }

        try {
            int matchId;

            if (commandInfo.ArgCount > 1 && int.TryParse(commandInfo.GetArg(1), out int explicitId)) {
                DataTable row = db.select(
                    $"id, server_address FROM {MatchTable} WHERE id=@id",
                    new MySqlParameter("@id", explicitId)
                );

                if (row.Rows.Count == 0) {
                    RespondTo(player, $"No tournament_match row with id {explicitId}.");
                    return;
                }

                if (!IsThisServer(row.Rows[0]["server_address"]?.ToString())) {
                    RespondTo(player, $"Match {explicitId} is assigned to a different server (server_address='{row.Rows[0]["server_address"]}').");
                    return;
                }

                matchId = explicitId;
            } else {
                // No id given: close whichever match this server currently has open.
                // server_address can't be filtered in SQL (free text, needs canonicalization),
                // so pull every open match and pick this server's most recently started one.
                DataTable open = db.select(
                    $"id, server_address, starts_at FROM {MatchTable} WHERE starts_at IS NOT NULL AND ends_at IS NULL"
                );

                int? found = null;
                long bestStart = long.MinValue;

                foreach (DataRow candidate in open.Rows) {
                    if (!IsThisServer(candidate["server_address"]?.ToString())) {
                        continue;
                    }

                    long startedAt = Convert.ToInt64(candidate["starts_at"]);
                    if (startedAt > bestStart) {
                        bestStart = startedAt;
                        found = Convert.ToInt32(candidate["id"]);
                    }
                }

                if (!found.HasValue) {
                    RespondTo(player, "No open match found for this server -- pass a match id explicitly.");
                    return;
                }

                matchId = found.Value;
            }

            db.update(
                $"{MatchTable} SET ends_at=UNIX_TIMESTAMP() WHERE id=@id",
                new MySqlParameter("@id", matchId)
            );

            RespondTo(player, $"Match {matchId}: Elo scoring window closed.");
            FlushPendingWrites("MatchStop");
            RefreshMatchWindow("MatchStop");
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] css_elo_match_stop failed: {e.Message}");
            RespondTo(player, "Failed to close the Elo scoring window -- see server console.");
        }
    }

    private bool RequireAdmin(CCSPlayerController? player) {
        // Console (player == null) is trusted; an in-game caller needs the configured permission.
        if (player == null) {
            return true;
        }

        if (!player.IsValid || !AdminManager.PlayerHasPermissions(player, adminPermission)) {
            RespondTo(player, "You do not have permission to do that.");
            return false;
        }

        return true;
    }

    private void RespondTo(CCSPlayerController? player, string message) {
        if (player != null && player.IsValid) {
            player.PrintToChat($" {ChatColors.Green}{chatPrefix}{ChatColors.Default}: {message}");
        }

        Console.WriteLine($"[INFO] OSBase[{ModuleName}] {message}");
    }

    // ----- helpers -----

    private static bool IsRealPlayer(CCSPlayerController? player) {
        if (player == null || !player.IsValid || !player.UserId.HasValue || player.IsHLTV || player.IsBot) {
            return false;
        }

        return player.SteamID > 0;
    }

    private static string NormalizeWeapon(string? weapon) {
        string normalized = (weapon ?? string.Empty).Trim().ToLowerInvariant();

        if (normalized.StartsWith("weapon_", StringComparison.Ordinal)) {
            normalized = normalized.Substring("weapon_".Length);
        }

        return normalized;
    }

    private static string CleanName(string? name) {
        string clean = name ?? "Unknown";
        clean = clean.Replace('\n', ' ').Replace('\r', ' ').Trim();

        if (clean.Length == 0) {
            clean = "Unknown";
        }

        if (clean.Length > 64) {
            clean = clean.Substring(0, 64);
        }

        return clean;
    }

    private static string Unquote(string value) {
        if (string.IsNullOrWhiteSpace(value)) {
            return string.Empty;
        }

        if (value.Length >= 2 && value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal)) {
            return value.Substring(1, value.Length - 2);
        }

        return value;
    }

    private static int ParseInt(string value, int defaultValue, int min, int max) {
        if (!int.TryParse(value, out int parsed)) {
            return defaultValue;
        }

        return Math.Clamp(parsed, min, max);
    }

    private static double ParseDouble(string value, double defaultValue, double min, double max) {
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)) {
            return defaultValue;
        }

        return Math.Clamp(parsed, min, max);
    }

    private static bool TryGetUInt64(object? value, out ulong result) {
        result = 0;

        if (value == null || value == DBNull.Value) {
            return false;
        }

        return ulong.TryParse(value.ToString(), out result);
    }
}
