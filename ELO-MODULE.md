# The Elo module — design notes

Written 2026-07-20, decided in the same conversation that produced this file.
Companion to `STATS-MODULE.md` (which is the OSWeb-side counters/body-diagram
plan) — this one is OSBase-side: a ladder à la old HLStats, but Elo instead of
HLStats' proprietary formula, so killing the #1 player is worth more than
killing #100.

## What it is, and what it deliberately is not

**Reversed 2026-07-21 (second time this doc has said this, see below): Elo
now scores ALL play, not only tournament matches.** The reasoning that
scoped it to tournaments in the first place (pub play has bots, uneven
teams, mid-round joins — a rating needs matched competition) was correct
*and stays correct* — what changed is a fact neither side had when that
call was made: this community runs roughly **one tournament a year**. At
that cadence a tournament-scoped rating doesn't rank anyone — it's an
annual event result that then sits frozen for twelve months while people
play several nights a week. Ask 11's gates (no bots, no warmup,
`min_players`) now do the job the tournament window used to do, and the
remaining risk (uneven teams, mid-round joins) is accepted deliberately,
weighed against HLstatsX having run exactly this, on exactly these servers,
for years.

- **Two parts, stored and reasoned about separately — never as one number.**
  Same rule as `staked`/`returned` in `STATS-MODULE.md` ask 12: a sum can
  never be split back apart. A rating column with points already folded in
  would make "how good is this player, independent of how much they've
  played" permanently unanswerable — which is exactly the question this
  whole change exists to answer.
  - **Rating** (`elo_rating`, no season, never reset) — a continuously
    updated skill estimate, Elo-style, in the spirit of a Premier rank.
    "How good is this player" doesn't stop being true in January.
  - **Points** (`elo_points`, keyed by `steamid64`+`season`, reset every
    quarter) — classic HLstatsX-style opponent-ratio-scaled points for
    kills, assists, and round wins. The competitive ladder people actually
    remember. Earned only by *doing* things — someone sitting in spectator
    earns zero, unlike `LevelsRanks`' playtime-based points, which is
    exactly what this replaces it for measuring presence as much as skill.
