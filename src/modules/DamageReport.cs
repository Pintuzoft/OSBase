using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using MySqlConnector;
using OSBase.Helpers;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace OSBase.Modules;

// Alongside its ephemeral per-round chat report, this module persists the durable,
// career-long counters that back the site's profile stats: body-diagram heatmap, per-weapon
// accuracy, damage/round, nemesis lists, clutches, multikills. See STATS-MODULE.md. Every
// table is dimensioned by side and (where noted) season at write time -- a dimension left out
// of the primary key before writing starts can never be split back out of an already-summed
// counter, so these are decided once, here, not patched in later:
//   player_hit_stat       (steamid64, weapon, hitgroup, direction, side, season) -> hits, damage
//   player_weapon_shots   (steamid64, weapon, side, season) -> shots
//   player_round_stat     (steamid64, side, season, map, end_reason) -> rounds, bomb_plants,
//                         bomb_defuses, defuse_fails, plants_exploded, plants_defused; rounds
//                         is damage/round's denominator (damage / rounds -- NOT industry
//                         "ADR", damage is uncapped, see STATS-MODULE.md)
//   player_duel_stat      (attackerid64, victimid64, attacker_side, victim_side, weapon, season)
//                         -> kills, headshots, noscopes, wallbangs, blind_kills, smoke_kills,
//                         dominations, revenges; nemesis lists ("who kills me / who I kill")
//   player_clutch_stat    (steamid64, side, season, opponents) -> attempts, wins
//   player_multikill_stat (steamid64, side, season, kills) -> rounds (exact-N, no cap)
// player_duel_stat/clutch/multikill run off every round on every server (not gated to
// tournament matches -- that gate belongs to EloRating's own scoring decision, not to whether
// a duel/clutch/round gets counted here). Counters, not raw events; writes buffered in
// memory, flushed between rounds, same pattern as EventWeekend/EloRating.
public class DamageReport : ModuleBase {
    public override string ModuleName => "damagereport";

    private const int ENVIRONMENT = -1;
    private const float DELAY_SECONDS = 3.0f;

    private const string HitStatTable = "player_hit_stat";
    private const string ShotStatTable = "player_weapon_shots";
    private const string RoundStatTable = "player_round_stat";
    private const string DuelStatTable = "player_duel_stat";
    private const string ClutchStatTable = "player_clutch_stat";
    private const string MultikillStatTable = "player_multikill_stat";
    private const string DailyStatTable = "player_daily_stat";
    private const string DuelTotalTable = "player_duel_total";
    private const string ServerStatSeasonTable = "server_stat_season";
    private const string KnifeTaserKillTable = "knife_taser_kill_event";
    private const string MapResultTable = "player_map_result";
    private const int DirectionDealt = 0;
    private const int DirectionReceived = 1;

    // side: CS2's own team numbers, not a private OSBase/OSWeb agreement -- fixed 2026-08-05
    // (osbase-side-encoding-fix.md) after the module shipped with 0=T/1=CT, which collided with
    // CS2's real meaning of those digits (0=unassigned, 1=spectator) and was invisible from the
    // game's own CsTeam enum. player_hit_stat.side and player_duel_stat.attacker_side/victim_side
    // never receive SideUnknown -- see the guards in AddHitCounter/AddDuel -- so those two
    // columns can never confuse "unknown" with a real team the way the old 0/1 scheme did.
    // player_round_stat still legitimately writes SideUnknown for spectators/mid-transition
    // players (ask 11: "unknown is a filterable bucket, not an error state"), and CsTeam.None is
    // the real game value for that, not an invented sentinel.
    private const int SideT = (int)CsTeam.Terrorist;
    private const int SideCT = (int)CsTeam.CounterTerrorist;
    private const int SideUnknown = (int)CsTeam.None;

    // Ask 30: CounterStrikeSharp.API.Modules.Entities.Constants.RoundEndReason.Unknown --
    // confirmed 0 by decompiling the installed API, not assumed. Two distinct rows can
    // legitimately carry this value and are told apart only by first_seen (ask 13) against
    // this migration's deploy date: a pre-migration row (first_seen before deploy) and a
    // round that genuinely never resolved (first_seen at/after deploy, drained here rather
    // than in OnRoundEnd -- see the OnRoundStart safety net).
    private const int RoundEndReasonUnknown = 0;

    // 0..10 (klassiska). Allt annat -> Uxx(xx)
    private readonly string[] hitboxName = {
        "Body", "Head", "Chest", "Stomach", "L-Arm", "R-Arm", "L-Leg", "R-Leg", "Neck", "U9", "Gear"
    };

    private readonly Dictionary<int, HashSet<int>> killedPlayer = new();
    private readonly Dictionary<int, Dictionary<int, int>> damageGiven = new();
    private readonly Dictionary<int, Dictionary<int, int>> damageTaken = new();
    private readonly Dictionary<int, Dictionary<int, int>> hitsGiven = new();
    private readonly Dictionary<int, Dictionary<int, int>> hitsTaken = new();

    private readonly Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitboxGiven = new();
    private readonly Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitboxTaken = new();
    private readonly Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitboxGivenDamage = new();
    private readonly Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitboxTakenDamage = new();

    private readonly Dictionary<int, string> playerNames = new();
    private readonly Dictionary<int, Timer> pendingReports = new();

    private Database? db;

    // Ask 22: read-only cross-module reference for the rating/points snapshot in
    // player_daily_stat -- EloRating owns rating/points, DamageReport owns
    // player_daily_stat. Null (module not loaded/disabled) just means no snapshot that day,
    // not an error.
    private EloRating? eloRating;
    private bool flushInProgress;

    // osbase-stat-contracts.md section 5: round-end used to flush synchronously, right at
    // the worst possible moment. Now the actual DB flush is scheduled a couple seconds out
    // (into the quiet part of the round) instead of firing on the exact tick. Nothing about
    // WHAT gets captured changes -- pending counters are still filled live/at round-end as
    // before; only WHEN the flush's transaction fires is delayed. OnRoundStart still flushes
    // immediately as a safety net so a fast round can never let more than one round's worth
    // of writes queue up behind the delay.
    private const float RoundEndFlushDelaySeconds = 2.0f;
    private Timer? pendingFlushTimer;

    // Ask 11: a filter, not a counter. Decided once at round start and held for the whole
    // round -- re-evaluating at round end would silently exclude normal play whenever people
    // log off late in the evening. Two warm-body pub players farming AWP kills on an empty
    // server would otherwise feed 100% headshot and a 1v1 clutch every round into the exact
    // same lifetime counters as real play; a lifetime counter can't unlearn that later.
    private bool statsGateOpen;
    private int minPlayers = 4;
    private readonly Dictionary<(ulong SteamId64, string Weapon, int Hitgroup, int Direction, int Side, string Season), PendingHitCounter> pendingHitCounters = new();
    private readonly Dictionary<(ulong SteamId64, string Weapon, int Side, string Season), int> pendingShotCounters = new();
    // Ask 30: end_reason joins player_round_stat's key. Kept here, not on
    // roundStagingCounters below -- see that field's comment for why the two are separate.
    private readonly Dictionary<(ulong SteamId64, int Side, string Season, string Map, int EndReason), PendingRoundCounter> pendingRoundCounters = new();

    // Ask 30: which reason a round ends with is only known when EventRoundEnd fires, but
    // bomb_plants/bomb_defuses/plants_exploded/plants_defused all happen at OTHER events,
    // mid-round, before that's known. This stages this round's contributions without
    // end_reason; OnRoundEnd drains it into pendingRoundCounters once e.Reason is in hand,
    // then clears it. AddRoundPlayed/AddDefuseFail could skip the staging step (they already
    // run inside OnRoundEnd, after the reason is known) but go through it anyway so there is
    // one path for every column on this table, not two to keep in sync.
    private readonly Dictionary<(ulong SteamId64, int Side, string Season, string Map), PendingRoundCounter> roundStagingCounters = new();
    private readonly Dictionary<(ulong AttackerId64, ulong VictimId64, int AttackerSide, int VictimSide, string Weapon, string Season), PendingDuelCounter> pendingDuelCounters = new();
    private readonly Dictionary<(ulong SteamId64, int Side, string Season, int Opponents), PendingClutchCounter> pendingClutchCounters = new();
    private readonly Dictionary<(ulong SteamId64, int Side, string Season, int Kills), int> pendingMultikillCounters = new();

    // Ask 15/16: a daily form summary (no weapon/hitgroup/side -- deliberately narrow, see
    // STATS-MODULE.md) and two roll-ups so expensive aggregate questions ("your kills vs
    // deaths this season", "server average headshot rate") are a point lookup instead of a
    // scan. Judgment call: these track dealt/offensive output only (hits/damage/headshots
    // dealt, shots fired, rounds played) -- ask 15 doesn't split direction, and "form" reads
    // naturally as personal output, not what was received. Flag if received stats were wanted
    // too.
    private readonly Dictionary<(ulong SteamId64, DateTime Day), PendingDailyCounter> pendingDailyCounters = new();
    private readonly Dictionary<(ulong SteamId64, string Season), PendingDuelTotalCounter> pendingDuelTotalCounters = new();
    private readonly Dictionary<string, PendingServerStatCounter> pendingServerStatCounters = new();

    // osbase-stat-contracts.md section 4: kept forever, never aggregated in memory -- one
    // row per knife/taser kill, both SteamIDs on the row (see the doc for why the victim
    // column exists but is never meant to be ranked).
    private readonly List<PendingKnifeTaserKill> pendingKnifeTaserKills = new();

    // Ask 29: same shape as pendingKnifeTaserKills -- a raw log, never aggregated in memory.
    private readonly List<PendingMapResult> pendingMapResults = new();

    // Round-scoped state, resolved into the pending counters above at round end, cleared at
    // round start as a safety net.
    private readonly HashSet<int> roundDefuseBegan = new();          // userId -> began a defuse, no matching EventBombDefused yet
    private readonly Dictionary<int, int> roundKillCount = new();    // userId -> kills this round (team kills excluded)

    // Ask 26: who planted the live bomb, so EventBombExploded/EventBombDefused can credit
    // the planter's plants_exploded/plants_defused instead of whoever's standing there at
    // resolution. Only one bomb can be live at a time, so a single slot (not a collection)
    // is enough. Set on a counted EventBombPlanted, cleared on resolution and as a safety
    // net at round start -- an unresolved plant (round ends while it's still ticking)
    // deliberately leaves both counters untouched.
    private ulong? plantedBySteamId64;
    private int plantedBySide = SideUnknown;
    private readonly HashSet<ulong> clutchFlaggedThisRound = new();  // steamid64 already recorded as clutching this round
    private readonly List<(ulong SteamId64, int Side, int Opponents)> roundClutchCandidates = new();

    // Ask 29: map-scoped, not round-scoped -- reset in OnMapStart, read once in OnMapEnd,
    // not touched by the per-round safety net above. roundsThisMap only counts rounds
    // where statsGateOpen was true (same gate as everywhere else), so a map that spent
    // most of its time under-populated reports a low number rather than the engine's raw
    // round count -- ask 11's gates "on top" of the row, per the ask. mapStartSide is
    // captured on the map's first EventRoundStart, not OnMapStart itself: team assignment
    // isn't guaranteed settled the instant a map loads, but it is by the time a round
    // actually begins.
    private int roundsThisMap;
    private bool captureMapStartSideNext;
    private readonly Dictionary<ulong, int> mapStartSide = new();

    // Ask 18 "seconds" (player_daily_stat): sampled at round end against a per-player last
    // credited timestamp, not derived from connect/disconnect deltas -- a crash or ungraceful
    // disconnect just stops accumulating this way instead of losing the whole session. Not
    // round-scoped/cleared at round start: it has to survive across rounds to measure the gap
    // between samples, and a missing entry (first observation, or after a disconnect) means
    // "establish the baseline, don't credit anything yet" rather than crediting time before we
    // were watching.
    private readonly Dictionary<int, DateTime> lastActivitySample = new();

    private sealed class PendingHitCounter {
        public int Hits;
        public int Damage;
    }

    private sealed class PendingRoundCounter {
        public int Rounds;
        public int RoundsWon;
        public int BombPlants;
        public int BombDefuses;
        public int DefuseFails;
        public int PlantsExploded;
        public int PlantsDefused;
    }

    private sealed class PendingDailyCounter {
        public int Hits;
        public int Damage;
        public int Headshots;
        public int Kills;
        public int Shots;
        public int Rounds;
        public int Seconds;
        public int? Rating;  // snapshot, overwritten not summed -- see AddDailyStat
        public int? Points;  // snapshot, overwritten not summed -- see AddDailyStat
    }

    private sealed class PendingDuelTotalCounter {
        public int Kills;
        public int Deaths;
        public int Headshots;
        public int Assists;
        // Found 2026-08-04: separate counters, not a subtraction from Kills above -- the
        // user was explicit that a teamkill/suicide penalty must never touch the existing
        // kill counter (that stays exactly what it's meant, a raw event record). These are
        // new, additive-only fields answering "how many", not "how much did it cost you".
        public int TeamKills;
        public int Suicides;
    }

    private sealed class PendingServerStatCounter {
        public int Hits;
        public int Damage;
        public int Headshots;
        public int Shots;
        public int Rounds;
    }

    private sealed class PendingDuelCounter {
        public int Kills;
        public int Headshots;
        public int Noscopes;
        public int Wallbangs;
        public int BlindKills;
        public int SmokeKills;
        public int Dominations;
        public int Revenges;
    }

    private sealed class PendingClutchCounter {
        public int Attempts;
        public int Wins;
    }

    private sealed class PendingKnifeTaserKill {
        public required ulong KillerSteamId64;
        public required ulong VictimSteamId64;
        public required int KillerSide;
        public required int VictimSide;
        public required string Weapon;
        public required string Mapname;
        public int? MatchId;
        public required DateTime Stamp;
        // Ask 27: wallets, not loot -- see the table comment above CreateTables' DDL for why.
        public required int KillerMoney;
        public required int VictimMoney;
        // Ask 28: null until Mug reports back what it actually moved (or never, for a
        // taser kill -- see ReportKnifeMoneyMoved). Mutable, unlike the fields above: this
        // is the one column on this row that isn't known at the moment the row is built.
        public int? MoneyMoved;
    }

