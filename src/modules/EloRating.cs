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
// Part one -- RATING (elo_rating, keyed by steamid64+season since 2026Q4, see
// osbase-order-2026Q4.md section 1). A pure zero-sum Elo duel: kills/deaths move it, nothing
// else does (section 2 -- the old headshot multiplier and the flat assist reward both
// created rating out of nothing and inflated the field from 1000 to ~3000 in one quarter).
// Every season starts everyone at start_rating with matches=0; last quarter's rows stay.
//
// Part two -- POINTS (elo_points, keyed by steamid64+season, reset every quarter by simply
// starting a new season string -- no archiving step, same reason every other table in this
// system uses season-in-the-key instead of cs2rank's table-rename approach: a reset that
// requires a step is a reset that one day skips the step, and this community's rule is
// nothing gets deleted). Since 2026Q4 the formula is the site's PointsFormula (helpers/
// PointsFormula.cs, values from the site-owned points_formula table): a kill is worth more
// the higher the victim stands on the season's points board relative to the attacker, rating
// nudges it +-W, weapon weight multiplies, headshot adds a flat bonus, and a death costs
// DEATH_SHARE of the attacker's placement base. Everyone starts at START_POINTS.
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

    // Site-owned points_formula (osbase-order-2026Q4.md section 6), same schema-qualified
    // config shape as weapon_weight_table below. Re-read at every round start so a change on
    // /admin/poangformel applies from the next round without a restart. Until a read has
    // landed (or if the table is unreachable) the formula runs on PointsFormula.Defaults and
    // every failed read logs an ERROR -- the numbers are the reference values, not a guess,
    // but the order is explicit that the table is the source of truth.
    private string pointsFormulaTable = "";
    private PointsFormula formula = new();
    private bool formulaLoaded;

    // Ask 11: no bots (IsRealPlayer, unchanged), no warmup (hard rule, not configurable --
    // same as DamageReport/TeamBets), min_players (configurable -- nobody knows the right
    // number until real data exists). Decided once per round in OnRoundStart, held for the
    // whole round so people logging off late in the evening don't retroactively disqualify a
    // round that started at full strength.
    private int minPlayers = 4;
    private bool statsGateOpen;

    // Teamkill/suicide POINTS penalties (never rating -- rating feeds the LAN balancer, and
    // shooting a teammate says nothing about aim). Both default to 0 since 2026Q4
    // (osbase-order-2026Q4.md 4c: suicide/fall/world = no points change; teamkill = owner's
    // decision pending, default nothing). Kept as config so the owner can flip either on
    // without a rebuild. Negative by convention -- applied as-is, never Math.Abs'd, so a
    // positive value fails loudly as a reward instead of silently doing nothing.
    private int teamkillPointsPenalty = 0;
    private int suicidePointsPenalty = 0;

    // Site-owned (OSWeb's weapon_point_weight, migration 0243) -- OSWeb and OSBase are
    // separate database schemas, not one shared database, so this must be schema-qualified in
    // config (e.g. "oldswedes.weapon_point_weight") and the qualifier differs between dev and
    // prod. Empty disables weighting entirely: ResolveWeaponWeight then returns 1.00 for every
    // kill and no query is ever attempted, which is both the safe default before the prod grant
    // is confirmed and the exact behavior this module already had. Refreshed on a timer, not
    // read once at load, because the site owns the values specifically so a HeadAdmin can
    // retune them without touching this plugin or restarting the server.
    private string weaponWeightTable = "";
    private int weaponWeightRefreshSeconds = 60;
    private List<WeaponWeightRule> weaponWeightRules = new();
    private Timer? weaponWeightTimer;

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
    // Keyed by (steamid64, season) since 2026Q4 -- a new season is simply a new key that
    // seeds at start_rating/0 matches. The previous season's final row is read separately
    // (TryGetBalancingRating) and never written again.
    private readonly Dictionary<(ulong SteamId64, string Season), decimal> liveRating = new();
    private readonly Dictionary<(ulong SteamId64, string Season), int> liveMatches = new();

    // Last season's final (rating, matches) per player, read once and cached; null entry =
    // looked up, no row. Only the balancer read path uses it.
    private readonly Dictionary<ulong, (decimal Rating, int Matches)?> previousSeasonRating = new();

    // The attacker's kills this season BEFORE the current one -- the "värnplikt" counter
    // (PointsFormula.WarmupKills). Seeded once per (player, season) from elo_kill_event rows
    // stamped inside the season, then kept live. Counts scored duels only (teamkills and
    // suicides never reach the duel path), which is exactly what acceptance check 1 in the
    // order counts: elo_kill_event rows with this player as attacker.
    private readonly Dictionary<(ulong SteamId64, string Season), int> liveKills = new();

    // Points-board placement snapshot, taken once at map start (order section 3: "Placering
    // vid mappstart, inte live"). Standard competition ranking (1, 2, 2, 4); a player without
    // a row this season stands at boardSize + 1. Refreshed asynchronously; until the first
    // snapshot has landed on a fresh load, boardSize is 0 and Base() degrades to EVEN.
    private readonly Dictionary<ulong, int> placeAtMapStart = new();
    private int boardSizeAtMapStart;
    private string placementSeason = "";

    // Round-scoped ledger mirror for DamageReport's per-round points (order section 8):
    // what each (attacker, victim) pair earned/lost this round and each player's bonus rows
    // by kind. Cleared on round start. Read-only from outside via the public accessors.
    private readonly Dictionary<(ulong Attacker, ulong Victim), (decimal AttackerDelta, decimal VictimDelta)> roundKillPoints = new();
    private readonly Dictionary<ulong, Dictionary<string, decimal>> roundBonusPoints = new();

    // bomb_pickup also fires when the round hands a T the bomb at spawn ("Spawna med bomben:
    // ingen bonus"). A pickup only pays after a bomb_dropped has happened this round.
    private int roundBombDrops;

    // Who has died this round (any cause) -- OnBombDropped's alive check, see there.
    private readonly HashSet<ulong> diedThisRound = new();

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
    private readonly Dictionary<(ulong SteamId64, string Season), PendingRating> pendingRatings = new();
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
        // 2026Q4 columns (osbase-order-2026Q4.md section 7). VictimPointsDelta is <= 0, the
        // clipped value that was actually applied. Places/board size are the map-start
        // snapshot the kill was priced against. InAir/InWater are nullable on purpose: NULL
        // means "couldn't read it", false means "stood on the ground/dry".
        public decimal VictimPointsDelta { get; init; }
        public int AttackerPlace { get; init; }
        public int VictimPlace { get; init; }
        public int BoardSize { get; init; }
        public int RoundNo { get; init; }
        public bool? AttackerInAir { get; init; }
        public bool? AttackerInWater { get; init; }
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
    // RatingDelta is always 0 since 2026Q4 (bonuses never touch rating -- order section 2);
    // the column stays so old assist rows remain readable.
    private sealed class PendingBonusEvent {
        public required string Kind;
        public required ulong SteamId64;
        public required string Name;
        public required int RatingDelta;
        public required decimal PointsDelta;
        public required string Season;
        public required string MapName;
        public int? MatchId;
        public int RoundNo;
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

    // One row from weapon_point_weight. MatchType is "exact", "prefix", or "suffix" --
    // resolution order, not row order; see ResolveWeaponWeight.
    private sealed class WeaponWeightRule {
        public required string Pattern;
        public required string MatchType;
        public required double Multiplier;
    }

    protected override void OnLoad() {
        gameStats = osbase?.GetGameStats();

        CreateCustomConfigs();
        LoadConfig();

        db = new Database(osbase!, config!);
        CreateTables();
        StartMatchWindowTimer();
        StartWeaponWeightTimer();
        RefreshPointsFormula("Load");
        // A hot load mid-map still needs a placement snapshot -- "at map start" means "the
        // board as it stood when this map's scoring began", and for a reload that's now.
        RefreshPlacement("Load");
    }

    protected override void OnUnload() {
        StopMatchWindowTimer();
        StopWeaponWeightTimer();
        pendingFlushTimer?.Kill();
        pendingFlushTimer = null;
        FlushPendingWrites("Unload");

        liveRating.Clear();
        liveMatches.Clear();
        liveKills.Clear();
        livePoints.Clear();
        previousSeasonRating.Clear();
        placeAtMapStart.Clear();
        boardSizeAtMapStart = 0;
        placementSeason = "";
        roundKillPoints.Clear();
        roundBonusPoints.Clear();
        diedThisRound.Clear();
        weaponWeightRules.Clear();
        formula = new PointsFormula();
        formulaLoaded = false;
        currentMatchId = null;
        db?.Shutdown();
        db = null;
        gameStats = null;
    }

    protected override void OnReloadConfig() {
        gameStats = osbase?.GetGameStats();
        CreateCustomConfigs();
        LoadConfig();
        CreateTables();
        // weaponWeightTable/weaponWeightRefreshSeconds are config-driven, unlike the match
        // window's fixed interval -- restart so a changed table name or TTL takes effect
        // without a full plugin reload.
        StartWeaponWeightTimer();
        RefreshPointsFormula("ReloadConfig");
    }

    protected override void RegisterHandlers() {
        osbase?.SubscribeToEvent<EventRoundStart>(OnRoundStart);
        osbase?.SubscribeToEvent<EventPlayerDeath>(OnPlayerDeath);
        osbase?.SubscribeToEvent<EventRoundEnd>(OnRoundEnd);
        osbase?.SubscribeToEvent<EventPlayerChat>(OnPlayerChat);
        osbase?.SubscribeToEvent<EventItemEquip>(OnItemEquip);
        osbase?.SubscribeToEvent<EventItemPurchase>(OnItemPurchase);
        osbase?.SubscribeToEvent<EventItemPickup>(OnItemPickup);
        osbase?.SubscribeToEvent<EventBombPlanted>(OnBombPlanted);
        osbase?.SubscribeToEvent<EventBombDefused>(OnBombDefused);
        osbase?.SubscribeToEvent<EventBombPickup>(OnBombPickup);
        osbase?.SubscribeToEvent<EventBombDropped>(OnBombDropped);
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
        osbase?.UnsubscribeFromEvent<EventBombPlanted>(OnBombPlanted);
        osbase?.UnsubscribeFromEvent<EventBombDefused>(OnBombDefused);
        osbase?.UnsubscribeFromEvent<EventBombPickup>(OnBombPickup);
        osbase?.UnsubscribeFromEvent<EventBombDropped>(OnBombDropped);
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

        // Per-round ledger mirror for DamageReport, and the spawn-bomb pickup filter.
        roundKillPoints.Clear();
        roundBonusPoints.Clear();
        roundBombDrops = 0;
        diedThisRound.Clear();

        // Order section 6: "Läs vid rondstart" -- an admin's change on /admin/poangformel
        // applies from the next round, no restart.
        RefreshPointsFormula("RoundStart");

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
            "// Teamkill/suicide POINTS penalties. 0 = off (2026Q4 default: a suicide costs\n" +
            "// nothing, teamkill awaits the owner's decision). Negative values only -- do not\n" +
            "// flip the sign here, that would turn a penalty into a reward.\n" +
            "teamkill_points_penalty 0\n" +
            "suicide_points_penalty 0\n" +
            "// Points formula values (START_POINTS, EVEN, TOP, ... see osbase-order-2026Q4.md\n" +
            "// section 6). Site-owned points_formula, synced into our database by OSWeb --\n" +
            "// schema-qualified like weapon_weight_table below. Re-read every round start.\n" +
            "// Empty = run on the reference defaults and log an error every round.\n" +
            "points_formula_table \"\"\n" +
            "// Weapon weight multiplier on kill POINTS only (never rating). Site-owned\n" +
            "// (OSWeb's weapon_point_weight) -- OSWeb and OSBase are separate database\n" +
            "// schemas, so this must be schema-qualified (e.g.\n" +
            "// \"oldswedes.weapon_point_weight\"; dev/prod schema names differ). Empty disables\n" +
            "// weighting -- every kill prices at weight 1.00.\n" +
            "weapon_weight_table \"\"\n" +
            "weapon_weight_refresh_seconds 60\n" +
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
        teamkillPointsPenalty = 0;
        suicidePointsPenalty = 0;
        pointsFormulaTable = "";
        weaponWeightTable = "";
        weaponWeightRefreshSeconds = 60;
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
                case "teamkill_points_penalty":
                    teamkillPointsPenalty = ParseInt(value, 0, -1000, 0);
                    break;
                case "suicide_points_penalty":
                    suicidePointsPenalty = ParseInt(value, 0, -1000, 0);
                    break;
                case "points_formula_table":
                    pointsFormulaTable = value;
                    break;
                // Retired 2026Q4 (headshot_bonus_pct, assist_reward, points_base,
                // points_exponent, points_assist_fraction, points_per_round_win) --
                // deliberately not cases here so an already-deployed elorating.cfg with these
                // lines falls into the "unknown config key" warning below and gets noticed.
                case "weapon_weight_table":
                    weaponWeightTable = value;
                    break;
                case "weapon_weight_refresh_seconds":
                    weaponWeightRefreshSeconds = ParseInt(value, 60, 5, 3600);
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

        // Both of these being empty is the single most likely reason "the weights are not
        // applied" (order section 5) -- say so at load, not only when a kill prices wrong.
        if (string.IsNullOrWhiteSpace(weaponWeightTable)) {
            Console.WriteLine($"[WARN] OSBase[{ModuleName}]: weapon_weight_table is empty -- weapon weighting is OFF, every kill prices at 1.00.");
        }
        if (string.IsNullOrWhiteSpace(pointsFormulaTable)) {
            Console.WriteLine($"[WARN] OSBase[{ModuleName}]: points_formula_table is empty -- running on PointsFormula.Defaults, the site's admin page has no effect here.");
        }
    }

    // ----- tables (OSBase-owned; tournament_match is not created here, it belongs to the site) -----

    private void CreateTables() {
        if (db == null) {
            return;
        }

        // Part one: rating. season joined the key in 2026Q4 (osbase-order-2026Q4.md section
        // 1): the rating resets to start_rating every quarter and last quarter's final row
        // stays put -- same lifecycle as points now. EnsureRatingSeason() migrates a live
        // pre-2026Q4 table in place.
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
            season     VARCHAR(8) NOT NULL,
            name       VARCHAR(64) NOT NULL,
            rating     DECIMAL(12,4) NOT NULL,
            matches    INT NOT NULL DEFAULT 0,
            updated_at DATETIME NOT NULL,
            PRIMARY KEY (steamid64, season)
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
        // hit -- a lopsided kill's (1 - expectedAttacker) factor, further shrunk by a low
        // weapon weight once weapon_weight_table is configured (see ResolveWeaponWeight), can
        // land well under 1 point long before rating's much larger skill gap requirement does.
        // Same fix, same shape: store exact, round only at display (TryGetPoints,
        // css_elo_points_top, !elorank/!elotop, ShowRankCommand).
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
            victim_points_delta    DECIMAL(12,2) NOT NULL DEFAULT 0,
            attacker_place         INT NULL,
            victim_place           INT NULL,
            board_size             INT NULL,
            round_no               INT NULL,
            attacker_in_air        TINYINT(1) NULL,
            attacker_in_water      TINYINT(1) NULL,
            PRIMARY KEY (id),
            INDEX idx_elo_kill_event (match_id, stamp),
            INDEX idx_elo_kill_event_attacker (attackerid64, stamp)
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
            round_no                 INT NULL,
            PRIMARY KEY (id),
            INDEX idx_elo_bonus_event (match_id, stamp)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        try {
            db.create(ratingTable);
            db.create(pointsTable);
            db.create(killEventTable);
            db.create(bonusEventTable);

            // 2026Q4 migrations against already-deployed tables (CREATE IF NOT EXISTS is a
            // no-op there). Same EnsureColumn pattern as DamageReport/ServerInfo/TeamBets.
            EnsureRatingSeason();
            EnsureColumn(KillEventTable, "victim_points_delta", "DECIMAL(12,2) NOT NULL DEFAULT 0");
            EnsureColumn(KillEventTable, "attacker_place", "INT NULL");
            EnsureColumn(KillEventTable, "victim_place", "INT NULL");
            EnsureColumn(KillEventTable, "board_size", "INT NULL");
            EnsureColumn(KillEventTable, "round_no", "INT NULL");
            EnsureColumn(KillEventTable, "attacker_in_air", "TINYINT(1) NULL");
            EnsureColumn(KillEventTable, "attacker_in_water", "TINYINT(1) NULL");
            EnsureIndex(KillEventTable, "idx_elo_kill_event_attacker", "(attackerid64, stamp)");
            EnsureColumn(BonusEventTable, "round_no", "INT NULL");

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] tables ensured.");
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed creating tables: {e.Message}");
        }
    }

    // Every row that exists before this migration belongs to 2026Q3: Elo went live on
    // 2026-08-07 and this code ships for the 2026Q4 season start. A constant rather than
    // "the quarter of updated_at" on purpose -- a player whose last duel landed after
    // midnight Oct 1 under the old plugin still carries a Q3 rating, and labelling that row
    // Q4 would both lose their Q3 final and start their Q4 at ~3000 instead of 1000.
    private const string PreSeasonSplitSeason = "2026Q3";

    // Safe against the table's writer by construction, not timing: CreateTables() runs from
    // OnLoad before RegisterHandlers, and a hot reload has already unloaded/flushed the old
    // handlers (same argument as DamageReport.EnsureEndReasonInPrimaryKey). One ALTER so the
    // column and the key change land together.
    private void EnsureRatingSeason() {
        if (db == null) {
            return;
        }

        try {
            DataTable existing = db.select(
                "column_name FROM information_schema.columns " +
                "WHERE table_schema = DATABASE() AND table_name = @table AND column_name = 'season'",
                new MySqlParameter("@table", RatingTable)
            );

            if (existing.Rows.Count == 0) {
                db.alter(
                    $"TABLE {RatingTable} " +
                    $"ADD COLUMN season VARCHAR(8) NOT NULL DEFAULT '{PreSeasonSplitSeason}' AFTER steamid64, " +
                    "DROP PRIMARY KEY, " +
                    "ADD PRIMARY KEY (steamid64, season)"
                );
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Migrated {RatingTable}: season added to the primary key, existing rows tagged {PreSeasonSplitSeason}.");
            }
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error migrating {RatingTable} to (steamid64, season): {e.Message}");
        }
    }

    private void EnsureColumn(string table, string column, string definition) {
        if (db == null) {
            return;
        }

        try {
            DataTable existing = db.select(
                "column_name FROM information_schema.columns " +
                "WHERE table_schema = DATABASE() AND table_name = @table AND column_name = @column",
                new MySqlParameter("@table", table),
                new MySqlParameter("@column", column)
            );

            if (existing.Rows.Count == 0) {
                db.alter($"TABLE {table} ADD COLUMN {column} {definition}");
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Added missing column {table}.{column}.");
            }
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error ensuring column {table}.{column}: {e.Message}");
        }
    }

    private void EnsureIndex(string table, string index, string columns) {
        if (db == null) {
            return;
        }

        try {
            DataTable existing = db.select(
                "index_name FROM information_schema.statistics " +
                "WHERE table_schema = DATABASE() AND table_name = @table AND index_name = @index",
                new MySqlParameter("@table", table),
                new MySqlParameter("@index", index)
            );

            if (existing.Rows.Count == 0) {
                db.alter($"TABLE {table} ADD INDEX {index} {columns}");
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Added missing index {table}.{index}.");
            }
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error ensuring index {table}.{index}: {e.Message}");
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

    // ----- weapon weights: site-owned weapon_point_weight -> points multiplier, timer-refreshed -----

    private void StartWeaponWeightTimer() {
        if (!isActive || osbase == null) {
            return;
        }

        StopWeaponWeightTimer();

        if (string.IsNullOrWhiteSpace(weaponWeightTable)) {
            // Disabled by config -- ResolveWeaponWeight already returns 1.00 with an empty rule
            // list, so no query is ever attempted against a table that might not exist yet or
            // lack a grant on this schema.
            return;
        }

        RefreshWeaponWeights("Load");
        weaponWeightTimer = osbase.AddTimer(weaponWeightRefreshSeconds, () => RefreshWeaponWeights("Timer"), TimerFlags.REPEAT);
    }

    private void StopWeaponWeightTimer() {
        weaponWeightTimer?.Kill();
        weaponWeightTimer = null;
    }

    // weaponWeightTable must be schema-qualified in config -- OSWeb and OSBase are separate
    // database schemas (confirmed against config/osbase.php and .env, 2026-08-16), not one
    // shared database, so an unqualified table name resolves against OSBase's own schema and
    // finds nothing there. A failed refresh (missing grant, wrong name, table not created yet)
    // logs loudly and keeps the last known-good cache -- ResolveWeaponWeight only ever falls
    // back to 1.00 if a successful refresh has never landed, not as a silent failure mode.
    private void RefreshWeaponWeights(string source) {
        var database = db;
        if (!isActive || database == null || string.IsNullOrWhiteSpace(weaponWeightTable)) {
            return;
        }

        Task.Run(() => {
            bool ok = database.trySelect($"pattern, match_type, multiplier FROM {weaponWeightTable}", out DataTable table);

            List<WeaponWeightRule>? rules = null;
            if (ok) {
                rules = new List<WeaponWeightRule>(table.Rows.Count);
                foreach (DataRow row in table.Rows) {
                    string pattern = row["pattern"]?.ToString()?.Trim().ToLowerInvariant() ?? "";
                    string matchType = row["match_type"]?.ToString()?.Trim().ToLowerInvariant() ?? "";

                    if (pattern.Length == 0 || (matchType != "exact" && matchType != "prefix" && matchType != "suffix")) {
                        Console.WriteLine($"[WARN] OSBase[{ModuleName}] skipped unusable weapon_weight row (pattern='{pattern}', match_type='{matchType}').");
                        continue;
                    }

                    rules.Add(new WeaponWeightRule {
                        Pattern = pattern,
                        MatchType = matchType,
                        Multiplier = Convert.ToDouble(row["multiplier"])
                    });
                }
            }

            Server.NextFrame(() => {
                if (!ok || rules == null) {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] weapon weight refresh failed ({source}) against '{weaponWeightTable}'; keeping {weaponWeightRules.Count} previously-loaded rule(s).");
                    return;
                }

                if (rules.Count == 0) {
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}] weapon weight refresh ({source}) returned 0 usable rows from '{weaponWeightTable}' -- every kill prices at the 1.00 default until this has real rows.");
                }

                weaponWeightRules = rules;
            });
        });
    }

    // exact -> prefix -> suffix, first hit wins within each pass, default 1.00. The order is
    // load-bearing, not a convenience: an exact "knife" row and a prefix "knife_" row must both
    // be checked in that order so a future exact "knife_t" row (a different value than the
    // "knife_" prefix) is still reachable instead of being shadowed by the broader prefix match.
    private double ResolveWeaponWeight(string weapon) {
        if (weaponWeightRules.Count == 0 || string.IsNullOrEmpty(weapon)) {
            return 1.0;
        }

        foreach (var rule in weaponWeightRules) {
            if (rule.MatchType == "exact" && weapon == rule.Pattern) {
                return rule.Multiplier;
            }
        }

        foreach (var rule in weaponWeightRules) {
            if (rule.MatchType == "prefix" && weapon.StartsWith(rule.Pattern, StringComparison.Ordinal)) {
                return rule.Multiplier;
            }
        }

        foreach (var rule in weaponWeightRules) {
            if (rule.MatchType == "suffix" && weapon.EndsWith(rule.Pattern, StringComparison.Ordinal)) {
                return rule.Multiplier;
            }
        }

        return 1.0;
    }

    // ----- points formula: site-owned points_formula -> PointsFormula, re-read every round -----

    private void RefreshPointsFormula(string source) {
        var database = db;
        if (!isActive || database == null) {
            return;
        }

        if (string.IsNullOrWhiteSpace(pointsFormulaTable)) {
            // Already warned at load. Not every round -- that would be 16 identical lines an
            // evening for a config that isn't going to change until someone edits it.
            return;
        }

        Task.Run(() => {
            bool ok = database.trySelect($"name, value FROM {pointsFormulaTable}", out DataTable table);

            Dictionary<string, double>? values = null;
            if (ok) {
                values = new Dictionary<string, double>(table.Rows.Count);
                foreach (DataRow row in table.Rows) {
                    string name = row["name"]?.ToString()?.Trim() ?? "";
                    if (name.Length > 0 && row["value"] != DBNull.Value) {
                        values[name] = Convert.ToDouble(row["value"]);
                    }
                }
            }

            Server.NextFrame(() => {
                if (!ok || values == null) {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] points_formula read failed ({source}) against '{pointsFormulaTable}'; keeping {(formulaLoaded ? "the last loaded values" : "PointsFormula.Defaults")}.");
                    return;
                }

                foreach (var name in PointsFormula.Defaults.Keys) {
                    if (!values.ContainsKey(name)) {
                        Console.WriteLine($"[WARN] OSBase[{ModuleName}] points_formula ({source}) has no row for {name} -- using the default {PointsFormula.Defaults[name]}.");
                    }
                }

                var next = new PointsFormula(values);
                bool changed = !formulaLoaded || next.Values.Any(kv => formula.Value(kv.Key) != kv.Value);
                formula = next;
                formulaLoaded = true;

                if (changed) {
                    Console.WriteLine($"[INFO] OSBase[{ModuleName}] points_formula ({source}): " +
                        string.Join(" ", formula.Values.Select(kv => $"{kv.Key}={kv.Value.ToString(CultureInfo.InvariantCulture)}")));
                }
            });
        });
    }

    // ----- placement: the season's points board as it stood at map start -----

    // Order section 3: place = position on the season's points list at MAP START, standard
    // competition ranking on ties (1, 2, 2, 4), N = players with an elo_points row this
    // season, no row = N + 1. Read once per map (and on load), held for the whole map. Async,
    // like every other read here; a kill before the snapshot lands on a fresh map prices at
    // board size 0 (Base() -> EVEN), which is also what an empty first-map-of-the-season
    // board gives -- correct, not a fallback.
    private void RefreshPlacement(string source) {
        var database = db;
        if (!isActive || database == null) {
            return;
        }

        string season = CurrentSeason();

        Task.Run(() => {
            bool ok = database.trySelect(
                $"steamid64, points FROM {PointsTable} WHERE season=@season ORDER BY points DESC",
                out DataTable table,
                new MySqlParameter("@season", season)
            );

            var places = new Dictionary<ulong, int>();
            if (ok) {
                int place = 0;
                int rank = 0;
                decimal? previous = null;
                foreach (DataRow row in table.Rows) {
                    rank++;
                    decimal points = Convert.ToDecimal(row["points"]);
                    if (previous == null || points != previous.Value) {
                        place = rank;
                        previous = points;
                    }

                    if (TryGetUInt64(row["steamid64"], out ulong steamId64)) {
                        places[steamId64] = place;
                    }
                }
            }

            Server.NextFrame(() => {
                if (!ok) {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] placement snapshot failed ({source}); keeping the previous snapshot ({placeAtMapStart.Count} players, N={boardSizeAtMapStart}).");
                    return;
                }

                placeAtMapStart.Clear();
                foreach (var kv in places) {
                    placeAtMapStart[kv.Key] = kv.Value;
                }
                boardSizeAtMapStart = places.Count;
                placementSeason = season;
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] placement snapshot ({source}): season={season} N={boardSizeAtMapStart}");
            });
        });
    }

    private int PlaceOf(ulong steamId64) {
        return placeAtMapStart.TryGetValue(steamId64, out int place) ? place : boardSizeAtMapStart + 1;
    }

    // ----- rating seed: synchronous, once per (player, season), only on the kill path that needs it -----

    private void SeedRating(ulong steamId64, string season) {
        var key = (steamId64, season);
        if (db == null || liveRating.ContainsKey(key)) {
            return;
        }

        try {
            DataTable table = db.select(
                $"rating, matches FROM {RatingTable} WHERE steamid64=@id AND season=@season",
                new MySqlParameter("@id", steamId64.ToString()),
                new MySqlParameter("@season", season)
            );

            if (table.Rows.Count > 0) {
                liveRating[key] = Convert.ToDecimal(table.Rows[0]["rating"]);
                liveMatches[key] = Convert.ToInt32(table.Rows[0]["matches"]);
            } else {
                liveRating[key] = startRating;
                liveMatches[key] = 0;
            }
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed seeding rating for {steamId64}/{season}: {e.Message}");
            liveRating[key] = startRating;
            liveMatches[key] = 0;
        }
    }

    // The attacker's scored kills this season so far -- elo_kill_event rows as attacker,
    // stamped inside the season. stamp is the DB server's NOW(), the season range is UTC;
    // the mismatch is at most the server's UTC offset around the quarter boundary, which
    // only matters for a player sitting exactly at WARMUP_KILLS at midnight on day one.
    private void SeedKills(ulong steamId64, string season) {
        var key = (steamId64, season);
        if (db == null || liveKills.ContainsKey(key)) {
            return;
        }

        try {
            var (start, end) = SeasonHelper.Range(season);
            DataTable table = db.select(
                $"COUNT(*) AS cnt FROM {KillEventTable} WHERE attackerid64=@id AND stamp >= @start AND stamp < @end",
                new MySqlParameter("@id", steamId64.ToString()),
                new MySqlParameter("@start", start),
                new MySqlParameter("@end", end)
            );

            liveKills[key] = table.Rows.Count > 0 ? Convert.ToInt32(table.Rows[0]["cnt"]) : 0;
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed seeding kill count for {steamId64}/{season}: {e.Message}");
            liveKills[key] = 0;
        }
    }

    private (decimal Rating, int Matches)? PreviousSeasonFinal(ulong steamId64, string season) {
        if (previousSeasonRating.TryGetValue(steamId64, out var cached)) {
            return cached;
        }

        (decimal, int)? result = null;
        string? previous = SeasonHelper.PreviousSeason(season);
        if (db != null && previous != null) {
            try {
                DataTable table = db.select(
                    $"rating, matches FROM {RatingTable} WHERE steamid64=@id AND season=@season",
                    new MySqlParameter("@id", steamId64.ToString()),
                    new MySqlParameter("@season", previous)
                );

                if (table.Rows.Count > 0) {
                    result = (Convert.ToDecimal(table.Rows[0]["rating"]), Convert.ToInt32(table.Rows[0]["matches"]));
                }
            } catch (Exception e) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed reading {previous} rating for {steamId64}: {e.Message}");
            }
        }

        previousSeasonRating[steamId64] = result;
        return result;
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

        string season = CurrentSeason();
        SeedRating(steamId64, season);

        if (!liveRating.TryGetValue((steamId64, season), out decimal exact)) {
            return false;
        }

        rating = (int)Math.Round(exact, MidpointRounding.AwayFromZero);
        matches = liveMatches.GetValueOrDefault((steamId64, season), 0);
        return true;
    }

    // The balancer's read (order sections 1 and 13): this season's rating once the player has
    // cleared the provisional gate, otherwise last season's final if there is one, otherwise
    // this season's (start_rating, 0) -- which the caller treats as "unknown, use the
    // median". matches is the matches of whichever row was used, so a caller comparing
    // against min_rated_matches sees last season's count when last season is what it got.
    public bool TryGetBalancingRating(ulong steamId64, out int rating, out int matches, out string sourceSeason) {
        sourceSeason = CurrentSeason();
        if (!TryGetRating(steamId64, out rating, out matches)) {
            return false;
        }

        if (matches >= provisionalMatches) {
            return true;
        }

        var previous = PreviousSeasonFinal(steamId64, sourceSeason);
        if (previous.HasValue && previous.Value.Matches > matches) {
            rating = (int)Math.Round(previous.Value.Rating, MidpointRounding.AwayFromZero);
            matches = previous.Value.Matches;
            sourceSeason = SeasonHelper.PreviousSeason(sourceSeason) ?? sourceSeason;
        }

        return true;
    }

    // ----- per-round ledger mirror, read by DamageReport (order section 8) -----

    public bool TryGetRoundKillPoints(ulong attackerSteamId64, ulong victimSteamId64, out decimal attackerDelta, out decimal victimDelta) {
        attackerDelta = 0m;
        victimDelta = 0m;
        if (!roundKillPoints.TryGetValue((attackerSteamId64, victimSteamId64), out var deltas)) {
            return false;
        }

        attackerDelta = deltas.AttackerDelta;
        victimDelta = deltas.VictimDelta;
        return true;
    }

    public IReadOnlyDictionary<string, decimal> GetRoundBonusPoints(ulong steamId64) {
        return roundBonusPoints.TryGetValue(steamId64, out var bonuses)
            ? bonuses
            : new Dictionary<string, decimal>();
    }

    private void RecordRoundBonus(ulong steamId64, string kind, decimal points) {
        if (!roundBonusPoints.TryGetValue(steamId64, out var bonuses)) {
            bonuses = new Dictionary<string, decimal>();
            roundBonusPoints[steamId64] = bonuses;
        }

        bonuses[kind] = bonuses.GetValueOrDefault(kind, 0m) + points;
    }

    private int RoundNo => gameStats?.roundNumber ?? 0;

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

        if (victim != null && victim.IsValid && victim.SteamID != 0) {
            diedThisRound.Add(victim.SteamID);
        }

        // Found 2026-08-04, per direct user ask: world-damage suicide (fall, drowning, ...)
        // reports no attacker at all, so it has to be caught here -- the real-player check
        // just below would otherwise drop it silently, same as it always has.
        // Excludes the bomb, found 2026-08-05: a bomb explosion also reports no attacker but
        // isn't the victim's own fault, so it must not eat the suicide-points penalty either.
        if (attacker == null && IsRealPlayer(victim) &&
            !string.Equals(eventInfo.Weapon, "planted_c4", StringComparison.OrdinalIgnoreCase)) {
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

        string season = CurrentSeason();
        string attackerName = CleanName(attacker.PlayerName);
        string victimName = CleanName(victim.PlayerName);
        string mapName = osbase?.currentMap ?? Server.MapName ?? "";

        // Read what the engine knows about the attacker's pawn NOW -- gone the instant this
        // handler returns. In-air comes on the event itself; in-water doesn't (order section
        // 7), so it's read off the pawn: CBaseEntity.m_fFlags FL_INWATER or
        // m_flWaterLevel > 0. NULL when there's no pawn to ask.
        bool? attackerInWater = null;
        var attackerPawn = attacker.PlayerPawn?.Value;
        if (attackerPawn != null && attackerPawn.IsValid) {
            bool flagInWater = (attackerPawn.Flags & (uint)PlayerFlags.FL_INWATER) != 0;
            float waterLevel = attackerPawn.WaterLevel;
            attackerInWater = flagInWater || waterLevel > 0f;
            if (attackerInWater == true) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] in-water kill: attacker={attackerName} FL_INWATER={flagInWater} m_flWaterLevel={waterLevel.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        var result = ScoreKill(new KillInput {
            Season = season,
            AttackerSteamId64 = attackerSteamId64,
            VictimSteamId64 = victimSteamId64,
            Headshot = eventInfo.Headshot,
            Weapon = NormalizeWeapon(eventInfo.Weapon)
        });

        var assister = eventInfo.Assister;
        if (IsRealPlayer(assister) && assister!.SteamID != attackerSteamId64 && assister.SteamID != victimSteamId64) {
            // Flat BONUS_ASSIST (order 4b). Never rating any more (section 2).
            ulong assisterSteamId64 = assister.SteamID;
            string assisterName = CleanName(assister.PlayerName);
            decimal assistPoints = Math.Round((decimal)formula.Value("BONUS_ASSIST"), 2, MidpointRounding.AwayFromZero);
            AddBonus("assist", assisterSteamId64, assisterName, assistPoints, season, mapName, attackerSteamId64, victimSteamId64);
        }

        pendingKillEvents.Add(new PendingKillEvent {
            MatchId = currentMatchId, // tag only now, nullable -- see class comment
            MapName = mapName,
            AttackerName = attackerName,
            AttackerSteamId64 = attackerSteamId64,
            AttackerRatingBefore = result.AttackerRatingBefore,
            AttackerDelta = result.AttackerDelta,
            AttackerPointsDelta = result.AttackerPointsDelta,
            VictimName = victimName,
            VictimSteamId64 = victimSteamId64,
            VictimRatingBefore = result.VictimRatingBefore,
            VictimDelta = result.VictimDelta,
            Weapon = result.Weapon,
            Headshot = eventInfo.Headshot,
            VictimActiveWeapon = lastEquippedWeapon.GetValueOrDefault(victimSteamId64),
            VictimBestWeapon = bestWeaponThisRound.TryGetValue(victimSteamId64, out var best) ? best.Weapon : null,
            VictimPointsDelta = result.VictimPointsDelta,
            AttackerPlace = result.AttackerPlace,
            VictimPlace = result.VictimPlace,
            BoardSize = result.BoardSize,
            RoundNo = RoundNo,
            AttackerInAir = eventInfo.Attackerinair,
            AttackerInWater = attackerInWater
        });

        AddPoints(attackerSteamId64, attackerName, season, result.AttackerPointsDelta);
        AddPoints(victimSteamId64, victimName, season, result.VictimPointsDelta);
        SetPendingRating(attackerSteamId64, season, attackerName, liveRating[(attackerSteamId64, season)]);
        SetPendingRating(victimSteamId64, season, victimName, liveRating[(victimSteamId64, season)]);

        var pairKey = (attackerSteamId64, victimSteamId64);
        var pair = roundKillPoints.GetValueOrDefault(pairKey);
        roundKillPoints[pairKey] = (pair.AttackerDelta + result.AttackerPointsDelta, pair.VictimDelta + result.VictimPointsDelta);

        return HookResult.Continue;
    }

    // Everything ScoreKill needs, as plain data -- the demo backfill (order section 11) will
    // call this from a replay driver with no game event in sight. Names, map, stamp and pawn
    // state stay with the caller; this is the rating + points arithmetic only.
    public sealed class KillInput {
        public required string Season { get; init; }
        public required ulong AttackerSteamId64 { get; init; }
        public required ulong VictimSteamId64 { get; init; }
        public required bool Headshot { get; init; }
        public required string Weapon { get; init; } // NormalizeWeapon'd: "ak47", "usp_silencer"
    }

    public sealed class KillResult {
        public decimal AttackerRatingBefore { get; init; }
        public decimal VictimRatingBefore { get; init; }
        public decimal AttackerDelta { get; init; }
        public decimal VictimDelta { get; init; }
        public decimal AttackerPointsDelta { get; init; }
        public decimal VictimPointsDelta { get; init; } // <= 0, clipped
        public int AttackerPlace { get; init; }
        public int VictimPlace { get; init; }
        public int BoardSize { get; init; }
        public double WeaponWeight { get; init; }
        public string Weapon { get; init; } = "";
    }

    // Applies the duel to the live rating/matches/kills caches and returns what was applied.
    // Pure in the sense that matters for a replay: no event, no pawn, no DB write -- the
    // caller decides what to log and when to flush.
    private KillResult ScoreKill(KillInput kill) {
        string season = kill.Season;
        var attackerKey = (kill.AttackerSteamId64, season);
        var victimKey = (kill.VictimSteamId64, season);

        SeedRating(kill.AttackerSteamId64, season);
        SeedRating(kill.VictimSteamId64, season);
        SeedKills(kill.AttackerSteamId64, season);
        SeedKills(kill.VictimSteamId64, season);
        SeedPoints(kill.VictimSteamId64, season);

        decimal attackerRating = liveRating[attackerKey];
        decimal victimRating = liveRating[victimKey];
        int attackerMatches = liveMatches[attackerKey];
        int victimMatches = liveMatches[victimKey];

        // Chess-style: each side has its own K, so the two deltas are not forced to be
        // equal and opposite -- a provisional attacker gaining fast off an established
        // victim who barely moves is correct, not a bug (order section 2 leaves the K
        // asymmetry in place and measures it afterwards). Nothing else moves rating: the
        // headshot multiplier is gone -- the victim never paid it, so every headshot minted
        // ~3 rating out of nothing. The expected-score curve stays double (Math.Pow has no
        // decimal overload, and this value is transient -- only the delta added to
        // liveRating needs to be exact).
        double expectedAttacker = 1.0 / (1.0 + Math.Pow(10.0, (double)(victimRating - attackerRating) / 400.0));
        double expectedVictim = 1.0 - expectedAttacker;

        int kAttacker = attackerMatches < provisionalMatches ? kFactorProvisional : kFactor;
        int kVictim = victimMatches < provisionalMatches ? kFactorProvisional : kFactor;

        // Rounded to 4 decimals, not 0 (found 2026-08-04): rounding to an int before
        // accumulating floors small deltas to 0 and discards real skill signal.
        decimal attackerDelta = Math.Round((decimal)(kAttacker * (1.0 - expectedAttacker)), 4, MidpointRounding.AwayFromZero);
        decimal victimDelta = Math.Round((decimal)(kVictim * (0.0 - expectedVictim)), 4, MidpointRounding.AwayFromZero);

        liveRating[attackerKey] = attackerRating + attackerDelta;
        liveRating[victimKey] = victimRating + victimDelta;
        liveMatches[attackerKey] = attackerMatches + 1;
        liveMatches[victimKey] = victimMatches + 1;

        // Points (order sections 3-5, PointsFormula = appendix A verbatim). Placement is the
        // map-start snapshot; surprise is rating's own 1 - expected; weapon weight is the
        // site's weapon_point_weight (exact -> prefix -> suffix, default 1.00); the first
        // WARMUP_KILLS of the season pay flat EVEN regardless. The death costs the victim
        // DEATH_SHARE of the attacker's placement base, clipped to their balance, and the
        // clipped value is what gets written.
        int attackerPlace = PlaceOf(kill.AttackerSteamId64);
        int victimPlace = PlaceOf(kill.VictimSteamId64);
        int boardSize = boardSizeAtMapStart;
        int attackerKillsBefore = liveKills[attackerKey];
        int victimKillsBefore = liveKills[victimKey];
        double surprise = 1.0 - expectedAttacker;
        double weaponWeight = ResolveWeaponWeight(kill.Weapon);

        decimal killPoints = formula.KillPoints(attackerPlace, victimPlace, boardSize, surprise, weaponWeight, kill.Headshot, attackerKillsBefore);
        decimal victimBalance = livePoints.GetValueOrDefault(victimKey, (decimal)formula.StartPoints);
        decimal deathLoss = formula.DeathLoss(attackerPlace, victimPlace, boardSize, attackerKillsBefore, victimKillsBefore, victimBalance);

        liveKills[attackerKey] = attackerKillsBefore + 1;

        return new KillResult {
            AttackerRatingBefore = attackerRating,
            VictimRatingBefore = victimRating,
            AttackerDelta = attackerDelta,
            VictimDelta = victimDelta,
            AttackerPointsDelta = killPoints,
            VictimPointsDelta = -deathLoss,
            AttackerPlace = attackerPlace,
            VictimPlace = victimPlace,
            BoardSize = boardSize,
            WeaponWeight = weaponWeight,
            Weapon = kill.Weapon
        };
    }

    // A 0 delta still goes through: the order (4c) wants the elo_points row created at the
    // player's first event of the season with START_POINTS, and a warmup-period death is a
    // 0-delta event. The flush's INSERT ... ON DUPLICATE KEY handles both cases in one
    // statement (see FlushPendingWrites).
    private void AddPoints(ulong steamId64, string name, string season, decimal points) {
        if (steamId64 == 0) {
            return;
        }

        SeedPoints(steamId64, season);
        var key = (steamId64, season);
        livePoints[key] = livePoints.GetValueOrDefault(key, (decimal)formula.StartPoints) + points;

        if (!pendingPoints.TryGetValue(key, out var pending)) {
            pending = new PendingPoints();
            pendingPoints[key] = pending;
        }

        pending.Name = name;
        pending.Points += points;
    }

    // One bonus row (order 4b/4c): assist, round_win, bomb_plant, bomb_defuse, bomb_pickup,
    // bomb_drop (negative), teamkill_penalty/suicide_penalty when configured. Never rating.
    private void AddBonus(string kind, ulong steamId64, string name, decimal points, string season, string mapName,
                          ulong? relatedAttacker = null, ulong? relatedVictim = null) {
        if (steamId64 == 0 || points == 0) {
            return;
        }

        AddPoints(steamId64, name, season, points);
        RecordRoundBonus(steamId64, kind, points);

        pendingBonusEvents.Add(new PendingBonusEvent {
            Kind = kind,
            SteamId64 = steamId64,
            Name = name,
            RatingDelta = 0,
            PointsDelta = points,
            Season = season,
            MapName = mapName,
            MatchId = currentMatchId,
            RoundNo = RoundNo,
            RelatedAttackerSteamId64 = relatedAttacker,
            RelatedVictimSteamId64 = relatedVictim,
            Stamp = DateTime.UtcNow
        });
    }

    // Found 2026-08-04, per direct user ask, corrected same day after agent-chat #18:
    // teamkill/suicide penalty, applied to POINTS (via the same AddPoints path a round-win
    // uses, just negative) and logged as a replayable elo_bonus_event row -- never rating,
    // see the field comment on teamkillPointsPenalty/suicidePointsPenalty for why.
    private void ApplyPenalty(ulong steamId64, string name, string kind, int pointsPenalty,
                               string season, string mapName, ulong? relatedVictimSteamId64) {
        AddBonus(kind, steamId64, name, pointsPenalty, season, mapName, null, relatedVictimSteamId64);
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

            // No row yet = START_POINTS (order section 1); the row itself is created by the
            // first flush that touches this player this season.
            livePoints[key] = table.Rows.Count > 0 ? Convert.ToDecimal(table.Rows[0]["points"]) : (decimal)formula.StartPoints;
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed seeding points for {steamId64}/{season}: {e.Message}");
            livePoints[key] = (decimal)formula.StartPoints;
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

    private void SetPendingRating(ulong steamId64, string season, string name, decimal rating) {
        var key = (steamId64, season);
        if (!pendingRatings.TryGetValue(key, out var pending)) {
            pending = new PendingRating();
            pendingRatings[key] = pending;
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
                    "victim_active_weapon, victim_best_weapon, victim_points_delta, attacker_place, victim_place, board_size, " +
                    "round_no, attacker_in_air, attacker_in_water) " +
                    "VALUES (@match_id, NOW(), @mapname, @attacker, @attackerid64, @attacker_rb, @attacker_delta, " +
                    "@attacker_points_delta, @victim, @victimid64, @victim_rb, @victim_delta, @weapon, @headshot, " +
                    "@victim_active_weapon, @victim_best_weapon, @victim_points_delta, @attacker_place, @victim_place, @board_size, " +
                    "@round_no, @attacker_in_air, @attacker_in_water)",
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
                        new("@victim_best_weapon", (object?)kill.VictimBestWeapon ?? DBNull.Value),
                        new("@victim_points_delta", kill.VictimPointsDelta),
                        new("@attacker_place", kill.AttackerPlace),
                        new("@victim_place", kill.VictimPlace),
                        new("@board_size", kill.BoardSize),
                        new("@round_no", kill.RoundNo),
                        new("@attacker_in_air", kill.AttackerInAir.HasValue ? (kill.AttackerInAir.Value ? 1 : 0) : DBNull.Value),
                        new("@attacker_in_water", kill.AttackerInWater.HasValue ? (kill.AttackerInWater.Value ? 1 : 0) : DBNull.Value)
                    }));
            }

            foreach (var kv in ratingBatch) {
                var (steamId64, season) = kv.Key;
                var pending = kv.Value;

                writes.Add(($"INTO {RatingTable} (steamid64, season, name, rating, matches, updated_at) " +
                    "VALUES (@steamid64, @season, @name, @rating, @matches, NOW()) " +
                    "ON DUPLICATE KEY UPDATE name=@name, rating=@rating, matches=matches+@matches, updated_at=NOW()",
                    new MySqlParameter[] {
                        new("@steamid64", steamId64.ToString()),
                        new("@season", season),
                        new("@name", pending.Name),
                        new("@rating", pending.Rating),
                        new("@matches", pending.MatchesDelta)
                    }));
            }

            // Insert = START_POINTS + delta, duplicate = += delta. Race-safe between two
            // servers sharing the table: whichever inserts first seeds the start balance, the
            // other lands on the duplicate branch and only adds its delta.
            decimal startPoints = (decimal)formula.StartPoints;
            foreach (var kv in pointsBatch) {
                var (steamId64, season) = kv.Key;
                var pending = kv.Value;

                writes.Add(($"INTO {PointsTable} (steamid64, season, name, points, updated_at) " +
                    "VALUES (@steamid64, @season, @name, @start + @points, NOW()) " +
                    "ON DUPLICATE KEY UPDATE name=@name, points=points+@points, updated_at=NOW()",
                    new MySqlParameter[] {
                        new("@steamid64", steamId64.ToString()),
                        new("@season", season),
                        new("@name", pending.Name),
                        new("@start", startPoints),
                        new("@points", pending.Points)
                    }));
            }

            foreach (var bonus in bonusBatch) {
                writes.Add(($"INTO {BonusEventTable} (kind, match_id, stamp, mapname, season, name, steamid64, " +
                    "rating_delta, points_delta, related_attacker_id64, related_victim_id64, round_no) " +
                    "VALUES (@kind, @match_id, @stamp, @mapname, @season, @name, @steamid64, " +
                    "@rating_delta, @points_delta, @related_attacker, @related_victim, @round_no)",
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
                        new("@related_victim", (object?)bonus.RelatedVictimSteamId64?.ToString() ?? DBNull.Value),
                        new("@round_no", bonus.RoundNo)
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
    private void MergePendingRating((ulong SteamId64, string Season) key, PendingRating rating) {
        if (pendingRatings.TryGetValue(key, out var existing)) {
            existing.MatchesDelta += rating.MatchesDelta;
        } else {
            pendingRatings[key] = rating;
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
        RefreshPointsFormula("MapStart");
        // Order section 3: the board is read once here and held for the whole map. Runs after
        // the flush above is handed off, but that flush is async -- the snapshot query may
        // land before last map's final rows do. Accepted: "at map start" is a snapshot, and a
        // couple of seconds of skew on a placement that's held for 30+ minutes is noise.
        RefreshPlacement("MapStart");
    }

    private HookResult OnRoundEnd(EventRoundEnd eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        decimal roundWin = Math.Round((decimal)formula.Value("BONUS_ROUND_WIN"), 2, MidpointRounding.AwayFromZero);
        if (statsGateOpen && roundWin != 0) {
            string season = CurrentSeason();
            string mapName = osbase?.currentMap ?? Server.MapName ?? "";

            foreach (var p in Utilities.GetPlayers()) {
                if (!IsRealPlayer(p) || p!.TeamNum != eventInfo.Winner) {
                    continue;
                }

                AddBonus("round_win", p.SteamID, CleanName(p.PlayerName), roundWin, season, mapName);
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

    // ----- bomb bonuses (order 4b): plant, defuse, pick up, and a deduction for dropping it -----

    private HookResult OnBombPlanted(EventBombPlanted e) {
        if (isActive && statsGateOpen && IsRealPlayer(e.Userid)) {
            AddBonus("bomb_plant", e.Userid!.SteamID, CleanName(e.Userid.PlayerName),
                Math.Round((decimal)formula.Value("BONUS_PLANT"), 2, MidpointRounding.AwayFromZero),
                CurrentSeason(), osbase?.currentMap ?? Server.MapName ?? "");
        }
        return HookResult.Continue;
    }

    private HookResult OnBombDefused(EventBombDefused e) {
        if (isActive && statsGateOpen && IsRealPlayer(e.Userid)) {
            AddBonus("bomb_defuse", e.Userid!.SteamID, CleanName(e.Userid.PlayerName),
                Math.Round((decimal)formula.Value("BONUS_DEFUSE"), 2, MidpointRounding.AwayFromZero),
                CurrentSeason(), osbase?.currentMap ?? Server.MapName ?? "");
        }
        return HookResult.Continue;
    }

    // "Plocka upp: den som tar på sig uppdraget." Only after someone has let go of it this
    // round -- the engine also fires bomb_pickup when a T spawns with the bomb, and that
    // pays nothing.
    private HookResult OnBombPickup(EventBombPickup e) {
        if (isActive && statsGateOpen && roundBombDrops > 0 && IsRealPlayer(e.Userid)) {
            AddBonus("bomb_pickup", e.Userid!.SteamID, CleanName(e.Userid.PlayerName),
                Math.Round((decimal)formula.Value("BONUS_BOMB_PICKUP"), 2, MidpointRounding.AwayFromZero),
                CurrentSeason(), osbase?.currentMap ?? Server.MapName ?? "");
        }
        return HookResult.Continue;
    }

    // bomb_dropped fires both when the carrier throws it and when they die with it. Only the
    // deliberate drop costs (owner: "att slänga bomben så ger du upp ditt uppdrag") -- told
    // apart by whether the carrier is still alive. Decided one frame later, not inside the
    // event: the engine's death-drop may raise bomb_dropped before the pawn's life state
    // has flipped, and player_death may arrive after it, so "alive right now" inside the
    // callback isn't trustworthy. By the next frame both have settled; diedThisRound covers
    // the case where the controller is already gone. Written as its own negative bonus row
    // so the ledger still sums to elo_points.
    private HookResult OnBombDropped(EventBombDropped e) {
        if (!isActive) {
            return HookResult.Continue;
        }

        roundBombDrops++;

        if (!statsGateOpen || !IsRealPlayer(e.Userid)) {
            return HookResult.Continue;
        }

        ulong steamId64 = e.Userid!.SteamID;
        string name = CleanName(e.Userid.PlayerName);
        string season = CurrentSeason();
        string mapName = osbase?.currentMap ?? Server.MapName ?? "";
        int round = RoundNo;

        Server.NextFrame(() => {
            if (!isActive || !statsGateOpen || RoundNo != round || diedThisRound.Contains(steamId64)) {
                return;
            }

            var carrier = Utilities.GetPlayers().FirstOrDefault(p => IsRealPlayer(p) && p.SteamID == steamId64);
            if (carrier == null || !carrier.PawnIsAlive) {
                return;
            }

            decimal drop = Math.Round((decimal)formula.Value("BONUS_BOMB_DROP"), 2, MidpointRounding.AwayFromZero);
            AddBonus("bomb_drop", steamId64, name, -drop, season, mapName);
        });

        return HookResult.Continue;
    }

    // ----- leaderboard -----

    private void OnEloTopCommand(CCSPlayerController? player, CommandInfo commandInfo) {
        if (!isActive || player == null || !player.IsValid || db == null) {
            return;
        }

        try {
            string season = CurrentSeason();
            DataTable table = db.select(
                $"name, steamid64, rating FROM {RatingTable} WHERE season=@season ORDER BY rating DESC LIMIT @limit",
                new MySqlParameter("@season", season),
                new MySqlParameter("@limit", topLimit)
            );

            player.PrintToChat($" {ChatColors.Green}{chatPrefix}{ChatColors.Default}: Elo leaderboard ({season}):");

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

    // Inclusive day range for the BETWEEN on player_daily_stat.day.
    private static (DateTime Start, DateTime End) SeasonDateRange(string season) {
        var (start, end) = SeasonHelper.Range(season);
        return (start, end.AddDays(-1));
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