- **Not per weapon.** Per-weapon breakdown already exists on the site:
  `player_kill_stat` (site migration 0156), populated by `ServerKillTracker`
  reading the log stream. Don't rebuild it. Weighting rating by weapon
  matchup was considered and rejected — it bakes in an unreviewable skill
  claim ("was that knife kill really worth more, or did the victim just
  stand still?"). Splitting rating per weapon was also rejected — most
  weapons don't get enough duels per player to converge; a leaderboard built
  on noise looks as authoritative as one that isn't.
- **`tournament_match` is now a tag, not a gate.** It still gets polled
  (below) and `elo_kill_event.match_id` still records the real
  `tournament_match.id` whenever a window happens to be open — purely so a
  Tuesday night and this year's one tournament stay distinguishable in the
  history. Nothing about scoring depends on it anymore.
- **Retired `GameStats.calcSkill()`/`skill_log` as `TeamBalancer`'s skill
  signal — feeds it Elo rating instead now** (see `src/helpers/
  SkillResolver.cs`). `GameStats` itself is not retired; it's still the
  team/round-tracking substrate `TeamBalancer` runs on (`getTeam`,
  `movePlayer`, `roundNumber`, `immune`) — only the skill *number* changed
  source. See "TeamBalancer" below.

## Ask 11's gates: what replaced the tournament window

Every duel, assist, and round-win now runs through the same gate `ask 11`
(see `STATS-MODULE.md`) already established for `DamageReport`/`TeamBets`:
no bots, never during warmup (hard rule, not configurable), never under
`min_players` connected humans (config, default 4 — nobody knows the right
number until real rating data exists). Decided once per round in a new
`EventRoundStart` handler and held for the whole round, same reason as
everywhere else this gate exists: re-checking at round end would silently
exclude a round that started at full strength just because people logged
off late in the evening.

`tournament_match` polling (`RefreshMatchWindow`, `IsThisServer`,
`css_elo_match_start`/`stop`) is unchanged from the confirmed contract below
— it just no longer decides whether a kill counts, only whether
`elo_kill_event.match_id` gets a real id or stays `NULL`.

**Confirmed `tournament_match` contract** (OSWeb migration 0158, documented
on the OSWeb side in `docs/osbase-elo-contract.md`):

- Table `tournament_match`, primary key **`id`** (not `match_id` — corrected
  from this doc's first draft).
- `server_address` — free text an admin typed: DNS name or IP, with or
  without a port. **Not directly comparable as a string.** OSWeb resolves
  this with `ServerCredentials::canonicalHost` + `HostResolver`; OSBase has
  no access to that code, so the module does its own best-effort
  canonicalization instead (`IsThisServer()`: parses host/port, tries an
  exact match, then falls back to DNS-resolving both sides to IP addresses
  and comparing those). A resolution failure is treated as "not a match" —
  wrong is a missed match, not a stolen one.
- `starts_at` / `ends_at` — **`INT`, unix seconds, nullable.**
- `server_name` exists too but is a display label, not an address — never
  matched on.
- `verified` means "an admin confirmed the result afterwards", **not** "the
  match is currently live" — irrelevant to this module, not used.
- Index on `(server_address, starts_at, ends_at)` exists site-side for this
  exact lookup shape.

1. `RefreshMatchWindow()` polls every 30s for `tournament_match` rows where
   `UNIX_TIMESTAMP() BETWEEN starts_at AND ends_at`, then calls
   `IsThisServer()` per candidate row to find one for this server. Same
   polling shape as `EventWeekend` reading `weapon_event_rules`.
2. `css_elo_match_start <match_id>` / `css_elo_match_stop [match_id]`
   (admin, `@css/generic`) **set** `starts_at`/`ends_at` on the site's row
   via `UNIX_TIMESTAMP()` — they are not their own source of truth, and both
   verify `IsThisServer()` on the target row before writing, so a wrong id
   can't open or close someone else's match.

## Why live computation, but buffered persistence

Two different constraints, easy to conflate:

- **The rating itself must be computed live, synchronously, kill by kill.**
  Elo is order-dependent — two duels between the same pair in one round must
  apply in the order they happened, or the rating is silently wrong. That
  rules out the "queue deltas, apply in any order" pattern `EventWeekend`
  uses for its point tally (which works there only because addition
  commutes). So the module keeps an in-memory `steamid64 -> rating` cache for
  players active in the current match, seeded once per player via a
  synchronous `SELECT` (cheap, once per player per match — not once per
  kill), then updated in memory on every kill.
- **The DB writes from that computation are buffered and sent between
  rounds**, not fired live — the user's call, to keep the write path off the
  server during active rounds. This reuses `EventWeekend`'s exact pattern:
  accumulate in memory during the round, flush on `EventRoundEnd` via a
  background task, merge unwritten rows back on failure and retry next
  flush. Rating math and write timing are independent: the in-memory cache
  is already correct the instant a kill happens; the flush just decides when
  that becomes durable.
- **Points don't share the order-dependency constraint** — they're a
  straightforward additive accumulator (`elo_points`, buffered the same way
  as everything else in this system), computed at the same moment as rating
  only because the opponent-ratio scaling needs that kill's live rating
  values to read from.

## Storage

Three tables, all OSBase-owned (created by this module, `CREATE TABLE IF NOT
EXISTS`, same as every other module):

- **`elo_rating`** — part one, current state, one row per player, **no
  season** (deliberate — this value never resets). `steamid64` PK, `name`,
  `rating`, `matches` (duel count, drives the provisional K-factor),
  `updated_at`. This is what `css_elo_top` and `TeamBalancer` read.
  Cross-module reads go through `EloRating.TryGetRating(steamId64, out
  rating, out matches)` (public, backed by the same `liveRating` cache the
  scoring path uses — always instantly correct, never a DB round trip,
  since another module reading it can't wait on this module's own flush).
- **`elo_points`** — part two, `(steamid64, season)` PK, `name`, `points`,
  `updated_at`. Reset every quarter by simply starting a new `season`
  string — no archiving step exists to skip, unlike cs2rank's
  `lvl_base_YYYYMMDD` table-rename approach, so a reset can never turn into
  an accidental deletion. Read by `css_elo_points_top` (a live `SELECT`
  against this table for one player's chat output — not a materialized
  view or a second table, nothing new for the GDPR list) and, cross-module,
  via `EloRating.TryGetPoints(steamId64, season, out points)` (same
  always-live pattern as `TryGetRating`, backed by a parallel `livePoints`
  cache — added for `player_daily_stat`'s rating/points snapshot, see ask
  22 in `STATS-MODULE.md`).
- **`elo_kill_event`** — durable, ordered, append-only log of every duel:
  `match_id` (**nullable** — `NULL` for ordinary play, a real
  `tournament_match.id` only when a window happened to be open; see "what
  it is" above), `stamp`, `mapname`, attacker/victim identity, **rating
  before** and the rating **delta applied** for both sides (kept separate —
  they're not just +x/-x of each other), **`attacker_points_delta`** (same
  precedent extended to points: save what was awarded and what it was built
  on), `weapon`, `headshot`. Kept so the whole ladder can be rebuilt from
  scratch when the formula changes. Assist and round-win points are *not*
  logged here — they aren't kills, there's no attacker/victim pair to hang
  them on — they go straight into `elo_points` as counters.
- All three carry `steamid64` (or `attackerid64`/`victimid64`) and therefore
  personal data — reachable by the GDPR erasure path like everything else
  (see GDPR erasure, below).
- Never write to `player_kill_stat` — that counter is owned end-to-end by
  OSWeb's `ServerKillTracker` (fed off the log stream). A second writer on
  the same counters double-counts silently, and a doubled kill count looks
  exactly like a good night, not like a bug — nothing would flag it. Same
  guardrail applies to `player_hit_stat` (see `STATS-MODULE.md`), which this
  module doesn't touch either — that's `DamageReport`'s table.

## Algorithm (v1)

**Rating** — standard Elo, chess-style: each side has **its own** K, so the
two rating changes are not forced to be equal and opposite — a provisional
attacker gaining fast while killing an established victim who barely moves
is correct, not a bug.

```
expected_attacker = 1 / (1 + 10^((rating_victim - rating_attacker) / 400))
expected_victim   = 1 - expected_attacker

delta_attacker = round(K_attacker * (1 - expected_attacker))   // positive
delta_victim   = round(K_victim   * (0 - expected_victim))     // negative

rating_attacker += delta_attacker
rating_victim   += delta_victim

// headshot bonus: proportional to the delta already earned, so beating a
// strong opponent with a headshot is still worth more than headshotting a
// weak one -- an opponent-blind flat bonus would break Elo's self-
// calibration.
if headshot and delta_attacker > 0:
    delta_attacker += round(delta_attacker * headshot_bonus_pct)

// assist: small, flat, opponent-blind -- an assist contributed to the
// duel, it didn't win it, and shouldn't move a rating the way a kill does.
if assister exists and assister != attacker/victim:
    rating_assister += assist_reward
```

- `K_x` = `k_factor_provisional` (default 50) while `matches < provisional_matches`
  (default 30) for that player, else `k_factor` (default 32) — new players
  converge faster, established ratings move less per duel. Both configurable.
- `start_rating` default 1000, `headshot_bonus_pct` default 0.20,
  `assist_reward` default 5 — all configurable, all calibration guesses;
  nobody has real rating data yet to tune against.
- Excluded from scoring: team kills, world/self damage, bots. Warmup is a
  hard rule now (ask 11), not a config toggle.

**Points** — classic HLstatsX-style, opponent-ratio-scaled, only ever
additive (points are earned by doing things, never lost by dying):

```
ratio       = clamp(rating_victim / rating_attacker, points_ratio_min, points_ratio_max)
kill_points = round(points_per_kill * ratio)          // beating a stronger opponent scales up

points_attacker += kill_points
if assister exists:
    points_assister += round(kill_points * points_assist_fraction)

// per round, for every real human on the winning side:
points_winner += points_per_round_win
```

- `points_per_kill` default 10, `points_ratio_min`/`points_ratio_max`
  default 0.5/2.0, `points_assist_fraction` default 0.3,
  `points_per_round_win` default 2 — all configurable, all calibration
  guesses, same reason as the rating constants above.
- Same exclusions as rating (no bots, no team kills feeding kill-points,
  ask 11's gates apply to the whole round before any points are awarded).

## GDPR erasure — decided, not assumed

`elo_rating` and `elo_kill_event` are keyed on `steamid64` and are therefore
personal data. OSWeb's `deleteAccount` flow already captures `steamid64`
before erasing an account, so it has what it needs to reach these tables —
the open question was only *how* it reaches them:

- **(a)** OSBase exposes some delete mechanism OSWeb calls, or
- **(b)** OSWeb deletes directly in these tables.

**Chosen: (b).** Every other piece of this cross-repo relationship already
works by both sides reading/writing the same shared MySQL tables directly —
`weapon_event_rules`/`kill`/`score`, `tournament_match`, `player_kill_stat` —
there is no RPC/API layer between OSBase and OSWeb anywhere in this system,
so adding one just for erasure would be new infrastructure to solve a
problem the existing pattern already solves. Flagging this choice explicitly
rather than leaving it implicit — say so if (a) is actually wanted instead.

**Reference — exact columns, checked against the actual `CREATE TABLE`
statements in this repo (not re-guessed), because OSWeb's erasure code had
assumed `BIGINT` and a uniform `steamid64` column on all four, and neither
is right:**

| Table                 | Personal-data column(s)                          | Type          |
|------------------------|--------------------------------------------------|---------------|
| `elo_rating`           | `steamid64`                                       | `VARCHAR(32)` |
| `elo_points`           | `steamid64`                                       | `VARCHAR(32)` |
| `elo_kill_event`       | `attackerid64`, `victimid64` (**two** columns, no plain `steamid64`) | `VARCHAR(32)` each |
| `player_hit_stat`      | `steamid64`                                       | `VARCHAR(32)` |
| `player_weapon_shots`  | `steamid64`                                       | `VARCHAR(32)` |
| `player_round_stat`    | `steamid64`                                       | `VARCHAR(32)` |
| `player_duel_stat`     | `attackerid64`, `victimid64` (**two** columns, no plain `steamid64`) | `VARCHAR(32)` each |
| `player_clutch_stat`   | `steamid64`                                       | `VARCHAR(32)` |
| `player_multikill_stat`| `steamid64`                                       | `VARCHAR(32)` |
| `player_teambet_stat`  | `steamid64`                                       | `VARCHAR(32)` |
| `player_daily_stat`    | `steamid64`                                       | `VARCHAR(32)` |
| `player_duel_total`    | `steamid64`                                       | `VARCHAR(32)` |

**Not personal data, not on this list on purpose:** `server_stat_season` —
keyed only by `season`, a server-wide aggregate across every player, no
steamid64 or any other identifying column. Nothing to erase there for any
individual account.

**Also confirmed, not just assumed:** `css_elo_top`/`css_elo_points_top`
need no entry of their own. Both are chat commands that run a live `SELECT
... FROM elo_rating`/`elo_points ... LIMIT @limit` and print the result to
one player — not a materialized table or view, nothing persisted beyond
what's already in `elo_rating`/`elo_points`, both already on this list.
Worth checking explicitly rather than assuming, since a materialized
leaderboard is exactly the kind of table that gets forgotten here —
`player_weapon_shots` already leaked past an erasure once for that same
reason before someone remembered it existed.

`VARCHAR(32)` everywhere, deliberately — same reason as
`weapon_event_kill`/`score` in `project-weapon-event-contract`: a Steam64 ID
overflows JS's safe integer range, so it's carried as a string everywhere it
might cross into JS, not `BIGINT`. Consistent across every OSBase-owned
table in this system; there was no reason to break that here.

OSWeb's `deleteAccount` should run:

```sql
DELETE FROM elo_rating           WHERE steamid64 = ?;
DELETE FROM elo_points           WHERE steamid64 = ?;
DELETE FROM elo_kill_event       WHERE attackerid64 = ? OR victimid64 = ?;
DELETE FROM player_hit_stat      WHERE steamid64 = ?;
DELETE FROM player_weapon_shots  WHERE steamid64 = ?;
DELETE FROM player_round_stat    WHERE steamid64 = ?;
DELETE FROM player_duel_stat     WHERE attackerid64 = ? OR victimid64 = ?;
DELETE FROM player_clutch_stat   WHERE steamid64 = ?;
DELETE FROM player_multikill_stat WHERE steamid64 = ?;
DELETE FROM player_teambet_stat  WHERE steamid64 = ?;
DELETE FROM player_daily_stat    WHERE steamid64 = ?;
DELETE FROM player_duel_total    WHERE steamid64 = ?;
```

with `?` bound as the string form of the Steam64 ID (not a native int/BIGINT
parameter) in all twelve. `player_round_stat` and `player_duel_stat` were
added 2026-07-21 alongside the `side`/`season` dimensions on
`player_hit_stat`/`player_weapon_shots`; `player_clutch_stat` and
`player_multikill_stat` followed the same day (see `STATS-MODULE.md`, "asks
5-10"); `player_teambet_stat` (owned by `TeamBets.cs`, not `DamageReport.cs`)
followed a moment later ("ask 12"); `player_daily_stat` and
`player_duel_total` followed after that ("asks 15-16", `DamageReport.cs`);
`elo_points` followed the two-part rating/points split ("asks 4, 19, 20" —
see "what it is" above). `player_duel_stat` also gained a `season` column
that day (still no plain `steamid64`, still the two
`attackerid64`/`victimid64` columns above — erasure predicate unchanged,
just note the table now also carries `season` in its primary key). Same
owner-per-table rule as the rest, same erasure requirement, easy to miss if
this list isn't kept as the single place both sides check.

**Also added the same round ("asks 13-17"):** `first_seen DATETIME` on the
seven `DamageReport`/`TeamBets` counter tables (`player_hit_stat`,
`player_weapon_shots`, `player_round_stat`, `player_duel_stat`,
`player_clutch_stat`, `player_multikill_stat`, `player_teambet_stat`) — set
on `INSERT` only, never touched by `ON DUPLICATE KEY UPDATE`. **Not** on
`elo_rating`/`elo_points`/`elo_kill_event` (out of that ask's scope
entirely) or on `player_daily_stat`/`player_duel_total` (postdate ask 13;
`player_daily_stat`'s own `day` column already pins it to an exact period
anyway). None of this changes any erasure predicate either way — still a
plain `steamid64` (or `attackerid64`/`victimid64`) match throughout.
`player_round_stat` also gained `rounds_won` and `map` (now part of its
primary key: `steamid64, side, season, map`) the same round — same non-effect
on its erasure predicate.

## TeamBalancer (2026-07-21, cutover phased 2026-07-21)

`TeamBalancer.cs` built its skill signal on `GameStats.calcSkill()`/`skill_log`
via `src/helpers/SkillResolver.cs`. Retiring that calc removes the input,
not just a data source — `SkillResolver.cs` gained a second, parallel code
path rather than being repointed wholesale.

**Two questions that looked like one, and aren't.** "Does `skill_log` keep
writing once Elo is on" and "does the balancer switch to reading Elo" are
independent — conflating them was the near-miss here. `GameStats.SaveIfEligible()`
(`GameStats.cs:157-207`) is the actual write path, triggered by its own
`OnRoundEnd`/`OnMapEnd` hooks (`GameStats.cs:304,137-140`) — it has never
been called by, or dependent on, anything reading `calcSkill()`/
`GetCached90dByUserId()` externally; those are pure in-memory arithmetic
with no write side effect. **Verified by reading the write path directly,
not assumed from the fact that nothing obviously touches it.** `EloRating`'s
`DefaultEnabled="0"` only gates the `EloRating` module through the normal
module-loading system; `GameStats` is constructed unconditionally in
`OSBase.cs`, entirely outside that system. So: `skill_log` keeps writing,
completely unaffected, for as long as `GameStats` itself stays loaded —
independent of what the balancer reads.

**What *is* switchable, deliberately not switched on day one:
`balancer_skill_source`** (`teambalancer.cfg`) — `gamestats` (default at
release) | `elo` | `shadow`.

- **`gamestats`** — unchanged behaviour. `SkillResolver`'s original
  GameStats blend (below) drives every swap decision, exactly as before
  this whole Elo effort.
- **`elo`** — `EloRating.rating` drives balancing instead. Not the default
  — flipping this the moment the Elo write path ships would feed the
  balancer cold ratings on day one: everyone under `min_rated_matches`
  resolves to the roster median, which is close to the same as having no
  skill signal at all. The plan is to let a season of ratings accumulate
  first.
- **`shadow`** — balances on `gamestats` (identical decisions to that mode)
  but *also* computes what `elo` would have shown for the same two teams
  and logs both side by side, plus how many of the roster have cleared
  `min_rated_matches` (`TeamBalancer.LogShadowSkillComparison`, called once
  per balance pass from `WarmupFinalBalance`/`BalanceAtRoundEnd`). Turns
  "are the ratings ready to switch on" into something read from logs
  instead of a guessed calendar date discovered live, in front of players,
  on the server. Cheap — reuses the team averages the real `gamestats`
  decision already computed, and Elo's side is one more average over a
  roster of a handful of players, not a second swap search.

**The overlap window is the form curve's only control period.** As long as
both `GameStats` and `EloRating` are writing, `skill_log` and
`player_daily_stat.rating` (ask 22, `STATS-MODULE.md`) describe the same
players on the same days, which is what lets anyone check the new source
against the old one before trusting it alone. Once `GameStats` is actually
turned off, that comparison window is gone for good — those days don't come
back, same non-retroactive rule as everything else in this document.

- **`GameStats` itself is not retired regardless of `balancer_skill_source`.**
  `TeamBalancer` still runs entirely on it for team/round tracking
  (`getTeam`, `movePlayer`, `roundNumber`, `immune`) — only the skill
  *value*, in `elo`/`shadow` modes, reads from a second source alongside it.
  `gameStats` field stays on `TeamBalancer`; a new `eloRating` field
  (`osbase?.GetModule<EloRating>()`) sits beside it.
- **The old baseline/live blend is still there, not deleted — `SkillResolver.cs`
  now carries both paths side by side**, selected by `balancer_skill_source`,
  not one replacing the other. GameStats recomputed
  a live in-round skill signal and blended it against a 90-day baseline,
  weighted by round number — that machinery existed because GameStats'
  `calcSkill()` genuinely recalculated continuously within a round.
  `EloRating.liveRating` already *is* a single, continuously-updated value
  at every moment; there is no separate "baseline vs live" question left to
  answer for that path, so the Elo side of `SkillResolver` doesn't try to —
  it's a genuinely simpler computation, not a missing feature.
- **Cold start (`elo`/`shadow` modes) = roster median, not a fixed "bad
  player" band.** The Elo path doesn't reuse the old resolver's
  steamid-hash into a fixed `[5000,7000]` provisional range (that logic is
  still there, but only the `gamestats` path uses it now). Matches OSWeb's
  `Services\BalancedDraft` instead: an unranked player is treated as
  exactly average *for this roster* (computed fresh
  each balance pass via `SkillResolver.ComputeRosterMedian`), so they
  neither drag a team down nor lift it — a flat low default would stack
  every newcomer onto the same side.
- **`min_rated_matches` (new, `teambalancer.cfg`, default 10)** — below this
  many rated Elo duels, a player's own rating is too noisy to trust and
  they're treated as the median too, same as a player with zero rating.
  Config, not a constant, same reasoning as ask 11's `min_players`: nobody
  knows the right number until real rating data exists.
- **Swap thresholds are relative only in `elo` mode — literal in
  `gamestats`/`shadow`, on purpose, not an oversight.**
  `WARMUP_TARGET_DEVIATION`, `MID_SWAP_THRESHOLD`, `LATE_SWAP_THRESHOLD`,
  `LATE_HYSTERESIS`, `MIN_PROJECTED_GAIN`, `EMERGENCY_GAP`, and the
  composition penalty in `ScoreState`/`ScoreStateSwapSim` were originally
  hardcoded point values tuned for GameStats' ~4000-11000 skill scale —
  **and stay exactly those literal values whenever `gamestats`/`shadow`
  mode is what's actually deciding**, because they're already correct for
  that scale. Only in `elo` mode would the same literal numbers make swaps
  almost never trigger — silently near-inert, not just miscalibrated —
  since Elo's typical spread is much narrower; only there do the
  thresholds convert to ratios (preserving each constant's *fraction* of
  GameStats' ~7000-point typical spread, not re-guessed against Elo's
  still-unknown distribution) multiplied by `SkillResolver.
  ComputeRosterSpread()`, the actual roster's current Elo rating range.
  A first version of this conversion applied the ratio unconditionally
  regardless of `balancer_skill_source` — caught before release: with
  `gamestats` as the default, that would have fed GameStats-scale gaps
  against Elo-scale thresholds from day one, which is the exact kind of
  mismatch the ratio conversion existed to prevent, just aimed at the
  wrong mode. **The ratios themselves are still a first cut** — revisit
  once real Elo rating spread data exists to check them against.
- **`RefreshRosterStats()` logs the roster's Elo median/spread and the
  *active* thresholds on every balance pass** (median, spread, and every one
  of the seven ratio-or-literal-derived
  values above) — without this, nobody could ever calibrate the ratios
  against real data, and a balancer gone silently near-inert under Elo's
  narrower spread looks identical to a working one right up until someone
  wonders why teams feel off.
- **The same scale bug existed one level in, inside the shadow log itself —
  caught before release, not after.** `LogShadowSkillComparison` prints
  `elo_gap` (always Elo-scale, from `ComputeEloTeamAverage`, which calls the
  Elo path directly and unconditionally) but had nothing on that same scale
  to print alongside it — the only threshold values in scope
  (`WARMUP_TARGET_DEVIATION` etc.) are the *active* ones, which in shadow
  mode correctly resolve to the GameStats literals, since that's what's
  really deciding. Comparing an Elo-scale gap against GameStats-scale
  thresholds is the identical mismatch the whole ratio-vs-literal split
  exists to prevent, just relocated into the one log line whose entire job
  is to be trustworthy without anyone needing to double-check it — a shadow
  log nobody can trust is worse than no shadow log, because it still looks
  like data. Fixed with a second, unconditional set of properties
  (`EloWarmupTargetDeviation`, `EloMidSwapThreshold`, etc. — always
  `ratio * currentRosterSpread`, regardless of `balancer_skill_source`)
  that only `LogShadowSkillComparison` reads, printed as "elo thresholds
  (would-be, not active)" right next to `elo_gap` in the same line.
  **General shape worth carrying through the rest of this cutover:** a mode
  switch creates a third state that neither endpoint was designed for, and
  that third state is exactly where a scale mismatch hides — checked by
  hand-tracing both branches (`gamestats`/`shadow` → literal,
  `elo`/shadow-log → ratio) since there's no live server here to run it
  against; worth an actual shadow-mode run against a real or seeded fake
  roster before deploy, reading the logged numbers, not just confirming the
  build compiles — the thresholds are the one place in this package where a
  bug looks like a normal number.
- `GetEffectiveSkillForPriority` (both overloads, called externally by
  `WeaponRestrict.cs`) kept their exact signatures — the roster
  median/spread refresh happens inside `TeamBalancer` now, so nothing
  downstream needed to change.

## Primary-key audit, done once more before deploy (2026-07-21)

Every `CREATE TABLE` read fresh from the code (not from this doc, not from
memory) and checked against every ask that touches it. Thirteen tables:

| Table | Primary key |
|---|---|
| `elo_rating` | `(steamid64)` — no season, by design (never resets) |
| `elo_points` | `(steamid64, season)` |
| `elo_kill_event` | `(id)` autoincrement — the one deliberate raw-event log, not a counter, so no dimensioned key applies |
| `player_hit_stat` | `(steamid64, weapon, hitgroup, direction, side, season)` |
| `player_weapon_shots` | `(steamid64, weapon, side, season)` |
| `player_round_stat` | `(steamid64, side, season, map)` |
| `player_duel_stat` | `(attackerid64, victimid64, attacker_side, victim_side, weapon, season)` |
| `player_clutch_stat` | `(steamid64, side, season, opponents)` |
| `player_multikill_stat` | `(steamid64, side, season, kills)` |
| `player_teambet_stat` | `(steamid64, season)` |
| `player_daily_stat` | `(steamid64, day)` — no weapon/hitgroup/side, **`side` a sealed decision, not an oversight — see below** |
| `player_duel_total` | `(steamid64, season)` — `headshots`/`assists` added ask 24, key unchanged |
| `server_stat_season` | `(season)` |

All match what asks 1-22 actually specified, including the deliberate
omissions (no `map` on `player_hit_stat`/`player_duel_stat`, no `side` on
`player_teambet_stat`/`player_duel_total` — never asked for, correctly not
added; scope creep here is exactly as permanent a mistake as an omission,
since both are equally impossible to walk back once rows exist). `first_seen`
present on exactly the seven tables ask 13 named, absent everywhere else on
purpose. Rating/points snapshot on `player_daily_stat` confirmed nullable,
not part of the key.

**Correction to this audit, same pass:** `player_daily_stat`'s missing
`side` was first waved through as "never asked for, correctly omitted" —
wrong framing, caught immediately after. Ask 15 (`STATS-MODULE.md`) had
explicitly called it "a now-or-never call" and left it open, not settled.
"Never asked for" and "actively decided against" look identical in a table
like this one, and only the second is actually safe to file away — the
first is an open question wearing the same formatting as a closed one. Now
resolved and recorded in `STATS-MODULE.md`'s ask 15 section: **no `side`
on `player_daily_stat`**, no writes exist yet so nothing was lost by
resolving it now rather than never. `map` (ask 17) was never at the same
risk despite the near-miss earlier in this conversation — it's reversible
in the direction that matters (added later, only prior history is lost);
a missing `side` on an already-summed daily row is not reversible at all.

## Release plan (2026-07-22)

Full plan given by the owner ahead of the actual release. All three
systems (`GameStats`/`skill_log`, cs2rank's `lvl_base`, this module) run
simultaneously through 2026Q3 as a trial season; cs2rank stays visible to
players the whole time; Oct 1 is when cs2rank unloads and this module's
chat commands take over its names.

1. **Ask 24** (`player_duel_total` gains `headshots`/`assists`) — done, see
   `STATS-MODULE.md`.
2. **`!elorank`/`!elotop`** — done, see `STATS-MODULE.md`. Runs under the
   `!elorank`/`!elotop` names (config, `elorating.cfg`) through the trial
   season; renamed to `!rank`/`!top` in that same config file on Oct 1,
   no rebuild.
3. **Deployed config must set `balancer_skill_source shadow`** in the live
   `teambalancer.cfg` — the code default stays `gamestats` deliberately
   (see "TeamBalancer" above), so this has to be an explicit line in
   whatever config actually ships, not an assumption that the default is
   already right. Not yet done — this is a deployment-time step, not a
   code change; flagging it here so it isn't missed at release time.
4. **Run an actual shadow-mode balance pass and read the numbers before
   release, not just confirm compilation.** No live CS2 server exists in
   this dev environment — every check on `TeamBalancer`'s Elo path so far
   has been `dotnet build` plus manual/hand-tracing of both branches, never
   an in-game run. This is the one item in this plan that still needs a
   real server: someone has to start a match with `balancer_skill_source
   shadow` and actually read `LogShadowSkillComparison`'s output, because
   (their framing, worth keeping) a bug in a comparison like this looks
   like a normal number, not an error.
5. **Post-deploy verification checklist** (after the real release, not
   before):
   - Rows landing in all the new/extended tables.
   - **Gates hold** — no rows from warmup, no bots, nothing under
     `min_players`. The one item on this list that can't be fixed after
     the fact if it's wrong, so it gets checked the first evening, not the
     first month. `DamageReport`/`EloRating`/`TeamBets` each now log their
     gate decision every round start (`round gate open/closed
     (humans=N min=M warmup=bool)`, added 2026-07-22 specifically so this
     check is a log grep instead of a DB query) — added proactively for
     this checklist item, not separately requested.
   - `season` reads `2026Q3` everywhere it's written.
   - `first_seen` is set on INSERT only and never moves on later updates.
   - The shadow log prints numbers that look like real gaps/thresholds on
     the right scale, not just numbers (see "TeamBalancer" above, scale
     bug #2 — a shadow log nobody can trust is worse than none, since it
     still looks like data).
6. **Ask 25** (parallel, non-blocking) — map `GameStats`' full dependency
   graph before other modules risk depending on shared match-state beyond
   the skill number. Done, see "GameStats dependency map" below.

## GameStats dependency map (ask 25, 2026-07-22)

Full research pass over `GameStats.cs` and every external call site, done
to answer one question before other modules risk building on it further:
**can `GameStats` ever be fully retired once nothing reads the skill
number, or does it need to keep running regardless?** Answer: **it has to
keep running regardless.** It's two bundled responsibilities wearing one
name, and only one of them is being migrated by this whole project.

**Structural fact, worth recording precisely:** `GameStats` does not
implement `IModule` and is not in `loadedModules` — it's hand-constructed
in `OSBase.cs:42`, unconditionally, entirely outside the module-loading
system `EloRating.DefaultEnabled="0"` belongs to, and reachable only via
`osbase.GetGameStats()` (never `GetModule<GameStats>()`). Confirms and
sharpens what was already noted in the TeamBalancer section above.

**Responsibility A — the skill-scoring engine** (`calcSkill()`,
`GetCached90dByUserId()`/`GetCached90dBySteam()`, `skill_log` via
`SaveIfEligible()`). This is the piece `EloRating`/`SkillResolver` is
migrating. Confirmed: **only `SkillResolver.cs` reads `calcSkill()`/
`GetCached90dByUserId()` from outside `GameStats.cs` itself** — `TeamBalancer`
reaches skill exclusively through `SkillResolver`'s two overloads, never
directly. Several public GameStats methods here are dead code (no call site
anywhere outside `GameStats.cs`): `GetCached90dBySteam`, `TeamAverage90d`,
`GetLiveSkillMomentsActive`, `TryGetLiveSkillBySteam`,
`TeamStats.getPlayerBySkill`/`getPlayerBySkillNonImmune`/
`GetPlayerByDeviation`, `GetTeamBySteam`.

**Responsibility B — shared match-state/team-roster service.** No
migration path exists for this, and it's load-bearing for five modules
independent of the skill number:

| State | Owned by GameStats | Read by |
|---|---|---|
| `IsWarmup` | field, set in `OnMapStart`/`OnWarmupEnd` | `WeaponRestrict`, `TeamBets`, `DamageReport`, `EventWeekend`, `EloRating` — each has its own warmup gate wired straight to this one flag |
| `roundNumber` | field, incremented in `OnRoundStart` | `TeamBalancer` (phase detection, swap cooldowns, halftime math — its heaviest dependency by call-site count), `SkillResolver` |
| Team rosters (`getTeam`/`getTeamPlayers`, `TeamStats.playerList`, `movePlayer`, `SyncTeamsNow`) | `teamList`, rebuilt every round-end + on-demand resync | `TeamBalancer`'s entire swap/size-fix engine is built directly on `TeamStats` objects GameStats hands out; `WeaponRestrict` uses team sizing for weapon-limit rules |
| Per-player raw counters (`GetPlayerStats().kills/deaths/assists/...`, not skill) | `PlayerStats` fields | `WeaponRestrict` (priority-kills lookup), `ServerInfo` (web dashboard upsert) |
| `PlayerStats.immune` | field, reset in `ResetCounters()` | Set/read only by `TeamBalancer`, after a forced swap |

GameStats has no connect/disconnect event handlers at all — its "who's
connected" view is entirely derived on-demand from `Utilities.GetPlayers()`
inside its own resync methods, not pushed via events. So there's no
separate "connected roster" API to re-home beyond the team containers
above.

**Bottom line for future work:** retiring the skill number (this whole
project) does not get `GameStats` any closer to being removable. If full
removal is ever wanted, four things need a new home first: warmup state,
the round counter, the team-roster service (by far the largest surface,
effectively `TeamBalancer`'s whole balancing loop is built on it), and the
raw per-player counters `WeaponRestrict`/`ServerInfo` read. None of that is
in scope for this release — noted here so the next person who reaches for
"can we just delete GameStats now" finds the answer already written down
instead of rediscovering it by breaking `TeamBalancer`.

## Suggested build order

1. ~~This module, engine + tables, disabled by default~~ — done.
2. ~~OSWeb: add `starts_at`/`ends_at` to `tournament_match`~~ — done
   (migration 0158, contract confirmed above).
3. ~~Scope to tournament matches only~~ — reversed 2026-07-21; scores all
   play now, see "what it is" above.
4. **Turn the module on**: set `host`/`port` in `elorating.cfg` (still used
   to tag `elo_kill_event.match_id`, no longer a gate), set `elorating 1` in
   `OSBase.cfg`. `min_players`/`headshot_bonus_pct`/`assist_reward`/points
   constants all have defaults but are calibration guesses — revisit once
   real data exists.
5. **OSWeb: wire `deleteAccount` to the tables above** (see GDPR erasure),
   and rewrite `RankSeasonReset` (OSWeb-side, built around cs2rank's
   table-rename approach) — `elo_points`'s season-in-the-key design needs
   no archiving step at all, so most of what that automation does goes away
   rather than getting ported.
6. **Leaderboards on the site**, reading `elo_rating` (all-time) and
   `elo_points` (this season) — same shape as `EventWeekend`'s
   `weapon_event_score` being read by the site today.
7. ~~`TeamBalancer` stays on GameStats skill~~ — done, see "TeamBalancer"
   above.

## House rules that apply here

Same ones as `STATS-MODULE.md`: OSBase tables are created by the module
itself (`CREATE TABLE IF NOT EXISTS`, no separate migration file — that
mechanism is OSWeb's); nothing is deleted from OSBase's own tables, GDPR
erasure is the only path that removes personal data (see above); never write
to a table another system already owns end-to-end (`player_kill_stat`).