    // Ask 29: a log, not a counter -- one row per player per finished map, `stamp` in the
    // PK so the same player/map pair accumulates rows across sessions instead of merging.
    // kills/deaths/score are read straight off the game's own scoreboard (see AddMapResult's
    // caller in OnMapEnd), not computed from any counter this module already keeps.
    private sealed class PendingMapResult {
        public required ulong SteamId64;
        public required string Map;
        public required string Season;
        public required DateTime Stamp;
        public required int Kills;
        public required int Deaths;
        public required int Score;
        public required int Rounds;
        public required int SideStart;
    }

    protected override void OnLoad() {
        CreateCustomConfigs();
        LoadConfig();

        db = new Database(osbase!, config!);
        CreateTables();
        eloRating = osbase?.GetModule<EloRating>();
    }

    protected override void OnUnload() {
        CancelAllPendingReports();
        pendingFlushTimer?.Kill();
        pendingFlushTimer = null;
        FlushPendingStats("Unload");
        ClearDamageData();
        db?.Shutdown();
        db = null;
        eloRating = null;
    }

    protected override void OnReloadConfig() {
        CreateCustomConfigs();
        LoadConfig();
        eloRating = osbase?.GetModule<EloRating>();
    }

    // ----- config (damagereport.cfg) -----

    private void CreateCustomConfigs() {
        config?.CreateCustomConfig(
            $"{ModuleName}.cfg",
            "// DamageReport Configuration\n" +
            "// Gate for the durable stat tables (player_hit_stat, player_weapon_shots,\n" +
            "// player_round_stat, player_duel_stat, player_clutch_stat,\n" +
            "// player_multikill_stat). Decided once at round start, held for the whole\n" +
            "// round. Warmup is always excluded and is not configurable; min_players is,\n" +
            "// because the right threshold isn't known until there's real data to look at.\n" +
            "min_players 4\n"
        );
    }

