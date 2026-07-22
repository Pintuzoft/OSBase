using System;
using System.Collections.Generic;
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
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace OSBase.Modules;

// Alongside its ephemeral per-round chat report, this module persists the durable,
// career-long counters that back the site's profile stats: body-diagram heatmap, per-weapon
// accuracy, ADR, nemesis lists, clutches, multikills. See STATS-MODULE.md. Every table is
// dimensioned by side and (where noted) season at write time -- a dimension left out of the
// primary key before writing starts can never be split back out of an already-summed
// counter, so these are decided once, here, not patched in later:
//   player_hit_stat       (steamid64, weapon, hitgroup, direction, side, season) -> hits, damage
//   player_weapon_shots   (steamid64, weapon, side, season) -> shots
//   player_round_stat     (steamid64, side, season) -> rounds, bomb_plants, bomb_defuses,
//                         defuse_fails; rounds is ADR's denominator (damage / rounds)
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
    private const int DirectionDealt = 0;
    private const int DirectionReceived = 1;

    // side: 0=T, 1=CT, 2=unknown (spectator/mid-transition/unresolved at write time)
    private const int SideT = 0;
    private const int SideCT = 1;
    private const int SideUnknown = 2;

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

    // Ask 11: a filter, not a counter. Decided once at round start and held for the whole
    // round -- re-evaluating at round end would silently exclude normal play whenever people
    // log off late in the evening. Two warm-body pub players farming AWP kills on an empty
    // server would otherwise feed 100% headshot and a 1v1 clutch every round into the exact
    // same lifetime counters as real play; a lifetime counter can't unlearn that later.
    private bool statsGateOpen;
    private int minPlayers = 4;
    private readonly Dictionary<(ulong SteamId64, string Weapon, int Hitgroup, int Direction, int Side, string Season), PendingHitCounter> pendingHitCounters = new();
    private readonly Dictionary<(ulong SteamId64, string Weapon, int Side, string Season), int> pendingShotCounters = new();
    private readonly Dictionary<(ulong SteamId64, int Side, string Season, string Map), PendingRoundCounter> pendingRoundCounters = new();
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

    // Round-scoped state, resolved into the pending counters above at round end, cleared at
    // round start as a safety net.
    private readonly HashSet<int> roundDefuseBegan = new();          // userId -> began a defuse, no matching EventBombDefused yet
    private readonly Dictionary<int, int> roundKillCount = new();    // userId -> kills this round (team kills excluded)
    private readonly HashSet<ulong> clutchFlaggedThisRound = new();  // steamid64 already recorded as clutching this round
    private readonly List<(ulong SteamId64, int Side, int Opponents)> roundClutchCandidates = new();

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

    protected override void OnLoad() {
        CreateCustomConfigs();
        LoadConfig();

        db = new Database(osbase!, config!);
        CreateTables();
        eloRating = osbase?.GetModule<EloRating>();
    }

    protected override void OnUnload() {
        CancelAllPendingReports();
        FlushPendingStats("Unload");
        ClearDamageData();
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
        osbase?.SubscribeToEvent<EventBombBegindefuse>(OnBombBeginDefuse);
        osbase?.SubscribeToEvent<EventRoundStart>(OnRoundStart);
        osbase?.SubscribeToEvent<EventRoundEnd>(OnRoundEnd);
        osbase?.SubscribeToEvent<EventPlayerConnect>(OnPlayerConnect);
        osbase?.SubscribeToEvent<EventPlayerDisconnect>(OnPlayerDisconnectEvent);
        osbase?.RegisterListener<Listeners.OnMapStart>(OnMapStart);
    }

    protected override void UnregisterHandlers() {
        // Use new EventBus system (Unsubscribe instead of DeregisterEventHandler)
        osbase?.UnsubscribeFromEvent<EventPlayerHurt>(OnPlayerHurt);
        osbase?.UnsubscribeFromEvent<EventPlayerDeath>(OnPlayerDeath);
        osbase?.UnsubscribeFromEvent<EventWeaponFire>(OnWeaponFire);
        osbase?.UnsubscribeFromEvent<EventBombPlanted>(OnBombPlanted);
        osbase?.UnsubscribeFromEvent<EventBombDefused>(OnBombDefused);
        osbase?.UnsubscribeFromEvent<EventBombBegindefuse>(OnBombBeginDefuse);
        osbase?.UnsubscribeFromEvent<EventRoundStart>(OnRoundStart);
        osbase?.UnsubscribeFromEvent<EventRoundEnd>(OnRoundEnd);
        osbase?.UnsubscribeFromEvent<EventPlayerConnect>(OnPlayerConnect);
        osbase?.UnsubscribeFromEvent<EventPlayerDisconnect>(OnPlayerDisconnectEvent);
        osbase?.RemoveListener<Listeners.OnMapStart>(OnMapStart);
    }

    private void OnMapStart(string mapName) {
        if (!isActive) {
            return;
        }

        FlushPendingStats("MapStart");
    }

    // All four tables below are owned by this module alone -- never write to the site's
    // player_kill_stat (owned by OSWeb's ServerKillTracker off the log stream); two
    // writers on the same counters double-count silently. steamid64/attackerid64/victimid64
    // are all VARCHAR(32) -- a Steam64 overflows JS's safe-integer range -- never BIGINT.
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

        // For ADR (average damage per round) = SUM(player_hit_stat.damage WHERE
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
        string roundStatTable = $"""
        TABLE IF NOT EXISTS {RoundStatTable} (
            steamid64    VARCHAR(32) NOT NULL,
            side         TINYINT UNSIGNED NOT NULL,
            season       VARCHAR(8) NOT NULL,
            map          VARCHAR(32) NOT NULL,
            rounds       INT NOT NULL DEFAULT 0,
            rounds_won   INT NOT NULL DEFAULT 0,
            bomb_plants  INT NOT NULL DEFAULT 0,
            bomb_defuses INT NOT NULL DEFAULT 0,
            defuse_fails INT NOT NULL DEFAULT 0,
            first_seen   DATETIME NOT NULL,
            updated_at   DATETIME NOT NULL,
            PRIMARY KEY (steamid64, side, season, map)
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
        string duelTotalTable = $"""
        TABLE IF NOT EXISTS {DuelTotalTable} (
            steamid64  VARCHAR(32) NOT NULL,
            season     VARCHAR(8) NOT NULL,
            kills      INT NOT NULL DEFAULT 0,
            deaths     INT NOT NULL DEFAULT 0,
            headshots  INT NOT NULL DEFAULT 0,
            assists    INT NOT NULL DEFAULT 0,
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
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] tables ensured.");
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed creating tables: {e.Message}");
        }
    }

    private void AddHitCounter(ulong steamId64, string weapon, int hitgroup, int direction, int side, string season, int damage) {
        if (steamId64 == 0) {
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
        if (steamId64 == 0) {
            return;
        }

        var key = (steamId64, weapon, side, season);
        pendingShotCounters[key] = pendingShotCounters.GetValueOrDefault(key, 0) + 1;
    }

    private PendingRoundCounter GetOrCreateRoundCounter(ulong steamId64, int side, string season, string map) {
        var key = (steamId64, side, season, map);
        if (!pendingRoundCounters.TryGetValue(key, out var counter)) {
            counter = new PendingRoundCounter();
            pendingRoundCounters[key] = counter;
        }

        return counter;
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

    private void AddDuelTotal(ulong steamId64, string season, int kills, int deaths, int headshots = 0, int assists = 0) {
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
        if (attackerId64 == 0 || victimId64 == 0 || attackerId64 == victimId64) {
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
    private static string CurrentSeason() {
        DateTime now = DateTime.UtcNow;
        int quarter = ((now.Month - 1) / 3) + 1;
        return $"{now.Year}Q{quarter}";
    }

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
            && pendingServerStatCounters.Count == 0) {
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

        flushInProgress = true;

        Task.Run(() => {
            var unwrittenHits = new List<KeyValuePair<(ulong SteamId64, string Weapon, int Hitgroup, int Direction, int Side, string Season), PendingHitCounter>>();
            var unwrittenShots = new List<KeyValuePair<(ulong SteamId64, string Weapon, int Side, string Season), int>>();
            var unwrittenRounds = new List<KeyValuePair<(ulong SteamId64, int Side, string Season, string Map), PendingRoundCounter>>();
            var unwrittenDuels = new List<KeyValuePair<(ulong AttackerId64, ulong VictimId64, int AttackerSide, int VictimSide, string Weapon, string Season), PendingDuelCounter>>();
            var unwrittenClutches = new List<KeyValuePair<(ulong SteamId64, int Side, string Season, int Opponents), PendingClutchCounter>>();
            var unwrittenMultikills = new List<KeyValuePair<(ulong SteamId64, int Side, string Season, int Kills), int>>();
            var unwrittenDaily = new List<KeyValuePair<(ulong SteamId64, DateTime Day), PendingDailyCounter>>();
            var unwrittenDuelTotals = new List<KeyValuePair<(ulong SteamId64, string Season), PendingDuelTotalCounter>>();
            var unwrittenServerStats = new List<KeyValuePair<string, PendingServerStatCounter>>();
            bool dbDown = false;

            foreach (var kv in hitBatch) {
                if (dbDown) {
                    unwrittenHits.Add(kv);
                    continue;
                }

                var (steamId64, weapon, hitgroup, direction, side, season) = kv.Key;
                var counter = kv.Value;

                int affected = database.insert(
                    $"INTO {HitStatTable} (steamid64, weapon, hitgroup, direction, side, season, hits, damage, first_seen, updated_at) " +
                    "VALUES (@steamid64, @weapon, @hitgroup, @direction, @side, @season, @hits, @damage, NOW(), NOW()) " +
                    "ON DUPLICATE KEY UPDATE hits=hits+@hits, damage=damage+@damage, updated_at=NOW()",
                    new MySqlParameter("@steamid64", steamId64.ToString()),
                    new MySqlParameter("@weapon", weapon),
                    new MySqlParameter("@hitgroup", hitgroup),
                    new MySqlParameter("@direction", direction),
                    new MySqlParameter("@side", side),
                    new MySqlParameter("@season", season),
                    new MySqlParameter("@hits", counter.Hits),
                    new MySqlParameter("@damage", counter.Damage)
                );

                if (affected == 0) {
                    dbDown = true;
                    unwrittenHits.Add(kv);
                }
            }

            foreach (var kv in shotBatch) {
                if (dbDown) {
                    unwrittenShots.Add(kv);
                    continue;
                }

                var (steamId64, weapon, side, season) = kv.Key;

                int affected = database.insert(
                    $"INTO {ShotStatTable} (steamid64, weapon, side, season, shots, first_seen, updated_at) " +
                    "VALUES (@steamid64, @weapon, @side, @season, @shots, NOW(), NOW()) " +
                    "ON DUPLICATE KEY UPDATE shots=shots+@shots, updated_at=NOW()",
                    new MySqlParameter("@steamid64", steamId64.ToString()),
                    new MySqlParameter("@weapon", weapon),
                    new MySqlParameter("@side", side),
                    new MySqlParameter("@season", season),
                    new MySqlParameter("@shots", kv.Value)
                );

                if (affected == 0) {
                    dbDown = true;
                    unwrittenShots.Add(kv);
                }
            }

            foreach (var kv in roundBatch) {
                if (dbDown) {
                    unwrittenRounds.Add(kv);
                    continue;
                }

                var (steamId64, side, season, map) = kv.Key;
                var counter = kv.Value;

                int affected = database.insert(
                    $"INTO {RoundStatTable} (steamid64, side, season, map, rounds, rounds_won, bomb_plants, bomb_defuses, defuse_fails, first_seen, updated_at) " +
                    "VALUES (@steamid64, @side, @season, @map, @rounds, @rounds_won, @plants, @defuses, @fails, NOW(), NOW()) " +
                    "ON DUPLICATE KEY UPDATE rounds=rounds+@rounds, rounds_won=rounds_won+@rounds_won, " +
                    "bomb_plants=bomb_plants+@plants, bomb_defuses=bomb_defuses+@defuses, defuse_fails=defuse_fails+@fails, updated_at=NOW()",
                    new MySqlParameter("@steamid64", steamId64.ToString()),
                    new MySqlParameter("@side", side),
                    new MySqlParameter("@season", season),
                    new MySqlParameter("@map", map),
                    new MySqlParameter("@rounds", counter.Rounds),
                    new MySqlParameter("@rounds_won", counter.RoundsWon),
                    new MySqlParameter("@plants", counter.BombPlants),
                    new MySqlParameter("@defuses", counter.BombDefuses),
                    new MySqlParameter("@fails", counter.DefuseFails)
                );

                if (affected == 0) {
                    dbDown = true;
                    unwrittenRounds.Add(kv);
                }
            }

            foreach (var kv in duelBatch) {
                if (dbDown) {
                    unwrittenDuels.Add(kv);
                    continue;
                }

                var (attackerId64, victimId64, attackerSide, victimSide, weapon, season) = kv.Key;
                var counter = kv.Value;

                int affected = database.insert(
                    $"INTO {DuelStatTable} (attackerid64, victimid64, attacker_side, victim_side, weapon, season, " +
                    "kills, headshots, noscopes, wallbangs, blind_kills, smoke_kills, dominations, revenges, first_seen, updated_at) " +
                    "VALUES (@attackerid64, @victimid64, @attacker_side, @victim_side, @weapon, @season, " +
                    "@kills, @headshots, @noscopes, @wallbangs, @blind_kills, @smoke_kills, @dominations, @revenges, NOW(), NOW()) " +
                    "ON DUPLICATE KEY UPDATE kills=kills+@kills, headshots=headshots+@headshots, " +
                    "noscopes=noscopes+@noscopes, wallbangs=wallbangs+@wallbangs, blind_kills=blind_kills+@blind_kills, " +
                    "smoke_kills=smoke_kills+@smoke_kills, dominations=dominations+@dominations, revenges=revenges+@revenges, updated_at=NOW()",
                    new MySqlParameter("@attackerid64", attackerId64.ToString()),
                    new MySqlParameter("@victimid64", victimId64.ToString()),
                    new MySqlParameter("@attacker_side", attackerSide),
                    new MySqlParameter("@victim_side", victimSide),
                    new MySqlParameter("@weapon", weapon),
                    new MySqlParameter("@season", season),
                    new MySqlParameter("@kills", counter.Kills),
                    new MySqlParameter("@headshots", counter.Headshots),
                    new MySqlParameter("@noscopes", counter.Noscopes),
                    new MySqlParameter("@wallbangs", counter.Wallbangs),
                    new MySqlParameter("@blind_kills", counter.BlindKills),
                    new MySqlParameter("@smoke_kills", counter.SmokeKills),
                    new MySqlParameter("@dominations", counter.Dominations),
                    new MySqlParameter("@revenges", counter.Revenges)
                );

                if (affected == 0) {
                    dbDown = true;
                    unwrittenDuels.Add(kv);
                }
            }

            foreach (var kv in clutchBatch) {
                if (dbDown) {
                    unwrittenClutches.Add(kv);
                    continue;
                }

                var (steamId64, side, season, opponents) = kv.Key;
                var counter = kv.Value;

                int affected = database.insert(
                    $"INTO {ClutchStatTable} (steamid64, side, season, opponents, attempts, wins, first_seen, updated_at) " +
                    "VALUES (@steamid64, @side, @season, @opponents, @attempts, @wins, NOW(), NOW()) " +
                    "ON DUPLICATE KEY UPDATE attempts=attempts+@attempts, wins=wins+@wins, updated_at=NOW()",
                    new MySqlParameter("@steamid64", steamId64.ToString()),
                    new MySqlParameter("@side", side),
                    new MySqlParameter("@season", season),
                    new MySqlParameter("@opponents", opponents),
                    new MySqlParameter("@attempts", counter.Attempts),
                    new MySqlParameter("@wins", counter.Wins)
                );

                if (affected == 0) {
                    dbDown = true;
                    unwrittenClutches.Add(kv);
                }
            }

            foreach (var kv in multikillBatch) {
                if (dbDown) {
                    unwrittenMultikills.Add(kv);
                    continue;
                }

                var (steamId64, side, season, kills) = kv.Key;

                int affected = database.insert(
                    $"INTO {MultikillStatTable} (steamid64, side, season, kills, rounds, first_seen, updated_at) " +
                    "VALUES (@steamid64, @side, @season, @kills, @rounds, NOW(), NOW()) " +
                    "ON DUPLICATE KEY UPDATE rounds=rounds+@rounds, updated_at=NOW()",
                    new MySqlParameter("@steamid64", steamId64.ToString()),
                    new MySqlParameter("@side", side),
                    new MySqlParameter("@season", season),
                    new MySqlParameter("@kills", kills),
                    new MySqlParameter("@rounds", kv.Value)
                );

                if (affected == 0) {
                    dbDown = true;
                    unwrittenMultikills.Add(kv);
                }
            }

            foreach (var kv in dailyBatch) {
                if (dbDown) {
                    unwrittenDaily.Add(kv);
                    continue;
                }

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

                int affected = database.insert(
                    $"INTO {DailyStatTable} ({string.Join(", ", insertCols)}, updated_at) " +
                    $"VALUES ({string.Join(", ", insertVals)}, NOW()) " +
                    $"ON DUPLICATE KEY UPDATE {string.Join(", ", updateClauses)}, updated_at=NOW()",
                    dailyParams.ToArray()
                );

                if (affected == 0) {
                    dbDown = true;
                    unwrittenDaily.Add(kv);
                }
            }

            foreach (var kv in duelTotalBatch) {
                if (dbDown) {
                    unwrittenDuelTotals.Add(kv);
                    continue;
                }

                var (steamId64, season) = kv.Key;
                var counter = kv.Value;

                int affected = database.insert(
                    $"INTO {DuelTotalTable} (steamid64, season, kills, deaths, headshots, assists, updated_at) " +
                    "VALUES (@steamid64, @season, @kills, @deaths, @headshots, @assists, NOW()) " +
                    "ON DUPLICATE KEY UPDATE kills=kills+@kills, deaths=deaths+@deaths, " +
                    "headshots=headshots+@headshots, assists=assists+@assists, updated_at=NOW()",
                    new MySqlParameter("@steamid64", steamId64.ToString()),
                    new MySqlParameter("@season", season),
                    new MySqlParameter("@kills", counter.Kills),
                    new MySqlParameter("@deaths", counter.Deaths),
                    new MySqlParameter("@headshots", counter.Headshots),
                    new MySqlParameter("@assists", counter.Assists)
                );

                if (affected == 0) {
                    dbDown = true;
                    unwrittenDuelTotals.Add(kv);
                }
            }

            foreach (var kv in serverStatBatch) {
                if (dbDown) {
                    unwrittenServerStats.Add(kv);
                    continue;
                }

                string season = kv.Key;
                var counter = kv.Value;

                int affected = database.insert(
                    $"INTO {ServerStatSeasonTable} (season, hits, damage, headshots, shots, rounds, updated_at) " +
                    "VALUES (@season, @hits, @damage, @headshots, @shots, @rounds, NOW()) " +
                    "ON DUPLICATE KEY UPDATE hits=hits+@hits, damage=damage+@damage, headshots=headshots+@headshots, " +
                    "shots=shots+@shots, rounds=rounds+@rounds, updated_at=NOW()",
                    new MySqlParameter("@season", season),
                    new MySqlParameter("@hits", counter.Hits),
                    new MySqlParameter("@damage", counter.Damage),
                    new MySqlParameter("@headshots", counter.Headshots),
                    new MySqlParameter("@shots", counter.Shots),
                    new MySqlParameter("@rounds", counter.Rounds)
                );

                if (affected == 0) {
                    dbDown = true;
                    unwrittenServerStats.Add(kv);
                }
            }

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
                        existing.Kills += kv.Value.Kills;
                        existing.Deaths += kv.Value.Deaths;
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

                int unwritten = unwrittenHits.Count + unwrittenShots.Count + unwrittenRounds.Count
                    + unwrittenDuels.Count + unwrittenClutches.Count + unwrittenMultikills.Count
                    + unwrittenDaily.Count + unwrittenDuelTotals.Count + unwrittenServerStats.Count;
                if (unwritten > 0) {
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}] database unavailable ({source}): kept {unwritten} stat rows cached for retry.");
                } else {
                    Console.WriteLine(
                        $"[DEBUG] OSBase[{ModuleName}] flushed pending stat writes ({source}): " +
                        $"hitRows={hitBatch.Count}, shotRows={shotBatch.Count}, roundRows={roundBatch.Count}, " +
                        $"duelRows={duelBatch.Count}, clutchRows={clutchBatch.Count}, multikillRows={multikillBatch.Count}, " +
                        $"dailyRows={dailyBatch.Count}, duelTotalRows={duelTotalBatch.Count}, serverStatRows={serverStatBatch.Count}"
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

    // Best-effort mirror of OSWeb's ServerKillTracker::normaliseWeapon (not a call to it --
    // that's PHP, out of reach from here) so these counters join cleanly against the site's
    // own player_kill_stat. Exact knife/taser spelling variants aren't confirmed against that
    // source; flag any mismatch found in practice rather than assume this list is complete.
    private static string NormalizeWeapon(string? weapon) {
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

                    AddDuel(
                        e.Attacker.SteamID,
                        e.Userid.SteamID,
                        MapSide(e.Attacker),
                        MapSide(e.Userid),
                        NormalizeWeapon(e.Weapon),
                        season,
                        e.Headshot,
                        e.Noscope,
                        e.Penetrated > 0,
                        e.Attackerblind,
                        e.Thrusmoke,
                        e.Dominated,
                        e.Revenge
                    );

                    // Ask 16a/24 roll-up: same scope as player_duel_stat above (team kills
                    // included, not filtered) -- it's the same numbers already in hand here.
                    AddDuelTotal(e.Attacker.SteamID, season, kills: 1, deaths: 0, headshots: e.Headshot ? 1 : 0);
                    AddDuelTotal(e.Userid.SteamID, season, kills: 0, deaths: 1);

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
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception in OnBombDefused: {ex}");
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

        // Decided here, not at round end -- see the field comment on statsGateOpen. Held for
        // the whole round regardless of who connects/disconnects while it's live.
        bool warmup = IsWarmupActive();
        int humans = CountConnectedHumans();
        statsGateOpen = !warmup && humans >= minPlayers;
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: round gate {(statsGateOpen ? "open" : "closed")} (humans={humans} min={minPlayers} warmup={warmup})");

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd e) {
        if (!isActive) {
            return HookResult.Continue;
        }

        string season = CurrentSeason();
        string map = CurrentMap();
        int winnerSide = MapWinnerSide(e.Winner);

        foreach (var p in Utilities.GetPlayers()) {
            if (p == null || !p.IsValid || p.IsHLTV || !p.UserId.HasValue) {
                continue;
            }

            ScheduleDamageReport(p.UserId.Value);

            // For ADR: one round played, for whichever side they were actually on.
            // Spectators/unassigned (MapSide -> unknown) still get a row -- excluding them
            // would silently drop anyone who spent the round on a team that then swapped, and
            // "unknown" is a legitimate, filterable bucket, not an error state. Gated by
            // statsGateOpen, decided at this round's start, not re-checked here.
            if (statsGateOpen && IsRealHuman(p)) {
                int side = MapSide(p);
                AddRoundPlayed(p.SteamID, side, season, map, won: side == winnerSide);

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

        FlushPendingStats("RoundEnd");

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