    private void LoadConfig() {
        minPlayers = 4;

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
                case "min_players":
                    minPlayers = ParseInt(value, 4, 0, 64);
                    break;
                default:
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Unknown config key {key}:{value}");
                    break;
            }
        }
    }

    private static int ParseInt(string value, int defaultValue, int min, int max) {
        if (!int.TryParse(value, out int parsed)) {
            return defaultValue;
        }

        return Math.Clamp(parsed, min, max);
    }

    private bool IsWarmupActive() {
        return osbase?.GetGameStats()?.IsWarmup ?? true;
    }

    private static int CountConnectedHumans() {
        return Utilities.GetPlayers().Count(p =>
            p != null && p.IsValid && !p.IsHLTV && !p.IsBot && p.Connected == PlayerConnectedState.Connected
        );
    }

    protected override void RegisterHandlers() {
        // Use new EventBus system (Subscribe instead of RegisterEventHandler)
        osbase?.SubscribeToEvent<EventPlayerHurt>(OnPlayerHurt);
        osbase?.SubscribeToEvent<EventPlayerDeath>(OnPlayerDeath);
        osbase?.SubscribeToEvent<EventWeaponFire>(OnWeaponFire);
        osbase?.SubscribeToEvent<EventBombPlanted>(OnBombPlanted);
        osbase?.SubscribeToEvent<EventBombDefused>(OnBombDefused);
        osbase?.SubscribeToEvent<EventBombExploded>(OnBombExploded);
        osbase?.SubscribeToEvent<EventBombBegindefuse>(OnBombBeginDefuse);
        osbase?.SubscribeToEvent<EventRoundStart>(OnRoundStart);
        osbase?.SubscribeToEvent<EventRoundEnd>(OnRoundEnd);
        osbase?.SubscribeToEvent<EventPlayerConnect>(OnPlayerConnect);
        osbase?.SubscribeToEvent<EventPlayerDisconnect>(OnPlayerDisconnectEvent);
        osbase?.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        osbase?.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
    }

    protected override void UnregisterHandlers() {
        // Use new EventBus system (Unsubscribe instead of DeregisterEventHandler)
        osbase?.UnsubscribeFromEvent<EventPlayerHurt>(OnPlayerHurt);
        osbase?.UnsubscribeFromEvent<EventPlayerDeath>(OnPlayerDeath);
        osbase?.UnsubscribeFromEvent<EventWeaponFire>(OnWeaponFire);
        osbase?.UnsubscribeFromEvent<EventBombPlanted>(OnBombPlanted);
        osbase?.UnsubscribeFromEvent<EventBombDefused>(OnBombDefused);
        osbase?.UnsubscribeFromEvent<EventBombExploded>(OnBombExploded);
        osbase?.UnsubscribeFromEvent<EventBombBegindefuse>(OnBombBeginDefuse);
        osbase?.UnsubscribeFromEvent<EventRoundStart>(OnRoundStart);
        osbase?.UnsubscribeFromEvent<EventRoundEnd>(OnRoundEnd);
        osbase?.UnsubscribeFromEvent<EventPlayerConnect>(OnPlayerConnect);
        osbase?.UnsubscribeFromEvent<EventPlayerDisconnect>(OnPlayerDisconnectEvent);
        osbase?.RemoveListener<Listeners.OnMapStart>(OnMapStart);
        osbase?.RemoveListener<Listeners.OnMapEnd>(OnMapEnd);
    }

    private void OnMapStart(string mapName) {
        if (!isActive) {
            return;
        }

        pendingFlushTimer?.Kill();
        pendingFlushTimer = null;
        FlushPendingStats("MapStart");

        // Ask 29: fresh map, fresh count. mapStartSide is deliberately NOT captured here --
        // see captureMapStartSideNext's field comment for why it waits for the map's first
        // round instead.
        roundsThisMap = 0;
        captureMapStartSideNext = true;
    }

    // Ask 29: read at map end, before the disconnect churn -- ServerInfo.cs's OnMapEnd
    // already established that ordering for this exact listener (its grace-window comment).
    // Whether CS2 has already reset ActionTrackingServices/Score by this point is NOT
    // verified from source, same caveat as ask 27's read-order trap; only a live map end
    // with known scores confirms it.
    private void OnMapEnd() {
        if (!isActive) {
            return;
        }

        try {
            string season = CurrentSeason();
            string map = CurrentMap();
            int rounds = roundsThisMap;

            foreach (var p in Utilities.GetPlayers()) {
                if (!IsRealHuman(p)) {
                    continue;
                }

                var tracking = p.ActionTrackingServices;
                if (tracking == null) {
                    continue;
                }

                int sideStart = mapStartSide.TryGetValue(p.SteamID, out int capturedSide) ? capturedSide : SideUnknown;
                AddMapResult(p.SteamID, map, season, tracking.MatchStats.Kills, tracking.MatchStats.Deaths, p.Score, rounds, sideStart);
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception in OnMapEnd: {ex}");
        }

        // Immediate, not delayed like the round-end flush -- there is no next round to wait
        // out, and players can start disconnecting for the map change any moment.
        pendingFlushTimer?.Kill();
        pendingFlushTimer = null;
        FlushPendingStats("MapEnd");
    }

    // All four tables below are owned by this module alone -- two writers on the same
    // counter double-count silently. steamid64/attackerid64/victimid64 are all VARCHAR(32)
    // -- a Steam64 overflows JS's safe-integer range -- never BIGINT.
    private void CreateTables() {
        if (db == null) {
            return;
        }

        // first_seen (ask 13): set on INSERT only, never touched by the ON DUPLICATE KEY
        // UPDATE clause, so it survives every later update. Lets a profile say what period its
        // numbers actually cover instead of leaving a pile of totals with no dates on them.
        string hitStatTable = $"""
        TABLE IF NOT EXISTS {HitStatTable} (
            steamid64  VARCHAR(32) NOT NULL,
            weapon     VARCHAR(32) NOT NULL,
            hitgroup   TINYINT UNSIGNED NOT NULL,
            direction  TINYINT UNSIGNED NOT NULL,
            side       TINYINT UNSIGNED NOT NULL,
            season     VARCHAR(8) NOT NULL,
            hits       INT NOT NULL DEFAULT 0,
            damage     INT NOT NULL DEFAULT 0,
            first_seen DATETIME NOT NULL,
            updated_at DATETIME NOT NULL,
            PRIMARY KEY (steamid64, weapon, hitgroup, direction, side, season)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        // Separate from player_hit_stat: EventPlayerHurt only fires on an actual hit, so
        // "shots fired" (for accuracy = hits / shots) needs its own counter fed by
        // EventWeaponFire. No hitgroup/direction here -- a miss has neither. side/season kept
        // in step with player_hit_stat's so accuracy stays computable per season/side too.
        string shotStatTable = $"""
        TABLE IF NOT EXISTS {ShotStatTable} (
            steamid64  VARCHAR(32) NOT NULL,
            weapon     VARCHAR(32) NOT NULL,
            side       TINYINT UNSIGNED NOT NULL,
            season     VARCHAR(8) NOT NULL,
            shots      INT NOT NULL DEFAULT 0,
            first_seen DATETIME NOT NULL,
            updated_at DATETIME NOT NULL,
            PRIMARY KEY (steamid64, weapon, side, season)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        // For damage/round (NOT industry "ADR" -- damage is uncapped, confirmed live
        // 2026-08-04, see STATS-MODULE.md) = SUM(player_hit_stat.damage WHERE
        // direction=dealt) / player_round_stat.rounds. Rounds, not raw ticks -- one row grows
        // by exactly 1 per player per round played. bomb_plants/bomb_defuses come straight off
        // their events; defuse_fails is a BeginDefuse that never got a matching Defused that
        // round (see roundDefuseBegan). Haskit is available on EventBombBegindefuse but
        // deliberately not split out here -- a no-kit-defuse distinction is a separate ask.
        // rounds_won (ask 14): of `rounds`, how many the player's side actually took -- the
        // round result is already in hand here to resolve clutch attempts, so this is one more
        // column, not new plumbing. Deliberately overlaps cs2rank.lvl_base's lifetime
        // round_win/round_lose: this version is split by side/season and sits behind the ask
        // 11 gates, which lvl_base's lifetime pair can never be split into after the fact.
        // map (ask 17): only here, not on player_hit_stat -- that table is already the
        // expensive one (weapon x hitgroup x direction x side x season), and multiplying it by
        // a map rotation would be the one genuinely costly change in this whole document.
        // Rounds are cheap: one row per (side, season, map) per player.
        // plants_exploded/plants_defused (ask 26): the outcome of the bombs THIS player
        // planted, not bomb_plants minus something -- a round can end with the bomb still
        // ticking (mp_restartgame, map change, match end, everyone leaving), so neither
        // counter derives from the other. Credited to the planter via round state
        // (plantedBySteamId64 below), not EventBombExploded's Userid -- that field is
        // whoever's near the bomb when it goes off, not who planted it, and it goes stale
        // the moment the planter disconnects before the timer runs out.
        // end_reason (ask 30): the game's OWN EventRoundEnd.Reason value, stored as-is --
        // no invented encoding, same rule the side-encoding fix paid for once already.
        // TINYINT UNSIGNED is enough (CS2's RoundEndReason enum tops out at 22).
        // A key dimension, not five extra columns -- same call ask 9 made for clutch
        // `opponents`. Every column on this table now also splits by how the round ended,
        // which is why it joins the SAME primary key as side/season/map, not a plain
        // ADD COLUMN -- see EnsureEndReasonInPrimaryKey for the migration this requires on
        // the already-live table.
        string roundStatTable = $"""
        TABLE IF NOT EXISTS {RoundStatTable} (
            steamid64    VARCHAR(32) NOT NULL,
            side         TINYINT UNSIGNED NOT NULL,
            season       VARCHAR(8) NOT NULL,
            map          VARCHAR(32) NOT NULL,
            end_reason   TINYINT UNSIGNED NOT NULL DEFAULT 0,
            rounds       INT NOT NULL DEFAULT 0,
            rounds_won   INT NOT NULL DEFAULT 0,
            bomb_plants  INT NOT NULL DEFAULT 0,
            bomb_defuses INT NOT NULL DEFAULT 0,
            defuse_fails INT NOT NULL DEFAULT 0,
            plants_exploded INT NOT NULL DEFAULT 0,
            plants_defused  INT NOT NULL DEFAULT 0,
            first_seen   DATETIME NOT NULL,
            updated_at   DATETIME NOT NULL,
            PRIMARY KEY (steamid64, side, season, map, end_reason)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        // Nemesis lists ("who kills me" / "who I kill"), both directions of a pair stored so
        // either side's profile can filter by their own side. A teamkill is simply a row
        // where attacker_side == victim_side, not filtered out. dominations/revenges mirror
        // the game's own domination/revenge mechanic (EventPlayerDeath.Dominated/.Revenge) so
        // OSWeb's own tally can be checked against it.
        string duelStatTable = $"""
        TABLE IF NOT EXISTS {DuelStatTable} (
            attackerid64  VARCHAR(32) NOT NULL,
            victimid64    VARCHAR(32) NOT NULL,
            attacker_side TINYINT UNSIGNED NOT NULL,
            victim_side   TINYINT UNSIGNED NOT NULL,
            weapon        VARCHAR(32) NOT NULL,
            season        VARCHAR(8) NOT NULL,
            kills         INT NOT NULL DEFAULT 0,
            headshots     INT NOT NULL DEFAULT 0,
            noscopes      INT NOT NULL DEFAULT 0,
            wallbangs     INT NOT NULL DEFAULT 0,
            blind_kills   INT NOT NULL DEFAULT 0,
            smoke_kills   INT NOT NULL DEFAULT 0,
            dominations   INT NOT NULL DEFAULT 0,
            revenges      INT NOT NULL DEFAULT 0,
            first_seen    DATETIME NOT NULL,
            updated_at    DATETIME NOT NULL,
            PRIMARY KEY (attackerid64, victimid64, attacker_side, victim_side, weapon, season),
            KEY idx_duel_victim (victimid64),
            KEY idx_duel_attacker (attackerid64)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        // Attempts, not just wins -- a win rate needs both. An attempt is logged the moment a
        // side drops to exactly one alive player (see CheckClutchSituations), resolved against
        // the round's actual winner at round end, not derived from the round result alone
        // (a lost clutch must still produce an attempt row).
        string clutchStatTable = $"""
        TABLE IF NOT EXISTS {ClutchStatTable} (
            steamid64  VARCHAR(32) NOT NULL,
            side       TINYINT UNSIGNED NOT NULL,
            season     VARCHAR(8) NOT NULL,
            opponents  TINYINT UNSIGNED NOT NULL,
            attempts   INT NOT NULL DEFAULT 0,
            wins       INT NOT NULL DEFAULT 0,
            first_seen DATETIME NOT NULL,
            updated_at DATETIME NOT NULL,
            PRIMARY KEY (steamid64, side, season, opponents)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        // Exact N, not "at least N" -- collapsing into a single N+ bucket at write time would
        // make the individual counts unrecoverable later. No cap at 5: pub teams can be larger
        // than 5v5, so 6k/7k rounds happen. Grouping "5k+" for display is a rendering choice,
        // not a storage one.
        string multikillStatTable = $"""
        TABLE IF NOT EXISTS {MultikillStatTable} (
            steamid64  VARCHAR(32) NOT NULL,
            side       TINYINT UNSIGNED NOT NULL,
            season     VARCHAR(8) NOT NULL,
            kills      TINYINT UNSIGNED NOT NULL,
            rounds     INT NOT NULL DEFAULT 0,
            first_seen DATETIME NOT NULL,
            updated_at DATETIME NOT NULL,
            PRIMARY KEY (steamid64, side, season, kills)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        // Ask 15: daily form summary, extended by ask 18 for "yesterday's highlights" (most
        // kills, most online). Deliberately narrow -- no weapon, hitgroup or side, or
        // multiplying by 365 days/year would be the reckless version of this table. A season
        // is too coarse for "how have I played this week"; a day is exact and this stays a
        // couple hundred rows/year even for an active regular. No first_seen here -- the day
        // itself already pins the row to an exact period, unlike season which spans a quarter.
        // headshots means headshot KILLS here (EventPlayerDeath.Headshot), not headshot hits
        // (hitgroup=1 in player_hit_stat) -- ask 18 settled the ambiguity explicitly: a
        // scoreboard and the old widget both mean kills, and hit-level detail already exists
        // unambiguously in player_hit_stat if anyone needs it. kills/headshots exclude team
        // kills, same as player_multikill_stat. seconds has no other source anywhere in this
        // system (cs2rank's lvl_base is lifetime-only, OSWeb's server_connection is pruned to
        // a short window) -- sampled at round end against a per-player last-seen timestamp,
        // so a crash or ungraceful disconnect just stops accumulating instead of losing
        // everything the way a pure connect/disconnect-delta approach would.
        // rating/points (ask 22): a SNAPSHOT, not a counter -- the player's elo_rating.rating
        // and elo_points.points as of this day's last round-end write, overwritten each time,
        // never summed. This is the one non-retroactive gap in the strongest sense in this
        // whole system: every other missing dimension loses a breakdown, this one loses time
        // itself -- a rating never written down on a given day cannot be recovered from
        // whatever it later became, by any query, ever. Nullable: no snapshot if EloRating
        // isn't loaded that day, not a claim that the rating was 0.
        string dailyStatTable = $"""
        TABLE IF NOT EXISTS {DailyStatTable} (
            steamid64  VARCHAR(32) NOT NULL,
            day        DATE NOT NULL,
            hits       INT NOT NULL DEFAULT 0,
            damage     INT NOT NULL DEFAULT 0,
            headshots  INT NOT NULL DEFAULT 0,
            kills      INT NOT NULL DEFAULT 0,
            shots      INT NOT NULL DEFAULT 0,
            rounds     INT NOT NULL DEFAULT 0,
            seconds    INT NOT NULL DEFAULT 0,
            rating     INT NULL,
            points     INT NULL,
            updated_at DATETIME NOT NULL,
            PRIMARY KEY (steamid64, day)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        // Ask 16a: roll-up so "how does this opponent do against you vs everyone else" is a
        // point lookup instead of a UNION/GROUP BY over every duel row (what
        // DuelStatRepository::generalForm() does today). One more upsert per flush here buys
        // that for every profile view.
        // Ask 24: headshots/assists added so this table doubles as the player's period
        // summary (feeds !elorank) instead of staying a duel-only roll-up -- same scope as
        // kills/deaths above (team kills included, not filtered), same reasoning: it's the
        // same numbers already in hand at the OnPlayerDeath call site, no separate pass needed.
        // teamkills/suicides added 2026-08-04, per direct user ask: additive-only counters,
        // deliberately never subtracted from kills/deaths above -- those keep meaning exactly
        // what they already meant. The scoreboard -1 penalty (TeamDamage.cs) is a separate,
        // purely cosmetic thing; these two exist so "how often" is answerable at all.
        string duelTotalTable = $"""
        TABLE IF NOT EXISTS {DuelTotalTable} (
            steamid64  VARCHAR(32) NOT NULL,
            season     VARCHAR(8) NOT NULL,
            kills      INT NOT NULL DEFAULT 0,
            deaths     INT NOT NULL DEFAULT 0,
            headshots  INT NOT NULL DEFAULT 0,
            assists    INT NOT NULL DEFAULT 0,
            teamkills  INT NOT NULL DEFAULT 0,
            suicides   INT NOT NULL DEFAULT 0,
            updated_at DATETIME NOT NULL,
            PRIMARY KEY (steamid64, season)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        // Ask 16b: server-wide version of the same summary shape as player_daily_stat, one row
        // per season, so "is this player above or below the server's normal" (e.g. "your 19%
        // headshot rate" vs a 14% server average) doesn't mean aggregating every player's rows
        // on every page view. Not personal data -- no steamid64, nothing for the GDPR list.
        string serverStatSeasonTable = $"""
        TABLE IF NOT EXISTS {ServerStatSeasonTable} (
            season     VARCHAR(8) NOT NULL,
            hits       INT NOT NULL DEFAULT 0,
            damage     INT NOT NULL DEFAULT 0,
            headshots  INT NOT NULL DEFAULT 0,
            shots      INT NOT NULL DEFAULT 0,
            rounds     INT NOT NULL DEFAULT 0,
            updated_at DATETIME NOT NULL,
            PRIMARY KEY (season)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        // osbase-stat-contracts.md section 4: kept forever (storage isn't the constraint --
        // a couple a day is ~1000 rows/year), two SteamIDs on the row on purpose. Both get
        // ANONYMIZED (2026-08-04 correction -- see ELO-MODULE.md, this was briefly documented
        // as delete-both, which was wrong) on an erasure request, same shape and reason as
        // player_duel_stat/elo_kill_event: deleting the row on the victim's request would also
        // erase the killer's most memorable moment, which they never asked to lose. Team kills
        // included (not filtered), same reasoning as player_duel_stat/elo_kill_event -- this is
        // a raw event record, not an achievement counter, nothing to protect from inflation --
        // but distinguishable via killer_side/victim_side (same SideT/SideCT/SideUnknown scale
        // as player_duel_stat) so the site can choose per-surface: a highlight feed wants both,
        // a "best with a knife" leaderboard almost certainly doesn't. No index beyond the PK:
        // the site never ranks victims (deliberately, per the contract doc) and reads this by
        // killer or by time, both fine as a scan at this table's size.
        // victim_money/killer_money (ask 27): wallets, not "stolen" -- named that way on
        // purpose, see the ask. Confirmed from source, not inferred: Mug.cs IS a real transfer
        // (cross-team knife kill -> full victim balance moves to the killer; same-team knife
        // kill -> the reverse, a punishment transfer attacker-to-victim), not the game's own
        // knife-kill bonus being misremembered as one. Mug.cs also subscribes to
        // EventPlayerDeath, and module discovery orders subscriptions alphabetically by type
        // name (DiscoverModules in OSBase.cs) -- "DamageReport" sorts before "Mug", so this
        // module's handler dispatches first and the read below is guaranteed pre-transfer,
        // deterministically, not by timing luck. That guarantee breaks if a future module
        // renamed to sort before "DamageReport" also moves money on a knife kill.
        // Still open, and NOT resolved by reading source (CS2's own economy is engine-internal):
        // whether the game's own kill-money award, if any, has already posted to killer_money
        // by the time EventPlayerDeath reaches plugins. Only observable on a live server --
        // knife someone with a known balance and read the row.
        // money_moved (ask 28): signed, from the KILLER's side (>0 taken from the victim, <0
        // paid to a knifed team-mate as Mug's penalty, =0 the transfer ran and moved nothing).
        // NULL and 0 mean different things on purpose -- same call as player_daily_stat.rating
        // -- NULL is a taser kill (the mechanic never touched it), 0 is a knife kill that Mug
        // touched and moved nothing (e.g. a broke victim). DamageReport stays the table's only
        // writer; Mug reports the figure through ReportKnifeMoneyMoved rather than writing here
        // itself, same two-writers-on-one-table guardrail as everywhere else in this module.
        string knifeTaserKillTable = $"""
        TABLE IF NOT EXISTS {KnifeTaserKillTable} (
            id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
            killer_steamid64 VARCHAR(32) NOT NULL,
            victim_steamid64 VARCHAR(32) NOT NULL,
            killer_side      TINYINT UNSIGNED NOT NULL,
            victim_side      TINYINT UNSIGNED NOT NULL,
            weapon           VARCHAR(32) NOT NULL,
            mapname          VARCHAR(64) NOT NULL,
            match_id         INT NULL,
            stamp            DATETIME NOT NULL,
            killer_money     INT NOT NULL DEFAULT 0,
            victim_money     INT NOT NULL DEFAULT 0,
            money_moved      INT NULL,
            PRIMARY KEY (id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        // Ask 29: a log (stamp in the PK, never merged), not a counter -- one row per player
        // per finished map. kills/deaths/score are the game's own scoreboard numbers, read at
        // OnMapEnd, same "read rather than computed" rule ask 27 already established for
        // score-shaped columns. Indexed on (map, kills), not (map, score): the settled
        // decision below is that kills is what a "best on Mirage" board actually sorts on --
        // score rides along on the row but isn't the leaderboard key. rounds is on the row so
        // a map cut short (restart/vote/crash) can't quietly set a record next to a real
        // 30-round map; it only counts rounds where ask 11's gate was open, so a map that
        // spent most of its time under-populated reports a low number, not the engine's raw
        // round count. side_start is best-effort (SideUnknown for anyone who joined after the
        // map's first round) and is genuinely optional per the ask.
        string mapResultTable = $"""
        TABLE IF NOT EXISTS {MapResultTable} (
            steamid64  VARCHAR(32) NOT NULL,
            map        VARCHAR(32) NOT NULL,
            season     VARCHAR(8) NOT NULL,
            stamp      DATETIME NOT NULL,
            kills      INT NOT NULL DEFAULT 0,
            deaths     INT NOT NULL DEFAULT 0,
            score      INT NOT NULL DEFAULT 0,
            rounds     INT NOT NULL DEFAULT 0,
            side_start TINYINT UNSIGNED NOT NULL,
            PRIMARY KEY (steamid64, map, stamp),
            KEY idx_map_kills (map, kills),
            KEY idx_steamid_stamp (steamid64, stamp)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        try {
            db.create(hitStatTable);
            db.create(shotStatTable);
            db.create(roundStatTable);
            db.create(duelStatTable);
            db.create(clutchStatTable);
            db.create(multikillStatTable);
            db.create(dailyStatTable);
            db.create(duelTotalTable);
            db.create(serverStatSeasonTable);
            db.create(knifeTaserKillTable);
            db.create(mapResultTable);

            // Migration for pre-existing installs (ask 26, 2026-08-06): CREATE TABLE IF NOT
            // EXISTS above is a no-op against the already-deployed player_round_stat. Default
            // 0, no backfill -- an outcome played before this column existed can never be
            // recovered.
            EnsureColumn(RoundStatTable, "plants_exploded", "INT NOT NULL DEFAULT 0");
            EnsureColumn(RoundStatTable, "plants_defused", "INT NOT NULL DEFAULT 0");

            // Ask 27, same day: knife_taser_kill_event is also already live.
            EnsureColumn(KnifeTaserKillTable, "killer_money", "INT NOT NULL DEFAULT 0");
            EnsureColumn(KnifeTaserKillTable, "victim_money", "INT NOT NULL DEFAULT 0");

            // Ask 28, same day: NULL by default on migration -- correct for every
            // pre-existing row regardless of weapon, since the figure genuinely wasn't
            // captured for any of them (not "definitely zero").
            EnsureColumn(KnifeTaserKillTable, "money_moved", "INT NULL");

            // Ask 30, 2026-08-06: end_reason joins the primary key, which EnsureColumn
            // can't do -- see EnsureEndReasonInPrimaryKey for why running it here is safe
            // against the table's active writer.
            EnsureEndReasonInPrimaryKey();

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] tables ensured.");
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed creating tables: {e.Message}");
        }
    }

    // Same pattern as ServerInfo.cs/TeamBets.cs -- adds a column to an existing table if
    // it's missing, so CREATE TABLE IF NOT EXISTS's no-op against already-deployed tables
    // doesn't leave the schema behind.
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

    // Ask 30: unlike EnsureColumn, this isn't a plain ADD COLUMN -- end_reason joins the
    // PRIMARY KEY, which needs its own ALTER (DROP + re-ADD, InnoDB rebuilds the clustered
    // index either way). Run as one statement so the column and the key change land
    // atomically -- no window where the column exists but the old 4-part key is still live.
    //
    // Safe against concurrent writers by construction, not by timing: this only ever runs
    // from CreateTables(), which OnLoad calls before RegisterHandlers wires up any event
    // subscription (ModuleBase.Load: OnLoad() then LoadHandlers()). On a cold start nothing
    // has subscribed yet. On a hot reload, Unload() already unregistered the old handlers
    // and flushed pending writes before the new Load() reaches this point. Either way there
    // is no in-flight write against player_round_stat while this runs -- same deploy
    // ordering the side-encoding fix needed, but guaranteed by the module lifecycle instead
    // of a manual step.
    //
    // DEFAULT 0 for pre-existing rows is RoundEndReasonUnknown (see that constant's comment),
    // CS2's own "we don't know" value -- not an invented sentinel, same precedent as
    // SideUnknown/CsTeam.None elsewhere on this exact table. No backfill: every round played
    // before this migration genuinely never had its reason recorded. Rows genuinely
    // unresolved live (see OnRoundStart's safety net) also land on end_reason=0 -- the two
    // are told apart by first_seen against this migration's deploy date, not by anything in
    // end_reason itself.
    private void EnsureEndReasonInPrimaryKey() {
        if (db == null) {
            return;
        }

        try {
            DataTable existing = db.select(
                "column_name FROM information_schema.columns " +
                "WHERE table_schema = DATABASE() AND table_name = @table AND column_name = @column",
                new MySqlParameter("@table", RoundStatTable),
                new MySqlParameter("@column", "end_reason")
            );

            if (existing.Rows.Count == 0) {
                db.alter(
                    $"TABLE {RoundStatTable} " +
                    "ADD COLUMN end_reason TINYINT UNSIGNED NOT NULL DEFAULT 0, " +
                    "DROP PRIMARY KEY, " +
                    "ADD PRIMARY KEY (steamid64, side, season, map, end_reason)"
                );
                Console.WriteLine($"[INFO] OSBase[{ModuleName}] - Migrated {RoundStatTable}'s primary key to include end_reason.");
            }
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error migrating {RoundStatTable}'s primary key: {e.Message}");
        }
    }

    private void AddHitCounter(ulong steamId64, string weapon, int hitgroup, int direction, int side, string season, int damage) {
        // player_hit_stat.side is a resolved team by contract (osbase-side-encoding-fix.md) --
        // skip rather than write SideUnknown, since that value is no longer a safe sentinel in
        // this column (it's CS2's own "None", not a made-up "unknown" that only this table knew
        // about). Practically dead code: a hit always has a real attacker/victim on T or CT.
        if (steamId64 == 0 || side == SideUnknown) {
            return;
        }

        var key = (steamId64, weapon, hitgroup, direction, side, season);
        if (!pendingHitCounters.TryGetValue(key, out var counter)) {
            counter = new PendingHitCounter();
            pendingHitCounters[key] = counter;
        }

        counter.Hits += 1;
        counter.Damage += damage;
    }

    private void AddShot(ulong steamId64, string weapon, int side, string season) {
        // Same side contract as player_hit_stat/player_duel_stat -- accuracy (hits/shots) is
        // computed per side, so this column needs the same "never ambiguous" guarantee.
        if (steamId64 == 0 || side == SideUnknown) {
            return;
        }

        var key = (steamId64, weapon, side, season);
        pendingShotCounters[key] = pendingShotCounters.GetValueOrDefault(key, 0) + 1;
    }

    private PendingRoundCounter GetOrCreateRoundCounter(ulong steamId64, int side, string season, string map) {
        var key = (steamId64, side, season, map);
        if (!roundStagingCounters.TryGetValue(key, out var counter)) {
            counter = new PendingRoundCounter();
            roundStagingCounters[key] = counter;
        }

        return counter;
    }

    // Ask 30: called once per round, from OnRoundEnd, once e.Reason is known. Folds this
    // round's staged contributions into the real, end_reason-keyed pending dictionary and
    // empties the staging area so it starts clean next round.
    private void DrainRoundStagingCounters(int endReason) {
        foreach (var kv in roundStagingCounters) {
            var (steamId64, side, season, map) = kv.Key;
            var staged = kv.Value;

            var key = (steamId64, side, season, map, endReason);
            if (!pendingRoundCounters.TryGetValue(key, out var counter)) {
                counter = new PendingRoundCounter();
                pendingRoundCounters[key] = counter;
            }

            counter.Rounds += staged.Rounds;
            counter.RoundsWon += staged.RoundsWon;
            counter.BombPlants += staged.BombPlants;
            counter.BombDefuses += staged.BombDefuses;
            counter.DefuseFails += staged.DefuseFails;
            counter.PlantsExploded += staged.PlantsExploded;
            counter.PlantsDefused += staged.PlantsDefused;
        }

        roundStagingCounters.Clear();
    }

    private void AddRoundPlayed(ulong steamId64, int side, string season, string map, bool won) {
        if (steamId64 == 0) {
            return;
        }

        var counter = GetOrCreateRoundCounter(steamId64, side, season, map);
        counter.Rounds += 1;
        if (won) {
            counter.RoundsWon += 1;
        }
    }

    private void AddBombPlant(ulong steamId64, int side, string season, string map) {
        if (steamId64 == 0) {
            return;
        }

        GetOrCreateRoundCounter(steamId64, side, season, map).BombPlants += 1;
    }

    private void AddBombDefuse(ulong steamId64, int side, string season, string map) {
        if (steamId64 == 0) {
            return;
        }

        GetOrCreateRoundCounter(steamId64, side, season, map).BombDefuses += 1;
    }

    private void AddDefuseFail(ulong steamId64, int side, string season, string map) {
        if (steamId64 == 0) {
            return;
        }

        GetOrCreateRoundCounter(steamId64, side, season, map).DefuseFails += 1;
    }

    private void AddPlantExploded(ulong steamId64, int side, string season, string map) {
        if (steamId64 == 0) {
            return;
        }

        GetOrCreateRoundCounter(steamId64, side, season, map).PlantsExploded += 1;
    }

    private void AddPlantDefused(ulong steamId64, int side, string season, string map) {
        if (steamId64 == 0) {
            return;
        }

        GetOrCreateRoundCounter(steamId64, side, season, map).PlantsDefused += 1;
    }

    private void AddDailyStat(ulong steamId64, int hits, int damage, int headshots, int kills, int shots, int rounds, int seconds, int? rating = null, int? points = null) {
        if (steamId64 == 0) {
            return;
        }

        var key = (steamId64, DateTime.UtcNow.Date);
        if (!pendingDailyCounters.TryGetValue(key, out var counter)) {
            counter = new PendingDailyCounter();
            pendingDailyCounters[key] = counter;
        }

        counter.Hits += hits;
        counter.Damage += damage;
        counter.Headshots += headshots;
        counter.Kills += kills;
        counter.Shots += shots;
        counter.Rounds += rounds;
        counter.Seconds += seconds;

        // Snapshot, not a counter -- a later call always wins, it never adds to a former one.
        if (rating.HasValue) {
            counter.Rating = rating;
        }
        if (points.HasValue) {
            counter.Points = points;
        }
    }

    private void AddDuelTotal(ulong steamId64, string season, int kills, int deaths, int headshots = 0, int assists = 0, int teamKills = 0, int suicides = 0) {
        if (steamId64 == 0) {
            return;
        }

        var key = (steamId64, season);
        if (!pendingDuelTotalCounters.TryGetValue(key, out var counter)) {
            counter = new PendingDuelTotalCounter();
            pendingDuelTotalCounters[key] = counter;
        }

        counter.Kills += kills;
        counter.Deaths += deaths;
        counter.Headshots += headshots;
        counter.Assists += assists;
        counter.TeamKills += teamKills;
        counter.Suicides += suicides;
    }

    private void AddServerStat(string season, int hits, int damage, int headshots, int shots, int rounds) {
        if (!pendingServerStatCounters.TryGetValue(season, out var counter)) {
            counter = new PendingServerStatCounter();
            pendingServerStatCounters[season] = counter;
        }

        counter.Hits += hits;
        counter.Damage += damage;
        counter.Headshots += headshots;
        counter.Shots += shots;
        counter.Rounds += rounds;
    }

    private string CurrentMap() {
        return osbase?.currentMap ?? Server.MapName ?? "";
    }

    private void AddDuel(
        ulong attackerId64, ulong victimId64, int attackerSide, int victimSide, string weapon, string season,
        bool headshot, bool noscope, bool wallbang, bool blind, bool smoke, int dominated, int revenge
    ) {
        // Same contract as AddHitCounter: attacker_side/victim_side must never carry
        // SideUnknown, or a genuinely-unknown side and a real Terrorist become the same number
        // again -- exactly the collision osbase-side-encoding-fix.md's ask 3 was written to
        // close (OSWeb's teamkill check reads attacker_side = victim_side, and two unresolved
        // sides would wrongly match each other). Practically dead code: both parties are
        // verified real, active players before this is called.
        if (attackerId64 == 0 || victimId64 == 0 || attackerId64 == victimId64 ||
            attackerSide == SideUnknown || victimSide == SideUnknown) {
            return;
        }

        var key = (attackerId64, victimId64, attackerSide, victimSide, weapon, season);
        if (!pendingDuelCounters.TryGetValue(key, out var counter)) {
            counter = new PendingDuelCounter();
            pendingDuelCounters[key] = counter;
        }

        counter.Kills += 1;
        if (headshot) {
            counter.Headshots += 1;
        }
        if (noscope) {
            counter.Noscopes += 1;
        }
        if (wallbang) {
            counter.Wallbangs += 1;
        }
        if (blind) {
            counter.BlindKills += 1;
        }
        if (smoke) {
            counter.SmokeKills += 1;
        }
        if (dominated > 0) {
            counter.Dominations += 1;
        }
        if (revenge > 0) {
            counter.Revenges += 1;
        }
    }

    private void AddClutch(ulong steamId64, int side, string season, int opponents, bool won) {
        if (steamId64 == 0) {
            return;
        }

        var key = (steamId64, side, season, opponents);
        if (!pendingClutchCounters.TryGetValue(key, out var counter)) {
            counter = new PendingClutchCounter();
            pendingClutchCounters[key] = counter;
        }

        counter.Attempts += 1;
        if (won) {
            counter.Wins += 1;
        }
    }

    private void AddMultikillRound(ulong steamId64, int side, string season, int kills) {
        if (steamId64 == 0 || kills <= 0) {
            return;
        }

        var key = (steamId64, side, season, kills);
        pendingMultikillCounters[key] = pendingMultikillCounters.GetValueOrDefault(key, 0) + 1;
    }

    private void AddKnifeTaserKill(ulong killerSteamId64, ulong victimSteamId64, int killerSide, int victimSide, string weapon, string mapname, int? matchId, int killerMoney, int victimMoney) {
        // Same side contract as AddDuel (which this always runs alongside) -- killer_side/
        // victim_side are documented as the same scale as player_duel_stat's, so they get the
        // same guarantee: never SideUnknown.
        if (killerSteamId64 == 0 || victimSteamId64 == 0 || killerSide == SideUnknown || victimSide == SideUnknown) {
            return;
        }

        pendingKnifeTaserKills.Add(new PendingKnifeTaserKill {
            KillerSteamId64 = killerSteamId64,
            VictimSteamId64 = victimSteamId64,
            KillerSide = killerSide,
            VictimSide = victimSide,
            Weapon = weapon,
            Mapname = mapname,
            MatchId = matchId,
            Stamp = DateTime.UtcNow,
            KillerMoney = killerMoney,
            VictimMoney = victimMoney,
        });
    }

    // Ask 28: Mug's report of what it actually moved, called from Mug's own EventPlayerDeath
    // handler which runs second (see the money_moved comment on knifeTaserKillTable's DDL for
    // why that order is guaranteed). DamageReport stays this table's only writer -- Mug never
    // touches pendingKnifeTaserKills directly, it hands the figure over and this module patches
    // its own row. Searched from the end and matched on an unset MoneyMoved: within one
    // EventPlayerDeath dispatch only one row can be waiting for this exact (killer, victim)
    // pair, but the same pair can knife each other more than once in a round before the next
    // flush, and an earlier kill's row must not be overwritten.
    public void ReportKnifeMoneyMoved(ulong killerSteamId64, ulong victimSteamId64, int moneyMoved) {
        if (!isActive) {
            return;
        }

        for (int i = pendingKnifeTaserKills.Count - 1; i >= 0; i--) {
            var row = pendingKnifeTaserKills[i];
            if (row.MoneyMoved == null && row.KillerSteamId64 == killerSteamId64 && row.VictimSteamId64 == victimSteamId64) {
                row.MoneyMoved = moneyMoved;
                return;
            }
        }
    }

    private void AddMapResult(ulong steamId64, string map, string season, int kills, int deaths, int score, int rounds, int sideStart) {
        if (steamId64 == 0) {
            return;
        }

        pendingMapResults.Add(new PendingMapResult {
            SteamId64 = steamId64,
            Map = map,
            Season = season,
            Stamp = DateTime.UtcNow,
            Kills = kills,
            Deaths = deaths,
            Score = score,
            Rounds = rounds,
            SideStart = sideStart,
        });
    }

    private static int MapSide(CCSPlayerController? player) {
        if (player == null) {
            return SideUnknown;
        }

        if (player.TeamNum == (int)CsTeam.Terrorist) {
            return SideT;
        }

        if (player.TeamNum == (int)CsTeam.CounterTerrorist) {
            return SideCT;
        }

        return SideUnknown;
    }

    private static int MapWinnerSide(int winnerTeamNum) {
        if (winnerTeamNum == (int)CsTeam.Terrorist) {
            return SideT;
        }

        if (winnerTeamNum == (int)CsTeam.CounterTerrorist) {
            return SideCT;
        }

        return SideUnknown;
    }

    // Alive for clutch/opponent counting. Bots are excluded entirely, same as everywhere else
    // in this module (a bot is not a person) -- a 1v4 where three are bots against one human
    // IS a 1v1, not a 1v4, because the bots were never counted in the first place. Same
    // PlayerPawn/LifeState check Idle.cs's IsAliveHuman uses.
    private static bool IsAlive(CCSPlayerController? p) {
        if (!IsRealHuman(p)) {
            return false;
        }

        if (p!.TeamNum != (int)CsTeam.Terrorist && p.TeamNum != (int)CsTeam.CounterTerrorist) {
            return false;
        }

        var ph = p.PlayerPawn;
        if (ph == null || !ph.IsValid) {
            return false;
        }

        var pawn = ph.Value;
        return pawn != null && pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE;
    }

    // Called after every death: if either side is now down to exactly one alive player, that
    // player is in a clutch against however many opponents are alive right now. Flagged once
    // per round per player -- later deaths on the same round don't create a second attempt for
    // an already-clutching player, and opponents is fixed at whatever it was the moment the
    // situation started, not re-evaluated as the enemy team is whittled down further.
    private void CheckClutchSituations() {
        CCSPlayerController? soleT = null;
        CCSPlayerController? soleCT = null;
        int aliveT = 0;
        int aliveCT = 0;

        foreach (var p in Utilities.GetPlayers()) {
            if (!IsAlive(p)) {
                continue;
            }

            if (p!.TeamNum == (int)CsTeam.Terrorist) {
                aliveT++;
                soleT = p;
            } else {
                aliveCT++;
                soleCT = p;
            }
        }

        if (aliveT == 1 && aliveCT >= 1) {
            TryFlagClutch(soleT, SideT, aliveCT);
        }

        if (aliveCT == 1 && aliveT >= 1) {
            TryFlagClutch(soleCT, SideCT, aliveT);
        }
    }

    private void TryFlagClutch(CCSPlayerController? player, int side, int opponents) {
        if (!IsRealHuman(player)) {
            return;
        }

        ulong steamId64 = player!.SteamID;
        if (!clutchFlaggedThisRound.Add(steamId64)) {
            return;
        }

        roundClutchCandidates.Add((steamId64, side, opponents));
    }

    // Computed at write time, per STATS-MODULE.md's rule: a dimension left out of the
    // primary key before writing starts can never be recovered from an already-summed
    // counter. Format e.g. "2026Q3". Hit data itself is never reset -- season is only ever a
    // filter, all-time is a SUM across seasons; a quarterly ELO reset (if/when built) is a
    // separate table's concern, not this one's.
    // Extracted to SeasonHelper 2026-08-04 (agent-chat #33) -- this used to be its own private
    // copy, byte-for-byte identical to EloRating.cs's and TeamBets.cs's. See that helper's
    // comment for why three copies that happen to agree isn't the same guarantee as one.
    private static string CurrentSeason() => SeasonHelper.CurrentSeason();

    // Buffered like EventWeekend/EloRating: accumulate during the round, send between
    // rounds. Unwritten rows on a DB outage are merged back (counts, not overwritten) and
    // retried on the next flush.
    private void FlushPendingStats(string source) {
        var database = db;
        if (database == null || flushInProgress) {
            return;
        }

        if (pendingHitCounters.Count == 0 && pendingShotCounters.Count == 0
            && pendingRoundCounters.Count == 0 && pendingDuelCounters.Count == 0
            && pendingClutchCounters.Count == 0 && pendingMultikillCounters.Count == 0
            && pendingDailyCounters.Count == 0 && pendingDuelTotalCounters.Count == 0
            && pendingServerStatCounters.Count == 0 && pendingKnifeTaserKills.Count == 0
            && pendingMapResults.Count == 0) {
            return;
        }

        var hitBatch = pendingHitCounters.ToList();
        pendingHitCounters.Clear();

        var shotBatch = pendingShotCounters.ToList();
        pendingShotCounters.Clear();

        var roundBatch = pendingRoundCounters.ToList();
        pendingRoundCounters.Clear();

        var duelBatch = pendingDuelCounters.ToList();
        pendingDuelCounters.Clear();

        var clutchBatch = pendingClutchCounters.ToList();
        pendingClutchCounters.Clear();

        var multikillBatch = pendingMultikillCounters.ToList();
        pendingMultikillCounters.Clear();

        var dailyBatch = pendingDailyCounters.ToList();
        pendingDailyCounters.Clear();

        var duelTotalBatch = pendingDuelTotalCounters.ToList();
        pendingDuelTotalCounters.Clear();

        var serverStatBatch = pendingServerStatCounters.ToList();
        pendingServerStatCounters.Clear();

        var knifeTaserBatch = pendingKnifeTaserKills.ToList();
        pendingKnifeTaserKills.Clear();

        var mapResultBatch = pendingMapResults.ToList();
        pendingMapResults.Clear();

        flushInProgress = true;

        Task.Run(() => {
            // Perf fix (osbase-stat-contracts.md section 5): this used to be one
            // database.insert() -- one connection, one round trip -- per row, run instantly
            // at round end (the single worst moment: death cam + round-end logic + scoreboard
            // all competing for the same tick). Collected into ONE transaction instead, and
            // the caller (OnRoundEnd) now delays invoking this by RoundEndFlushDelaySeconds so
            // it lands in the quiet part of the round. All rows still commit atomically or
            // not at all -- on failure the entire batch (every type) goes back to pending for
            // retry next flush, which is simpler than the old per-type dbDown/partial-cutoff
            // scheme and no less correct, since a transaction can't half-succeed anyway.
            var writes = new List<(string query, MySqlParameter[] parameters)>();

            foreach (var row in knifeTaserBatch) {
                writes.Add(($"INTO {KnifeTaserKillTable} (killer_steamid64, victim_steamid64, killer_side, victim_side, weapon, mapname, match_id, stamp, killer_money, victim_money, money_moved) " +
                    "VALUES (@killer, @victim, @killer_side, @victim_side, @weapon, @mapname, @match_id, @stamp, @killer_money, @victim_money, @money_moved)",
                    new MySqlParameter[] {
                        new("@killer", row.KillerSteamId64.ToString()),
                        new("@victim", row.VictimSteamId64.ToString()),
                        new("@killer_side", row.KillerSide),
                        new("@victim_side", row.VictimSide),
                        new("@weapon", row.Weapon),
                        new("@mapname", row.Mapname),
                        new("@match_id", (object?)row.MatchId ?? DBNull.Value),
                        new("@stamp", row.Stamp),
                        new("@killer_money", row.KillerMoney),
                        new("@victim_money", row.VictimMoney),
                        new("@money_moved", (object?)row.MoneyMoved ?? DBNull.Value)
                    }));
            }

            foreach (var row in mapResultBatch) {
                writes.Add(($"INTO {MapResultTable} (steamid64, map, season, stamp, kills, deaths, score, rounds, side_start) " +
                    "VALUES (@steamid64, @map, @season, @stamp, @kills, @deaths, @score, @rounds, @side_start)",
                    new MySqlParameter[] {
                        new("@steamid64", row.SteamId64.ToString()),
                        new("@map", row.Map),
                        new("@season", row.Season),
                        new("@stamp", row.Stamp),
                        new("@kills", row.Kills),
                        new("@deaths", row.Deaths),
                        new("@score", row.Score),
                        new("@rounds", row.Rounds),
                        new("@side_start", row.SideStart)
                    }));
            }

            foreach (var kv in hitBatch) {
                var (steamId64, weapon, hitgroup, direction, side, season) = kv.Key;
                var counter = kv.Value;

                writes.Add(($"INTO {HitStatTable} (steamid64, weapon, hitgroup, direction, side, season, hits, damage, first_seen, updated_at) " +
                    "VALUES (@steamid64, @weapon, @hitgroup, @direction, @side, @season, @hits, @damage, NOW(), NOW()) " +
                    "ON DUPLICATE KEY UPDATE hits=hits+@hits, damage=damage+@damage, updated_at=NOW()",
                    new MySqlParameter[] {
                        new("@steamid64", steamId64.ToString()),
                        new("@weapon", weapon),
                        new("@hitgroup", hitgroup),
                        new("@direction", direction),
                        new("@side", side),
                        new("@season", season),
                        new("@hits", counter.Hits),
                        new("@damage", counter.Damage)
                    }));
            }

            foreach (var kv in shotBatch) {
                var (steamId64, weapon, side, season) = kv.Key;

                writes.Add(($"INTO {ShotStatTable} (steamid64, weapon, side, season, shots, first_seen, updated_at) " +
                    "VALUES (@steamid64, @weapon, @side, @season, @shots, NOW(), NOW()) " +
                    "ON DUPLICATE KEY UPDATE shots=shots+@shots, updated_at=NOW()",
                    new MySqlParameter[] {
                        new("@steamid64", steamId64.ToString()),
                        new("@weapon", weapon),
                        new("@side", side),
                        new("@season", season),
                        new("@shots", kv.Value)
                    }));
            }

            foreach (var kv in roundBatch) {
                var (steamId64, side, season, map, endReason) = kv.Key;
                var counter = kv.Value;

                writes.Add(($"INTO {RoundStatTable} (steamid64, side, season, map, end_reason, rounds, rounds_won, bomb_plants, bomb_defuses, defuse_fails, plants_exploded, plants_defused, first_seen, updated_at) " +
                    "VALUES (@steamid64, @side, @season, @map, @end_reason, @rounds, @rounds_won, @plants, @defuses, @fails, @plants_exploded, @plants_defused, NOW(), NOW()) " +
                    "ON DUPLICATE KEY UPDATE rounds=rounds+@rounds, rounds_won=rounds_won+@rounds_won, " +
                    "bomb_plants=bomb_plants+@plants, bomb_defuses=bomb_defuses+@defuses, defuse_fails=defuse_fails+@fails, " +
                    "plants_exploded=plants_exploded+@plants_exploded, plants_defused=plants_defused+@plants_defused, updated_at=NOW()",
                    new MySqlParameter[] {
                        new("@steamid64", steamId64.ToString()),
                        new("@side", side),
                        new("@season", season),
                        new("@map", map),
                        new("@end_reason", endReason),
                        new("@rounds", counter.Rounds),
                        new("@rounds_won", counter.RoundsWon),
                        new("@plants", counter.BombPlants),
                        new("@defuses", counter.BombDefuses),
                        new("@fails", counter.DefuseFails),
                        new("@plants_exploded", counter.PlantsExploded),
                        new("@plants_defused", counter.PlantsDefused)
                    }));
            }

            foreach (var kv in duelBatch) {
                var (attackerId64, victimId64, attackerSide, victimSide, weapon, season) = kv.Key;
                var counter = kv.Value;

                writes.Add(($"INTO {DuelStatTable} (attackerid64, victimid64, attacker_side, victim_side, weapon, season, " +
                    "kills, headshots, noscopes, wallbangs, blind_kills, smoke_kills, dominations, revenges, first_seen, updated_at) " +
                    "VALUES (@attackerid64, @victimid64, @attacker_side, @victim_side, @weapon, @season, " +
                    "@kills, @headshots, @noscopes, @wallbangs, @blind_kills, @smoke_kills, @dominations, @revenges, NOW(), NOW()) " +
                    "ON DUPLICATE KEY UPDATE kills=kills+@kills, headshots=headshots+@headshots, " +
                    "noscopes=noscopes+@noscopes, wallbangs=wallbangs+@wallbangs, blind_kills=blind_kills+@blind_kills, " +
                    "smoke_kills=smoke_kills+@smoke_kills, dominations=dominations+@dominations, revenges=revenges+@revenges, updated_at=NOW()",
                    new MySqlParameter[] {
                        new("@attackerid64", attackerId64.ToString()),
                        new("@victimid64", victimId64.ToString()),
                        new("@attacker_side", attackerSide),
                        new("@victim_side", victimSide),
                        new("@weapon", weapon),
                        new("@season", season),
                        new("@kills", counter.Kills),
                        new("@headshots", counter.Headshots),
                        new("@noscopes", counter.Noscopes),
                        new("@wallbangs", counter.Wallbangs),
                        new("@blind_kills", counter.BlindKills),
                        new("@smoke_kills", counter.SmokeKills),
                        new("@dominations", counter.Dominations),
                        new("@revenges", counter.Revenges)
                    }));
            }

            foreach (var kv in clutchBatch) {
                var (steamId64, side, season, opponents) = kv.Key;
                var counter = kv.Value;

                writes.Add(($"INTO {ClutchStatTable} (steamid64, side, season, opponents, attempts, wins, first_seen, updated_at) " +
                    "VALUES (@steamid64, @side, @season, @opponents, @attempts, @wins, NOW(), NOW()) " +
                    "ON DUPLICATE KEY UPDATE attempts=attempts+@attempts, wins=wins+@wins, updated_at=NOW()",
                    new MySqlParameter[] {
                        new("@steamid64", steamId64.ToString()),
                        new("@side", side),
                        new("@season", season),
                        new("@opponents", opponents),
                        new("@attempts", counter.Attempts),
                        new("@wins", counter.Wins)
                    }));
            }

            foreach (var kv in multikillBatch) {
                var (steamId64, side, season, kills) = kv.Key;

                writes.Add(($"INTO {MultikillStatTable} (steamid64, side, season, kills, rounds, first_seen, updated_at) " +
                    "VALUES (@steamid64, @side, @season, @kills, @rounds, NOW(), NOW()) " +
                    "ON DUPLICATE KEY UPDATE rounds=rounds+@rounds, updated_at=NOW()",
                    new MySqlParameter[] {
                        new("@steamid64", steamId64.ToString()),
                        new("@side", side),
                        new("@season", season),
                        new("@kills", kills),
                        new("@rounds", kv.Value)
                    }));
            }

            foreach (var kv in dailyBatch) {
                var (steamId64, day) = kv.Key;
                var counter = kv.Value;

                // rating/points are a snapshot, not a counter -- only present in the column
                // list at all when this batch actually carries one (not every flush touches
                // every key via the round-end loop that reads them, e.g. a player who
                // disconnected between their last hit and round end). Absent means "leave
                // whatever was last snapshotted alone", never "overwrite with unknown".
                var insertCols = new List<string> { "steamid64", "day", "hits", "damage", "headshots", "kills", "shots", "rounds", "seconds" };
                var insertVals = new List<string> { "@steamid64", "@day", "@hits", "@damage", "@headshots", "@kills", "@shots", "@rounds", "@seconds" };
                var updateClauses = new List<string> {
                    "hits=hits+@hits", "damage=damage+@damage", "headshots=headshots+@headshots",
                    "kills=kills+@kills", "shots=shots+@shots", "rounds=rounds+@rounds", "seconds=seconds+@seconds"
                };
                var dailyParams = new List<MySqlParameter> {
                    new("@steamid64", steamId64.ToString()),
                    new("@day", day),
                    new("@hits", counter.Hits),
                    new("@damage", counter.Damage),
                    new("@headshots", counter.Headshots),
                    new("@kills", counter.Kills),
                    new("@shots", counter.Shots),
                    new("@rounds", counter.Rounds),
                    new("@seconds", counter.Seconds)
                };

                if (counter.Rating.HasValue) {
                    insertCols.Add("rating");
                    insertVals.Add("@rating");
                    updateClauses.Add("rating=@rating");
                    dailyParams.Add(new MySqlParameter("@rating", counter.Rating.Value));
                }
                if (counter.Points.HasValue) {
                    insertCols.Add("points");
                    insertVals.Add("@points");
                    updateClauses.Add("points=@points");
                    dailyParams.Add(new MySqlParameter("@points", counter.Points.Value));
                }

                writes.Add(($"INTO {DailyStatTable} ({string.Join(", ", insertCols)}, updated_at) " +
                    $"VALUES ({string.Join(", ", insertVals)}, NOW()) " +
                    $"ON DUPLICATE KEY UPDATE {string.Join(", ", updateClauses)}, updated_at=NOW()",
                    dailyParams.ToArray()));
            }

            foreach (var kv in duelTotalBatch) {
                var (steamId64, season) = kv.Key;
                var counter = kv.Value;

                writes.Add(($"INTO {DuelTotalTable} (steamid64, season, kills, deaths, headshots, assists, teamkills, suicides, updated_at) " +
                    "VALUES (@steamid64, @season, @kills, @deaths, @headshots, @assists, @teamkills, @suicides, NOW()) " +
                    "ON DUPLICATE KEY UPDATE kills=kills+@kills, deaths=deaths+@deaths, " +
                    "headshots=headshots+@headshots, assists=assists+@assists, " +
                    "teamkills=teamkills+@teamkills, suicides=suicides+@suicides, updated_at=NOW()",
                    new MySqlParameter[] {
                        new("@steamid64", steamId64.ToString()),
                        new("@season", season),
                        new("@kills", counter.Kills),
                        new("@deaths", counter.Deaths),
                        new("@headshots", counter.Headshots),
                        new("@assists", counter.Assists),
                        new("@teamkills", counter.TeamKills),
                        new("@suicides", counter.Suicides)
                    }));
            }

            foreach (var kv in serverStatBatch) {
                string season = kv.Key;
                var counter = kv.Value;

                writes.Add(($"INTO {ServerStatSeasonTable} (season, hits, damage, headshots, shots, rounds, updated_at) " +
                    "VALUES (@season, @hits, @damage, @headshots, @shots, @rounds, NOW()) " +
                    "ON DUPLICATE KEY UPDATE hits=hits+@hits, damage=damage+@damage, headshots=headshots+@headshots, " +
                    "shots=shots+@shots, rounds=rounds+@rounds, updated_at=NOW()",
                    new MySqlParameter[] {
                        new("@season", season),
                        new("@hits", counter.Hits),
                        new("@damage", counter.Damage),
                        new("@headshots", counter.Headshots),
                        new("@shots", counter.Shots),
                        new("@rounds", counter.Rounds)
                    }));
            }

            bool ok = writes.Count == 0 || database.ExecuteTransaction(writes) > 0;

            var unwrittenHits = ok ? new() : hitBatch;
            var unwrittenShots = ok ? new() : shotBatch;
            var unwrittenRounds = ok ? new() : roundBatch;
            var unwrittenDuels = ok ? new() : duelBatch;
            var unwrittenClutches = ok ? new() : clutchBatch;
            var unwrittenMultikills = ok ? new() : multikillBatch;
            var unwrittenDaily = ok ? new() : dailyBatch;
            var unwrittenDuelTotals = ok ? new() : duelTotalBatch;
            var unwrittenServerStats = ok ? new() : serverStatBatch;
            var unwrittenKnifeTaserKills = ok ? new() : knifeTaserBatch;
            var unwrittenMapResults = ok ? new() : mapResultBatch;

            Server.NextFrame(() => {
                flushInProgress = false;

                foreach (var kv in unwrittenHits) {
                    if (!pendingHitCounters.TryGetValue(kv.Key, out var existing)) {
                        pendingHitCounters[kv.Key] = kv.Value;
                    } else {
                        existing.Hits += kv.Value.Hits;
                        existing.Damage += kv.Value.Damage;
                    }
                }

                foreach (var kv in unwrittenShots) {
                    pendingShotCounters[kv.Key] = pendingShotCounters.GetValueOrDefault(kv.Key, 0) + kv.Value;
                }

                foreach (var kv in unwrittenRounds) {
                    if (!pendingRoundCounters.TryGetValue(kv.Key, out var existing)) {
                        pendingRoundCounters[kv.Key] = kv.Value;
                    } else {
                        existing.Rounds += kv.Value.Rounds;
                        existing.RoundsWon += kv.Value.RoundsWon;
                        existing.BombPlants += kv.Value.BombPlants;
                        existing.BombDefuses += kv.Value.BombDefuses;
                        existing.DefuseFails += kv.Value.DefuseFails;
                        existing.PlantsExploded += kv.Value.PlantsExploded;
                        existing.PlantsDefused += kv.Value.PlantsDefused;
                    }
                }

                foreach (var kv in unwrittenDuels) {
                    if (!pendingDuelCounters.TryGetValue(kv.Key, out var existing)) {
                        pendingDuelCounters[kv.Key] = kv.Value;
                    } else {
                        existing.Kills += kv.Value.Kills;
                        existing.Headshots += kv.Value.Headshots;
                        existing.Noscopes += kv.Value.Noscopes;
                        existing.Wallbangs += kv.Value.Wallbangs;
                        existing.BlindKills += kv.Value.BlindKills;
                        existing.SmokeKills += kv.Value.SmokeKills;
                        existing.Dominations += kv.Value.Dominations;
                        existing.Revenges += kv.Value.Revenges;
                    }
                }

                foreach (var kv in unwrittenClutches) {
                    if (!pendingClutchCounters.TryGetValue(kv.Key, out var existing)) {
                        pendingClutchCounters[kv.Key] = kv.Value;
                    } else {
                        existing.Attempts += kv.Value.Attempts;
                        existing.Wins += kv.Value.Wins;
                    }
                }

                foreach (var kv in unwrittenMultikills) {
                    pendingMultikillCounters[kv.Key] = pendingMultikillCounters.GetValueOrDefault(kv.Key, 0) + kv.Value;
                }

                foreach (var kv in unwrittenDaily) {
                    if (!pendingDailyCounters.TryGetValue(kv.Key, out var existing)) {
                        pendingDailyCounters[kv.Key] = kv.Value;
                    } else {
                        existing.Hits += kv.Value.Hits;
                        existing.Damage += kv.Value.Damage;
                        existing.Headshots += kv.Value.Headshots;
                        existing.Kills += kv.Value.Kills;
                        existing.Shots += kv.Value.Shots;
                        existing.Rounds += kv.Value.Rounds;
                        existing.Seconds += kv.Value.Seconds;

                        // Snapshot, not a counter -- whatever's already accumulated since the
                        // failed batch was pulled out is newer; only fall back to the
                        // retried value if nothing more recent exists yet.
                        existing.Rating ??= kv.Value.Rating;
                        existing.Points ??= kv.Value.Points;
                    }
                }

                foreach (var kv in unwrittenDuelTotals) {
                    if (!pendingDuelTotalCounters.TryGetValue(kv.Key, out var existing)) {
                        pendingDuelTotalCounters[kv.Key] = kv.Value;
                    } else {
                        // Fixed 2026-08-04 while adding TeamKills/Suicides below: this branch
                        // only merged Kills/Deaths back, silently dropping Headshots/Assists
                        // from a failed batch whenever new activity had already re-created the
                        // key in the meantime. Pre-existing, unrelated to the new fields --
                        // fixed here since it's the exact block being touched anyway.
                        existing.Kills += kv.Value.Kills;
                        existing.Deaths += kv.Value.Deaths;
                        existing.Headshots += kv.Value.Headshots;
                        existing.Assists += kv.Value.Assists;
                        existing.TeamKills += kv.Value.TeamKills;
                        existing.Suicides += kv.Value.Suicides;
                    }
                }

                foreach (var kv in unwrittenServerStats) {
                    if (!pendingServerStatCounters.TryGetValue(kv.Key, out var existing)) {
                        pendingServerStatCounters[kv.Key] = kv.Value;
                    } else {
                        existing.Hits += kv.Value.Hits;
                        existing.Damage += kv.Value.Damage;
                        existing.Headshots += kv.Value.Headshots;
                        existing.Shots += kv.Value.Shots;
                        existing.Rounds += kv.Value.Rounds;
                    }
                }

                pendingKnifeTaserKills.AddRange(unwrittenKnifeTaserKills);
                pendingMapResults.AddRange(unwrittenMapResults);

                int unwritten = unwrittenHits.Count + unwrittenShots.Count + unwrittenRounds.Count
                    + unwrittenDuels.Count + unwrittenClutches.Count + unwrittenMultikills.Count
                    + unwrittenDaily.Count + unwrittenDuelTotals.Count + unwrittenServerStats.Count
                    + unwrittenKnifeTaserKills.Count + unwrittenMapResults.Count;
                if (unwritten > 0) {
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}] database unavailable ({source}): kept {unwritten} stat rows cached for retry.");
                } else {
                    Console.WriteLine(
                        $"[DEBUG] OSBase[{ModuleName}] flushed pending stat writes ({source}): " +
                        $"hitRows={hitBatch.Count}, shotRows={shotBatch.Count}, roundRows={roundBatch.Count}, " +
                        $"duelRows={duelBatch.Count}, clutchRows={clutchBatch.Count}, multikillRows={multikillBatch.Count}, " +
                        $"dailyRows={dailyBatch.Count}, duelTotalRows={duelTotalBatch.Count}, serverStatRows={serverStatBatch.Count}, " +
                        $"knifeTaserRows={knifeTaserBatch.Count}, mapResultRows={mapResultBatch.Count}"
                    );
                }
            });
        });
    }

    private static bool IsRealHuman(CCSPlayerController? player) {
        if (player == null || !player.IsValid || player.IsHLTV || player.IsBot) {
            return false;
        }

        return player.SteamID > 0;
    }

    // Kept consistent across every OSBase table that carries a weapon column
    // (player_hit_stat, player_weapon_shots, player_duel_stat, elo_kill_event) -- corrected
    // 2026-08-04 (agent-chat #18): this used to say the goal was matching OSWeb's
    // ServerKillTracker::normaliseWeapon/player_kill_stat, but neither was ever built. Purely
    // internal consistency now, not a cross-repo join.
    // Public (2026-08-06): this is now also the single place that answers "is this a knife?"
    // for the whole plugin, not just this module. Mug.cs used to run its own copy of that
    // question (a bare `.Contains("knife")` on the raw weapon string) and got it wrong for
    // the bayonet, which this method already special-cases -- see the DDL comment on
    // knifeTaserKillTable for the incident. Fixed by deleting Mug's copy, not by teaching it
    // about "bayonet" too: a second hand-maintained knife list is what caused the gap, and
    // the next knife skin that isn't named knife_* would have hit it again. One definition,
    // asked twice, not two definitions kept in sync by hand.
    public static string NormalizeWeapon(string? weapon) {
        string normalized = (weapon ?? string.Empty).Trim().ToLowerInvariant();

        if (normalized.StartsWith("weapon_", StringComparison.Ordinal)) {
            normalized = normalized.Substring("weapon_".Length);
        }

        if (normalized.EndsWith("_projectile", StringComparison.Ordinal)) {
            normalized = normalized.Substring(0, normalized.Length - "_projectile".Length);
        }

        if (normalized.Length == 0) {
            return "unknown";
        }

        if (normalized.StartsWith("knife", StringComparison.Ordinal) || normalized.Contains("bayonet")) {
            return "knife";
        }

        if (normalized == "taser" || normalized == "zeus" || normalized == "zeusx27") {
            return "taser";
        }

        return normalized;
    }

    // Unlike NormalizeWeapon above, deliberately does NOT collapse every knife into "knife" --
    // osbase-stat-contracts.md section 4 wants "which knife" preserved (e.g. "knife_karambit",
    // "bayonet"), only the classification check (IsKnifeOrTaser) needs the collapsed form.
    private static string RawWeaponName(string? weapon) {
        string normalized = (weapon ?? string.Empty).Trim().ToLowerInvariant();

        if (normalized.StartsWith("weapon_", StringComparison.Ordinal)) {
            normalized = normalized.Substring("weapon_".Length);
        }

        return normalized.Length == 0 ? "unknown" : normalized;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt e) {
        if (!isActive) {
            return HookResult.Continue;
        }

        try {
            if (e.Userid == null || e.Attacker == null) {
                return HookResult.Continue;
            }

            if (!e.Userid.UserId.HasValue) {
                return HookResult.Continue;
            }

            if (e.DmgHealth <= 0) {
                return HookResult.Continue;
            }

            int victim = e.Userid.UserId.Value;
            int attacker = e.Attacker.UserId ?? ENVIRONMENT;

            if (attacker == victim && string.Equals(e.Weapon, "world", StringComparison.OrdinalIgnoreCase)) {
                attacker = ENVIRONMENT;
            }

            // Ensure nicknames are cached (simple O(1) lookup, no copies)
            if (attacker != ENVIRONMENT && !playerNames.ContainsKey(attacker)) {
                playerNames[attacker] = e.Attacker?.PlayerName ?? "Unknown";
            }
            if (!playerNames.ContainsKey(victim)) {
                playerNames[victim] = e.Userid?.PlayerName ?? "Unknown";
            }

            int damage = e.DmgHealth;
            int hitgroup = ReadHitgroupByteCompat(e);

            Ensure2(damageGiven, attacker);
            Ensure2(damageTaken, victim);
            Ensure2(hitsGiven, attacker);
            Ensure2(hitsTaken, victim);

            Ensure3(hitboxGiven, attacker, victim);
            Ensure3(hitboxTaken, victim, attacker);
            Ensure3(hitboxGivenDamage, attacker, victim);
            Ensure3(hitboxTakenDamage, victim, attacker);

            damageGiven[attacker][victim] = damageGiven[attacker].GetValueOrDefault(victim, 0) + damage;
            damageTaken[victim][attacker] = damageTaken[victim].GetValueOrDefault(attacker, 0) + damage;

            hitsGiven[attacker][victim] = hitsGiven[attacker].GetValueOrDefault(victim, 0) + 1;
            hitsTaken[victim][attacker] = hitsTaken[victim].GetValueOrDefault(attacker, 0) + 1;

            hitboxGiven[attacker][victim][hitgroup] = hitboxGiven[attacker][victim].GetValueOrDefault(hitgroup, 0) + 1;
            hitboxTaken[victim][attacker][hitgroup] = hitboxTaken[victim][attacker].GetValueOrDefault(hitgroup, 0) + 1;

            hitboxGivenDamage[attacker][victim][hitgroup] = hitboxGivenDamage[attacker][victim].GetValueOrDefault(hitgroup, 0) + damage;
            hitboxTakenDamage[victim][attacker][hitgroup] = hitboxTakenDamage[victim][attacker].GetValueOrDefault(hitgroup, 0) + damage;

            // Durable career counters for the site's body diagram -- bots/HLTV/world excluded,
            // this is personal data keyed to a real SteamID, not entertainment output. side is
            // the side each party was actually playing when the hit happened (attacker's side
            // for the dealt row, victim's for the received row -- they can differ). Gated by
            // statsGateOpen (ask 11): warmup and empty-server farming must never reach these
            // tables, decided once at round start.
            if (statsGateOpen) {
                string weaponKey = NormalizeWeapon(e.Weapon);
                string season = CurrentSeason();
                if (attacker != ENVIRONMENT && IsRealHuman(e.Attacker)) {
                    AddHitCounter(e.Attacker!.SteamID, weaponKey, hitgroup, DirectionDealt, MapSide(e.Attacker), season, damage);

                    // Ask 15/16b: daily form + server-wide roll-up track dealt output only
                    // (see the field comment on pendingDailyCounters). headshots for these two
                    // tables means headshot KILLS (ask 18), not headshot hits -- that's
                    // recorded in OnPlayerDeath instead, so 0 here; hit-level detail already
                    // exists unambiguously via hitgroup in player_hit_stat.
                    AddDailyStat(e.Attacker.SteamID, hits: 1, damage: damage, headshots: 0, kills: 0, shots: 0, rounds: 0, seconds: 0);
                    AddServerStat(season, hits: 1, damage: damage, headshots: 0, shots: 0, rounds: 0);
                }
                if (IsRealHuman(e.Userid)) {
                    AddHitCounter(e.Userid!.SteamID, weaponKey, hitgroup, DirectionReceived, MapSide(e.Userid), season, damage);
                }
            }

            return HookResult.Continue;
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception in OnPlayerHurt: {ex}");
            return HookResult.Continue;
        }
    }

    private HookResult OnPlayerDeath(EventPlayerDeath e) {
        if (!isActive) {
            return HookResult.Continue;
        }

        int victimId = e.Userid?.UserId ?? -1;
        int attackerId = e.Attacker?.UserId ?? -1;

        // Ensure nicknames are cached (simple O(1) lookup, no copies needed)
        if (e.Userid != null && victimId >= 0 && !playerNames.ContainsKey(victimId)) {
            playerNames[victimId] = e.Userid.PlayerName ?? "Unknown";
        }
        if (e.Attacker != null && attackerId >= 0 && !playerNames.ContainsKey(attackerId)) {
            playerNames[attackerId] = e.Attacker.PlayerName ?? "Unknown";
        }

        if (attackerId >= 0 && victimId >= 0) {
            EnsureKillSet(attackerId).Add(victimId);
        }

        if (victimId >= 0) {
            ScheduleDamageReport(victimId);
        }

        // World-damage suicide (fall, drowning, a lingering own molotov, ...) reports no
        // attacker at all, so it never reaches IsRealHuman(e.Attacker) below -- handled here
        // instead, same deliberately-separate suicides counter as the self-kill case further
        // down (which does have an attacker: itself). Excludes the bomb, found 2026-08-05:
        // it also reports no attacker but isn't the victim's own fault, so it must not count
        // toward the suicides column either.
        if (statsGateOpen && e.Attacker == null && IsRealHuman(e.Userid) &&
            !string.Equals(e.Weapon, "planted_c4", StringComparison.OrdinalIgnoreCase)) {
            AddDuelTotal(e.Userid!.SteamID, CurrentSeason(), kills: 0, deaths: 0, suicides: 1);
        }

        // Nemesis counters -- every kill on every server, not gated to a tournament match
        // window (that gate is EloRating's own scoring decision, separate from whether a duel
        // gets counted here). Self-kills are dropped by AddDuel (attackerId64==victimId64);
        // team kills are kept, distinguishable by attacker_side==victim_side. A kill that
        // happens after EventRoundEnd but before the next EventRoundStart (the round-end
        // freeze window) is still just a kill -- no post-round special case, deliberately;
        // the scoreboard counts it too.
        if (statsGateOpen) {
            try {
                if (IsRealHuman(e.Attacker) && IsRealHuman(e.Userid)) {
                    bool isTeamKill = e.Attacker!.TeamNum == e.Userid!.TeamNum;
                    string season = CurrentSeason();
                    string weaponClass = NormalizeWeapon(e.Weapon);

                    AddDuel(
                        e.Attacker.SteamID,
                        e.Userid.SteamID,
                        MapSide(e.Attacker),
                        MapSide(e.Userid),
                        weaponClass,
                        season,
                        e.Headshot,
                        e.Noscope,
                        e.Penetrated > 0,
                        e.Attackerblind,
                        e.Thrusmoke,
                        e.Dominated,
                        e.Revenge
                    );

                    // osbase-stat-contracts.md section 4. Team kills included, not filtered --
                    // see the field comment on pendingKnifeTaserKills for why (a raw event
                    // record, not an achievement counter).
                    if (weaponClass == "knife" || weaponClass == "taser") {
                        // Ask 27: read here, before Mug.cs's own EventPlayerDeath handler can
                        // move any money -- see the killer_money/victim_money comment on
                        // knifeTaserKillTable's DDL for why that ordering is guaranteed, not
                        // assumed. 0 if InGameMoneyServices isn't available (shouldn't happen
                        // for a real, valid, connected player, but it's nullable on the API).
                        AddKnifeTaserKill(
                            e.Attacker.SteamID,
                            e.Userid.SteamID,
                            MapSide(e.Attacker),
                            MapSide(e.Userid),
                            RawWeaponName(e.Weapon),
                            CurrentMap(),
                            eloRating?.CurrentMatchId,
                            e.Attacker.InGameMoneyServices?.Account ?? 0,
                            e.Userid.InGameMoneyServices?.Account ?? 0
                        );
                    }

                    // Ask 16a/24 roll-up: same scope as player_duel_stat above (team kills
                    // included, not filtered) -- it's the same numbers already in hand here.
                    AddDuelTotal(e.Attacker.SteamID, season, kills: 1, deaths: 0, headshots: e.Headshot ? 1 : 0);
                    AddDuelTotal(e.Userid.SteamID, season, kills: 0, deaths: 1);

                    // Found 2026-08-04 (per user ask): teamkills/suicides, deliberately
                    // additive-only counters kept separate from kills/deaths above -- those
                    // stay exactly as they already are, nothing here changes what they mean.
                    // A self-kill (attacker==victim, e.g. own grenade or the "kill" command)
                    // is a suicide, not a teamkill, even though isTeamKill above is trivially
                    // true for it (same TeamNum as itself) -- checked explicitly rather than
                    // reusing that flag for the wrong thing.
                    if (e.Attacker.SteamID == e.Userid.SteamID) {
                        AddDuelTotal(e.Userid.SteamID, season, kills: 0, deaths: 0, suicides: 1);
                    } else if (isTeamKill) {
                        AddDuelTotal(e.Attacker.SteamID, season, kills: 0, deaths: 0, teamKills: 1);
                    }

                    // Ask 24: assist half of the period summary. Same eligibility check as
                    // EloRating's assist reward (real human, not the attacker or victim
                    // themselves) -- kept independent rather than shared so this module never
                    // has to reach into EloRating's internals for something this small.
                    var assister = e.Assister;
                    if (IsRealHuman(assister) && assister!.SteamID != e.Attacker.SteamID && assister.SteamID != e.Userid.SteamID) {
                        AddDuelTotal(assister.SteamID, season, kills: 0, deaths: 0, assists: 1);
                    }

                    // Multikills count enemy eliminations, not team kills -- a "5k round" means
                    // five opponents down, matching the scoreboard's own kill count.
                    if (!isTeamKill && e.Attacker.UserId.HasValue) {
                        int uid = e.Attacker.UserId.Value;
                        roundKillCount[uid] = roundKillCount.GetValueOrDefault(uid, 0) + 1;

                        // Ask 18: kills and headshot-KILLS for "yesterday's highlights" and
                        // the server-wide roll-up -- same team-kill exclusion as multikills,
                        // for the same reason (a TK inflating a personal kill count would be
                        // nonsensical, same as it would for a multikill round).
                        int headshotKill = e.Headshot ? 1 : 0;
                        AddDailyStat(e.Attacker.SteamID, hits: 0, damage: 0, headshots: headshotKill, kills: 1, shots: 0, rounds: 0, seconds: 0);
                        AddServerStat(season, hits: 0, damage: 0, headshots: headshotKill, shots: 0, rounds: 0);
                    }
                }
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception recording duel stat in OnPlayerDeath: {ex}");
            }

            try {
                CheckClutchSituations();
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception in CheckClutchSituations: {ex}");
            }
        }

        return HookResult.Continue;
    }

    // Shots fired. hits/shots is only a valid percentage for single-projectile weapons --
    // shotguns (each pellet is its own EventPlayerHurt) and grenades (one throw, up to
    // several victims hurt) will read well over 100%, correctly, not as a bug. See
    // STATS-MODULE.md for which weapons that applies to; the site decides how to display
    // it, this module just counts.
    private HookResult OnWeaponFire(EventWeaponFire e) {
        if (!isActive) {
            return HookResult.Continue;
        }

        try {
            if (statsGateOpen && IsRealHuman(e.Userid)) {
                string season = CurrentSeason();
                AddShot(e.Userid!.SteamID, NormalizeWeapon(e.Weapon), MapSide(e.Userid), season);
                AddDailyStat(e.Userid.SteamID, hits: 0, damage: 0, headshots: 0, kills: 0, shots: 1, rounds: 0, seconds: 0);
                AddServerStat(season, hits: 0, damage: 0, headshots: 0, shots: 1, rounds: 0);
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception in OnWeaponFire: {ex}");
        }

        return HookResult.Continue;
    }

    private HookResult OnBombPlanted(EventBombPlanted e) {
        if (!isActive) {
            return HookResult.Continue;
        }

        try {
            if (statsGateOpen && IsRealHuman(e.Userid)) {
                AddBombPlant(e.Userid!.SteamID, MapSide(e.Userid), CurrentSeason(), CurrentMap());

                // Ask 26: remember who planted, so the eventual explosion/defuse can credit
                // them regardless of who's standing there when it resolves.
                plantedBySteamId64 = e.Userid!.SteamID;
                plantedBySide = MapSide(e.Userid);
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception in OnBombPlanted: {ex}");
        }

        return HookResult.Continue;
    }

    private HookResult OnBombDefused(EventBombDefused e) {
        if (!isActive) {
            return HookResult.Continue;
        }

        try {
            if (e.Userid?.UserId.HasValue == true) {
                roundDefuseBegan.Remove(e.Userid.UserId.Value);
            }

            if (statsGateOpen && IsRealHuman(e.Userid)) {
                AddBombDefuse(e.Userid!.SteamID, MapSide(e.Userid), CurrentSeason(), CurrentMap());
            }

            // Ask 26: this is the planter's outcome, not the defuser's -- crediting whoever
            // defused is AddBombDefuse above, already keyed off e.Userid correctly.
            if (statsGateOpen && plantedBySteamId64.HasValue) {
                AddPlantDefused(plantedBySteamId64.Value, plantedBySide, CurrentSeason(), CurrentMap());
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception in OnBombDefused: {ex}");
        } finally {
            plantedBySteamId64 = null;
            plantedBySide = SideUnknown;
        }

        return HookResult.Continue;
    }

    private HookResult OnBombExploded(EventBombExploded e) {
        if (!isActive) {
            return HookResult.Continue;
        }

        try {
            // Ask 26: deliberately not e.Userid -- that's whoever's near the bomb when it
            // detonates, not who planted it (see the roundStatTable comment above). The
            // planter is tracked separately from EventBombPlanted and survives them
            // disconnecting before the timer runs out.
            if (statsGateOpen && plantedBySteamId64.HasValue) {
                AddPlantExploded(plantedBySteamId64.Value, plantedBySide, CurrentSeason(), CurrentMap());
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception in OnBombExploded: {ex}");
        } finally {
            plantedBySteamId64 = null;
            plantedBySide = SideUnknown;
        }

        return HookResult.Continue;
    }

    private HookResult OnBombBeginDefuse(EventBombBegindefuse e) {
        if (!isActive) {
            return HookResult.Continue;
        }

        try {
            if (statsGateOpen && e.Userid?.UserId.HasValue == true) {
                roundDefuseBegan.Add(e.Userid.UserId.Value);
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception in OnBombBeginDefuse: {ex}");
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart _) {
        if (!isActive) {
            return HookResult.Continue;
        }

        CancelAllPendingReports();
        ClearDamageData();
        UpdatePlayerNames();

        // Safety net: these are normally drained and cleared at the end of OnRoundEnd, but a
        // missed round-end (plugin reload mid-round, etc.) shouldn't leak state into the next
        // round.
        roundDefuseBegan.Clear();
        roundKillCount.Clear();
        clutchFlaggedThisRound.Clear();
        roundClutchCandidates.Clear();
        plantedBySteamId64 = null;
        plantedBySide = SideUnknown;

        // roundStagingCounters is NOT simply cleared like its siblings above. Those hold
        // in-progress attempts whose outcome genuinely never happened without a round end
        // (no defuse fail completed, no clutch resolved) -- dropping them is correct.
        // roundStagingCounters holds things that ALREADY happened (a bomb WAS planted) and,
        // pre-ask-30, wrote straight into pendingRoundCounters with no dependency on
        // EventRoundEnd at all. Dropping it here would silently undercount bomb_plants/
        // bomb_defuses/plants_exploded/plants_defused for every round that never resolves
        // (map change mid-round, mp_restartgame, crash, server empties) -- a behavior
        // regression hidden inside a schema change. Drained under RoundEndReasonUnknown
        // instead, so the round becomes a visible bucket rather than a silent subtraction.
        DrainRoundStagingCounters(RoundEndReasonUnknown);

        // osbase-stat-contracts.md section 5's third requirement: flush on the way out, so a
        // fast round can never let more than one round's worth of writes queue up behind the
        // delay timer above. If the timer already fired this is a no-op (nothing pending).
        pendingFlushTimer?.Kill();
        pendingFlushTimer = null;
        FlushPendingStats("RoundStart");

        // Decided here, not at round end -- see the field comment on statsGateOpen. Held for
        // the whole round regardless of who connects/disconnects while it's live.
        bool warmup = IsWarmupActive();
        int humans = CountConnectedHumans();
        statsGateOpen = !warmup && humans >= minPlayers;
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: round gate {(statsGateOpen ? "open" : "closed")} (humans={humans} min={minPlayers} warmup={warmup})");

        // Ask 29: the map's first round, now that team assignment has had a chance to
        // settle -- see the field comment on captureMapStartSideNext for why this doesn't
        // happen in OnMapStart itself.
        if (captureMapStartSideNext) {
            captureMapStartSideNext = false;
            mapStartSide.Clear();
            foreach (var p in Utilities.GetPlayers()) {
                if (IsRealHuman(p)) {
                    mapStartSide[p.SteamID] = MapSide(p);
                }
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd e) {
        if (!isActive) {
            return HookResult.Continue;
        }

        string season = CurrentSeason();
        string map = CurrentMap();
        int winnerSide = MapWinnerSide(e.Winner);

        // Ask 29: one map-level counter, not per-player -- gated the same as everything else
        // in this round, so a map that spent most of its time under-populated ends with a
        // low count rather than the engine's raw round total.
        if (statsGateOpen) {
            roundsThisMap += 1;
        }

        foreach (var p in Utilities.GetPlayers()) {
            if (p == null || !p.IsValid || p.IsHLTV || !p.UserId.HasValue) {
                continue;
            }

            ScheduleDamageReport(p.UserId.Value);

            // For damage/round: one round played, for whichever side they were actually on.
            // Spectators/unassigned (MapSide -> unknown) still get a row -- excluding them
            // would silently drop anyone who spent the round on a team that then swapped, and
            // "unknown" is a legitimate, filterable bucket, not an error state. Gated by
            // statsGateOpen, decided at this round's start, not re-checked here.
            if (statsGateOpen && IsRealHuman(p)) {
                int side = MapSide(p);
                // Confirmed 2026-08-04 (agent-chat #29/#30): "played this round" and
                // "won this round" are both presence/side checks at round END, not round
                // START -- a player who connected mid-round still gets +1 rounds, and a
                // player who died mid-round still gets rounds_won if their side took it.
                // Neither is a bug; there's no round-start snapshot to compare against.
                AddRoundPlayed(p.SteamID, side, season, map, won: side == winnerSide);

                // seconds = time since THIS player's last round-end sample, not time alive
                // and not total session time -- a rolling per-round-end delta. A player's
                // first-ever sample has nothing to diff against, so it contributes 0, not
                // that round's actual duration.
                int seconds = 0;
                DateTime now = DateTime.UtcNow;
                if (lastActivitySample.TryGetValue(p.UserId!.Value, out DateTime lastSample)) {
                    seconds = Math.Max(0, (int)(now - lastSample).TotalSeconds);
                }
                lastActivitySample[p.UserId!.Value] = now;

                // Ask 22: end-of-day rating/points snapshot -- read live from EloRating
                // (never a DB round trip that could race its own flush), null if the module
                // isn't loaded rather than a false "rating 0".
                int? ratingSnapshot = null;
                int? pointsSnapshot = null;
                if (eloRating != null) {
                    if (eloRating.TryGetRating(p.SteamID, out int liveRatingValue, out _)) {
                        ratingSnapshot = liveRatingValue;
                    }
                    if (eloRating.TryGetPoints(p.SteamID, season, out int livePointsValue)) {
                        pointsSnapshot = livePointsValue;
                    }
                }

                AddDailyStat(p.SteamID, hits: 0, damage: 0, headshots: 0, kills: 0, shots: 0, rounds: 1, seconds: seconds, rating: ratingSnapshot, points: pointsSnapshot);
                AddServerStat(season, hits: 0, damage: 0, headshots: 0, shots: 0, rounds: 1);
            }
        }

        try {
            // Clutch attempts were flagged mid-round against however many opponents were alive
            // at the time; resolve win/loss now against the round's actual winner. A lost
            // clutch still produced an attempt row above -- that's the whole point of counting
            // attempts instead of only wins.
            foreach (var (steamId64, side, opponents) in roundClutchCandidates) {
                AddClutch(steamId64, side, season, opponents, side == winnerSide);
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception resolving clutch stats in OnRoundEnd: {ex}");
        } finally {
            roundClutchCandidates.Clear();
            clutchFlaggedThisRound.Clear();
        }

        try {
            // Anyone who began a defuse this round without a matching EventBombDefused for
            // themselves failed it -- interrupted, killed mid-defuse, or the round just ended.
            foreach (int userId in roundDefuseBegan) {
                var p = FindPlayerByUserId(userId);
                if (IsRealHuman(p)) {
                    AddDefuseFail(p!.SteamID, MapSide(p), season, map);
                }
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception resolving defuse fails in OnRoundEnd: {ex}");
        } finally {
            roundDefuseBegan.Clear();
        }

        try {
            foreach (var kv in roundKillCount) {
                var p = FindPlayerByUserId(kv.Key);
                if (IsRealHuman(p) && kv.Value > 0) {
                    AddMultikillRound(p!.SteamID, MapSide(p), season, kv.Value);
                }
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception resolving multikill stats in OnRoundEnd: {ex}");
        } finally {
            roundKillCount.Clear();
        }

        // Ask 30: last, now that every Add* call above for this round has run and staged
        // its contribution -- e.Reason is the game's own value, stored as-is (no invented
        // encoding, same rule as side).
        try {
            DrainRoundStagingCounters(e.Reason);
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception draining round staging counters in OnRoundEnd: {ex}");
        }

        // Capture is already done above (everything's in the pending dictionaries now);
        // only the flush itself is delayed, off the exact round-end tick.
        pendingFlushTimer?.Kill();
        pendingFlushTimer = osbase?.AddTimer(RoundEndFlushDelaySeconds, () => {
            pendingFlushTimer = null;
            FlushPendingStats("RoundEnd");
        });

        return HookResult.Continue;
    }

    private HookResult OnPlayerConnect(EventPlayerConnect _) {
        if (!isActive) {
            return HookResult.Continue;
        }

        UpdatePlayerNames();
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnectEvent(EventPlayerDisconnect e) {
        if (!isActive) {
            return HookResult.Continue;
        }

        if (e.Userid?.UserId != null) {
            OnPlayerDisconnect(e.Userid.UserId.Value);
        }

        return HookResult.Continue;
    }

    private void UpdatePlayerNames() {
        try {
            foreach (var p in Utilities.GetPlayers()) {
                if (p == null || !p.UserId.HasValue) {
                    continue;
                }

                int id = p.UserId.Value;
                playerNames[id] = string.IsNullOrEmpty(p.PlayerName) ? "Bot" : p.PlayerName;
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception in UpdatePlayerNames: {ex}");
        }
    }

    private void PopulateMissingPlayerNames() {
        try {
            // For all players in damage data, ensure we have their names
            var allPlayerIds = new HashSet<int>();
            
            // Collect all player IDs from damage data
            foreach (var giver in damageGiven.Keys) allPlayerIds.Add(giver);
            foreach (var taker in damageTaken.Keys) allPlayerIds.Add(taker);
            foreach (var pair in damageGiven) {
                foreach (var victimId in pair.Value.Keys) allPlayerIds.Add(victimId);
            }
            foreach (var pair in damageTaken) {
                foreach (var attackerId in pair.Value.Keys) allPlayerIds.Add(attackerId);
            }

            // For each player ID not yet in playerNames, try to find their name
            foreach (int id in allPlayerIds) {
                if (!playerNames.ContainsKey(id)) {
                    // Try to find active player
                    var p = FindPlayerByUserId(id);
                    if (p != null && !string.IsNullOrEmpty(p.PlayerName)) {
                        playerNames[id] = p.PlayerName;
                    } else if (!playerNames.ContainsKey(id)) {
                        // Keep as Unknown placeholder for disconnected players
                        playerNames[id] = "Unknown";
                    }
                }
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception in PopulateMissingPlayerNames: {ex}");
        }
    }

    private void ScheduleDamageReport(int userId) {
        if (osbase == null || !isActive) {
            return;
        }

        if (pendingReports.ContainsKey(userId)) {
            return;
        }

        Timer? timer = null;
        timer = osbase.AddTimer(DELAY_SECONDS, () => {
            try {
                if (!isActive) {
                    return;
                }

                CCSPlayerController? p = FindPlayerByUserId(userId);
                if (p == null || !p.IsValid || p.IsHLTV || !p.UserId.HasValue) {
                    return;
                }

                DisplayDamageReport(p);
            } finally {
                if (timer != null) {
                    pendingReports.Remove(userId);
                }
            }
        });

        pendingReports[userId] = timer;
    }

    private CCSPlayerController? FindPlayerByUserId(int userId) {
        foreach (var p in Utilities.GetPlayers()) {
            if (p == null || !p.IsValid || !p.UserId.HasValue) {
                continue;
            }

            if (p.UserId.Value == userId) {
                return p;
            }
        }

        return null;
    }

    private void DisplayDamageReport(CCSPlayerController player) {
        if (player == null || !player.IsValid || !player.UserId.HasValue) {
            return;
        }

        int playerId = player.UserId.Value;

        // Sync active player names before report
        UpdatePlayerNames();
        
        // Populate nicknames for all involved players (even disconnected ones)
        PopulateMissingPlayerNames();

        bool hasVictimData = damageGiven.ContainsKey(playerId) && damageGiven[playerId].Count > 0;
        bool hasAttackerData = damageTaken.ContainsKey(playerId) && damageTaken[playerId].Count > 0;

        if (!hasVictimData && !hasAttackerData) {
            return;
        }

        player.PrintToChat("===[ Damage Report (hits:damage) ]===");

        if (hasVictimData) {
            player.PrintToChat("Victims:");
            foreach (var v in damageGiven[playerId]) {
                int victimId = v.Key;
                int dmg = v.Value;
                int hits = hitsGiven[playerId].GetValueOrDefault(victimId, 0);

                string victimName = playerNames.GetValueOrDefault(victimId, "Unknown");
                string killedText = (killedPlayer.ContainsKey(playerId) && killedPlayer[playerId].Contains(victimId)) ? " (Killed)" : "";

                string hitInfo = BuildHitInfo(hitboxGiven, hitboxGivenDamage, playerId, victimId, dmg);
                player.PrintToChat($"- {victimName}{killedText}: {hits} hits, {dmg} damage{hitInfo}");
            }
        }

        if (hasAttackerData) {
            player.PrintToChat("Attackers:");
            foreach (var a in damageTaken[playerId]) {
                int attackerId = a.Key;
                int dmg = a.Value;
                int hits = hitsTaken[playerId].GetValueOrDefault(attackerId, 0);

                string attackerName = playerNames.GetValueOrDefault(attackerId, "Unknown");
                string killedByText = (killedPlayer.ContainsKey(attackerId) && killedPlayer[attackerId].Contains(playerId)) ? " (Killed by)" : "";

                string hitInfo = BuildHitInfo(hitboxTaken, hitboxTakenDamage, playerId, attackerId, dmg);
                player.PrintToChat($"- {attackerName}{killedByText}: {hits} hits, {dmg} damage{hitInfo}");
            }
        }
    }

    private string BuildHitInfo(
        Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitCounts,
        Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitDamages,
        int a,
        int b,
        int totalDamage
    ) {
        if (!hitCounts.ContainsKey(a) || !hitCounts[a].ContainsKey(b)) {
            return "";
        }

        int calc = 0;
        var parts = new List<string>();

        foreach (var kv in hitCounts[a][b]) {
            int hg = kv.Key;
            int count = kv.Value;

            int dmg = 0;
            if (hitDamages.ContainsKey(a) && hitDamages[a].ContainsKey(b)) {
                dmg = hitDamages[a][b].GetValueOrDefault(hg, 0);
            }

            calc += dmg;
            parts.Add($"{GetHitgroupLabel(hg)} {count}:{dmg}");
        }

        string s = " [" + string.Join(", ", parts) + "]";
        if (calc != totalDamage) {
            s += $" [Inconsistent: {totalDamage} != {calc}]";
        }

        return s;
    }

    private string GetHitgroupLabel(int hgByte) {
        if (hgByte >= 0 && hgByte < hitboxName.Length) {
            return hitboxName[hgByte];
        }

        return $"U{hgByte}({hgByte})";
    }

    private void ClearDamageData() {
        damageGiven.Clear();
        damageTaken.Clear();
        hitsGiven.Clear();
        hitsTaken.Clear();
        killedPlayer.Clear();

        hitboxGiven.Clear();
        hitboxTaken.Clear();
        hitboxGivenDamage.Clear();
        hitboxTakenDamage.Clear();

        playerNames.Clear();  // Also clear names with damage data

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Damage data cleared.");
    }

    private void OnPlayerDisconnect(int playerId) {
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Player disconnected: ID={playerId}");

        CancelPendingReport(playerId);

        damageGiven.Remove(playerId);
        damageTaken.Remove(playerId);
        hitsGiven.Remove(playerId);
        hitsTaken.Remove(playerId);

        hitboxGiven.Remove(playerId);
        hitboxTaken.Remove(playerId);
        hitboxGivenDamage.Remove(playerId);
        hitboxTakenDamage.Remove(playerId);

        killedPlayer.Remove(playerId);
        lastActivitySample.Remove(playerId);

        // Keep playerNames for disconnected players - other players' reports may reference them
        // playerNames.Remove(playerId);  // REMOVED: preserve names for damage reports
    }

    private void CancelPendingReport(int playerId) {
        if (!pendingReports.TryGetValue(playerId, out var timer)) {
            return;
        }

        timer.Kill();
        pendingReports.Remove(playerId);
    }

    private void CancelAllPendingReports() {
        foreach (var timer in pendingReports.Values) {
            timer.Kill();
        }

        pendingReports.Clear();
    }

    private HashSet<int> EnsureKillSet(int attackerId) {
        if (!killedPlayer.ContainsKey(attackerId)) {
            killedPlayer[attackerId] = new HashSet<int>();
        }

        return killedPlayer[attackerId];
    }

    private static void Ensure2(Dictionary<int, Dictionary<int, int>> map, int a) {
        if (!map.ContainsKey(a)) {
            map[a] = new Dictionary<int, int>();
        }
    }

    private static void Ensure3(Dictionary<int, Dictionary<int, Dictionary<int, int>>> map, int a, int b) {
        if (!map.ContainsKey(a)) {
            map[a] = new Dictionary<int, Dictionary<int, int>>();
        }

        if (!map[a].ContainsKey(b)) {
            map[a][b] = new Dictionary<int, int>();
        }
    }

    private static int ReadHitgroupByteCompat(EventPlayerHurt e) {
        int hg;
        try {
            hg = e.Hitgroup;
        } catch {
            hg = 0;
        }

        if (hg >= 0 && hg <= 255) {
            return hg;
        }

        return TryGetGameEventByte(e, "hitgroup");
    }

    private static byte TryGetGameEventByte(GameEvent ev, string key) {
        try {
            MethodInfo? mi = typeof(GameEvent).GetMethod("Get", BindingFlags.Instance | BindingFlags.NonPublic);
            if (mi == null) {
                return 0;
            }

            MethodInfo g = mi.MakeGenericMethod(typeof(byte));
            object? val = g.Invoke(ev, new object[] { key });

            if (val is byte b) {
                return b;
            }

            if (val != null) {
                return Convert.ToByte(val, CultureInfo.InvariantCulture);
            }

            return 0;
        } catch {
            return 0;
        }
    }
}