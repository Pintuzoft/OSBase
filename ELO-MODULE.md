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
- **Not per weapon.** Per-weapon breakdown already exists via OSBase's own
  tables (`player_duel_stat`, `player_hit_stat`) — **corrected 2026-08-04
  (agent-chat #18): the `player_kill_stat`/`ServerKillTracker` this bullet
  originally pointed to never existed.** `player_kill_stat` was migration
  0156's placeholder for a log-stream parser (`ServerKillTracker`) that was
  never built; OSWeb pulled the table itself in migration 0169 once OSBase
  took over the measurement instead, specifically because two kill-sums on
  a profile page had been drifting apart every night. Don't rebuild a
  per-weapon breakdown here regardless — it already exists elsewhere.
  Weighting rating by weapon matchup was considered and rejected — it
  bakes in an unreviewable skill claim ("was that knife kill really worth
  more, or did the victim just stand still?"). Splitting rating per weapon
  was also rejected — most
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

Four tables, all OSBase-owned (created by this module, `CREATE TABLE IF NOT
EXISTS`, same as every other module):

- **`elo_rating`** — part one, current state, one row per player, **no
  season** (deliberate — this value never resets). `steamid64` PK, `name`,
  `rating`, `matches` (duel count, drives the provisional K-factor),
  `updated_at`. This is what `css_elo_top` and `TeamBalancer` read.
  Cross-module reads go through `EloRating.TryGetRating(steamId64, out
  rating, out matches)` (public, backed by the same `liveRating` cache the
  scoring path uses — always instantly correct, never a DB round trip,
  since another module reading it can't wait on this module's own flush).
  **`rating` is `DECIMAL(12,4)`, not `INT`** (fixed 2026-08-04, see
  "Algorithm" below) — the in-memory `liveRating` cache and every duel
  delta are `decimal` too, so nothing along the accumulation path rounds
  before it's stored. `TryGetRating`'s `out int rating` is unchanged: it
  rounds the exact value at the moment of that call, since every existing
  caller (`TeamBalancer`/`SkillResolver`/`!elorank`) wants a whole number
  to show or compare, not the accumulator itself.
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
  22 in `STATS-MODULE.md`). **`points` is `DECIMAL(12,2)`, not `INT`**
  (fixed 2026-08-04, same fix and same day as `elo_rating.rating` above,
  see "Points" in Algorithm below for why this one mattered more, not
  less) — `livePoints` and every kill/assist points award are `decimal`
  too. `TryGetPoints`'s `out int points` is unchanged, rounded at the
  moment of the call, same reasoning as `TryGetRating`.
- **`elo_kill_event`** — durable, ordered, append-only log of every duel:
  `match_id` (**nullable** — `NULL` for ordinary play, a real
  `tournament_match.id` only when a window happened to be open; see "what
  it is" above), `stamp`, `mapname`, attacker/victim identity, **rating
  before** and the rating **delta applied** for both sides (kept separate —
  they're not just +x/-x of each other; both pairs are `DECIMAL(12,4)` as
  of the 2026-08-04 fix, same reasoning as `elo_rating.rating` — the ledger
  should hold the exact value that was actually added, not a rounded
  approximation of it), **`attacker_points_delta`** (same
  precedent extended to points: save what was awarded and what it was built
  on — also `DECIMAL(12,2)` as of the same 2026-08-04 fix, see "Points"
  below), `weapon`, `headshot`. Kept so the whole ladder can be rebuilt from
  scratch when the formula changes. Assist and round-win points are *not*
  logged here — they aren't kills, there's no attacker/victim pair to hang
  them on — they go into `elo_bonus_event` instead (below). **Also,
  2026-08-04:** `victim_active_weapon`/`victim_best_weapon` — `weapon`
  above is the killer's weapon only; these two capture what the *victim*
  had, for a planned weapon-dependent points scheme where the owner's own
  examples ("AWPing pistols", "meeting ARs with a pistol") are about the
  matchup, not the murder weapon alone. `victim_active_weapon` is the raw
  classname of whatever they last equipped (`EventItemEquip`, tracked
  live, not read off pawn state at the moment of death — untested whether
  that would even be reliable then); `victim_best_weapon` is the
  highest-priced weapon they'd bought or picked up so far that round
  (`EventItemPurchase`/`EventItemPickup`, running max, reset every
  `EventRoundStart`), answering "what kind of player was this" rather than
  "what could they do in that exact instant" — deliberately both, since
  which one produces a ladder that *feels* right can't be settled by
  argument, only by looking at real numbers from real nights. Both
  nullable — a victim who dies before their first tracked weapon event has
  a genuine "unknown", not a value worth faking. Prices come from a static
  `WeaponPrices` table (public CS2 economy data, not something to verify
  against a server). OSWeb owns turning these two columns into an actual
  points weight; this module's job stops at making sure the dimension
  exists and gets filled before the first real run — a column that isn't
  there yet can be added, a kill that already happened without one can't.
- **`elo_bonus_event`** — added 2026-08-04, found via agent-chat rather
  than in-house: `elo_kill_event`'s own "rebuild the whole ladder from
  scratch" claim wasn't actually true, because assist and round-win
  awards touched `elo_rating`/`elo_points` directly with no replayable
  row anywhere — a counter, not a ledger entry. This table is that missing
  half. One row per award: `kind`, `steamid64` (who got it), `name`,
  `rating_delta`, `points_delta` (`DECIMAL(12,2)` as of the 2026-08-04
  fix — an assist's points award goes through the same rounding as a kill's
  and needed the same fix), `season`, `mapname`, `match_id` (same
  nullable tag as `elo_kill_event`), `stamp`, and `related_attacker_id64`/
  `related_victim_id64` where relevant, so a row stays traceable to
  whatever it grew out of, same "save what it was built on" reasoning as
  `elo_kill_event` itself. **This table existing at all is why a formula
  change, a season reset, or the demo backfill can now actually reconstruct
  the full ladder** — before it, all three of those would have silently
  reproduced a plausible-looking but too-low number, with the gap invisible
  because the missing amount was never negative, never absent, just never
  there. Four `kind` values so far:
  - `'assist'` — `rating_delta`/`points_delta` positive, both
    `related_attacker_id64`/`related_victim_id64` populated (the duel it
    grew out of).
  - `'round_win'` — `rating_delta` always `0` (round wins never touch
    rating), `points_delta` positive, no related columns (no duel behind a
    round win).
  - **`'teamkill_penalty'`/`'suicide_penalty'`** — added 2026-08-04, direct
    user ask (old CS:Source gave -1 on the scoreboard for these; this is
    the Elo-side equivalent), **corrected same day after agent-chat #18**.
    First built against `rating_delta` — wrong: `elo_rating` feeds LAN
    team-balancing (`TeamBalancer`, `balancer_skill_source elo`), so
    docking rating for a teamkill would make the balancer think the
    player is worse than they actually are and build lopsided teams out
    of a punishment that has nothing to do with skill. Rating measures
    skill; shooting a teammate doesn't make anyone worse at aiming. There's
    also no opponent to weigh a rating penalty against, so it would land
    flat — breaking the same self-calibration property the headshot bonus
    was deliberately built to preserve (proportional to what was already
    earned, never an opponent-blind flat add-on). **Corrected to
    `points_delta`, negative** (`teamkill_points_penalty`/
    `suicide_points_penalty` config, defaults -10/-5, calibration guesses,
    deliberately unequal — a teamkill costs the team a player *and* is
    someone else's fault; a suicide already punishes itself). `rating_delta`
    is always `0` for both. This is a **deliberate, documented exception**
    to "points never go down" below, not a silent violation of it — see
    that section for the note. `teamkill_penalty` populates
    `related_victim_id64` (who was killed) for traceability;
    `related_attacker_id64` stays NULL (it would just repeat `steamid64`).
    `suicide_penalty` populates neither — there's no second person
    involved. Applied from `EloRating.cs`'s `OnPlayerDeath`, which now
    catches team kills and suicides (including the world-damage case —
    fall, drowning — where `eventInfo.Attacker` is null and would
    otherwise be silently dropped) before the normal duel path, instead of
    just returning early as it used to.
- All four carry `steamid64` (or `attackerid64`/`victimid64`) and therefore
  personal data — reachable by the GDPR erasure path like everything else
  (see GDPR erasure, below).
- **`player_kill_stat` no longer exists** (removed 2026-08-04 note, found
  via agent-chat #18: `ServerKillTracker` was never built, and OSWeb
  dropped the table in migration 0169 once OSBase's own counters took over
  the measurement — the guardrail below protected nothing by the time this
  was written, it just hadn't been noticed). The still-relevant guardrail:
  never write to `player_hit_stat` (see `STATS-MODULE.md`) — that's
  `DamageReport`'s table, not this module's, and a second writer on any
  counter double-counts silently; a doubled kill count looks exactly like
  a good night, not like a bug — nothing would flag it.

## Algorithm (v1)

**Rating** — standard Elo, chess-style: each side has **its own** K, so the
two rating changes are not forced to be equal and opposite — a provisional
attacker gaining fast while killing an established victim who barely moves
is correct, not a bug.

```
expected_attacker = 1 / (1 + 10^((rating_victim - rating_attacker) / 400))
expected_victim   = 1 - expected_attacker

delta_attacker = round4(K_attacker * (1 - expected_attacker))   // positive
delta_victim   = round4(K_victim   * (0 - expected_victim))     // negative

// headshot bonus: proportional to the delta already earned, so beating a
// strong opponent with a headshot is still worth more than headshotting a
// weak one -- an opponent-blind flat bonus would break Elo's self-
// calibration. Applied BEFORE rating_attacker is updated below -- this
// ordering is load-bearing, not stylistic: get it backwards (as an
// earlier draft of this pseudocode once did) and the bonus only changes
// the value that gets logged, never the player's actual rating, so the
// live number and the ledger entry for the same duel would silently say
// two different things. The real code (EloRating.cs) has always applied
// it in this order; only this document briefly had it wrong.
if headshot and delta_attacker > 0:
    delta_attacker += round4(delta_attacker * headshot_bonus_pct)

rating_attacker += delta_attacker
rating_victim   += delta_victim
// rating_attacker/rating_victim are stored exact (DECIMAL(12,4)), never
// rounded on the way in -- round4() above only trims binary-float noise
// from the double-precision expected-score math down to 4 decimal places,
// it does not collapse a delta toward 0 the way round-to-int did. Rounding
// to a whole number happens exactly once, at the moment a rating is about
// to be shown to a human (!elorank, !elotop, css_elo_top) -- see "Fixed
// 2026-08-04" below.

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

**Two more findings from the same 2026-08-04 review that caught the
pseudocode-ordering bug above.** The first was a real bug, fixed same day;
the second is a real, deliberate design property, documented but not
"fixed" (there's nothing broken to fix, only a consequence to watch for):

- **Fixed same day: `Math.Round` was flooring `attackerDelta`/
  `victimDelta` to exactly `0`.** A dominant-enough kill against a
  weak-enough opponent rounded a small fractional Elo gain down to
  nothing — correct continuous Elo, but the `int` storage type collapsed a
  whole band of legitimately-small deltas to indistinguishable-from-no-
  change, every time it happened, permanently (a rounded-away delta leaves
  no trace of itself). The initial write-up here treated this as an open
  question because rating is conventionally *displayed* to players as a
  whole number, unlike points — but that's a distinction about display,
  not storage, and the two don't have to share a type. **Resolved by
  storing exact, rounding only at display**: `elo_rating.rating` and
  `elo_kill_event.attacker_rating_before`/`attacker_delta`/
  `victim_rating_before`/`victim_delta` are now `DECIMAL(12,4)`, not `INT`
  — same type change already planned for `elo_points` in the HLstatsX
  backlog, applied here first because this one was a confirmed live bug,
  not a proposal. `TryGetRating` (the only cross-module read of rating,
  used by `TeamBalancer`/`SkillResolver`/`!elorank`) still returns an
  `int`, rounded from the exact stored value at the moment of the call —
  every existing caller is unaffected, since a one-shot display read
  rounding once is exactly the "round only at the point something is
  printed" rule working as intended, not a regression of the fix.
- **The rating pool is deliberately not zero-sum, in two independent
  ways**: `K_attacker`/`K_victim` can differ (provisional vs. established,
  already documented above as intentional — "not forced to be equal and
  opposite"), and the headshot bonus only ever adds to the attacker's
  side, never subtracts from the victim's. Both reasonable in isolation;
  together they mean the sum of every player's rating drifts upward over
  time, not just individual ratings moving around a fixed total. **Doesn't
  matter for any ranking** (`!elorank`, `!elotop`, `css_elo_top` all
  compare players against each other, and drift affects everyone) — **does
  matter for `TeamBalancer`'s `elo` mode**, which reads absolute rating
  values to build teams: a player who's been away for months hasn't
  participated in the drift, so they'd read as systematically weaker than
  an equally-skilled player who kept playing through it, independent of
  either player's actual current skill. This is exactly the kind of thing
  the still-outstanding shadow-mode live run (see "Release plan" below)
  should be checking for specifically, not just eyeballing whether the
  numbers look plausible.

**Points** — classic HLstatsX-style, opponent-ratio-scaled, additive from
normal play (points are earned by doing things, never lost by dying).
**One deliberate, documented exception, added 2026-08-04:** the
`teamkill_penalty`/`suicide_penalty` `elo_bonus_event` kinds above apply a
negative `points_delta`. Everything from *normal duels, assists, and round
wins* still only ever adds — the exception is scoped to these two penalty
kinds specifically, not a general reopening of "can points go down now."

```
ratio       = clamp(rating_victim / rating_attacker, points_ratio_min, points_ratio_max)
kill_points = round2(points_per_kill * ratio)          // beating a stronger opponent scales up

points_attacker += kill_points
if assister exists:
    points_assister += round2(kill_points * points_assist_fraction)

// per round, for every real human on the winning side:
points_winner += points_per_round_win
```

- `points_per_kill` default 10, `points_ratio_min`/`points_ratio_max`
  default 0.5/2.0, `points_assist_fraction` default 0.3,
  `points_per_round_win` default 2 — all configurable, all calibration
  guesses, same reason as the rating constants above.
- Same exclusions as rating (no bots, no team kills feeding kill-points,
  ask 11's gates apply to the whole round before any points are awarded).
- **Fixed 2026-08-04 (agent-chat #63), same rounding-to-zero bug as
  rating, and initially left as backlog for the wrong reason.** The
  earlier writeup here (and the still-open `elo_points` `DECIMAL(12,2)`
  item in the HLstatsX backlog) treated points as lower priority than
  rating because "rating is shown as a whole number, points wasn't" — the
  reasoning ran backwards. Points is the *more* visible failure of the
  two: a player who kills someone and sees the number not move notices in
  the same second a stalled rating never surfaces from one duel. It's
  also easier to trigger — `points_ratio_min`'s floor (0.5 today, 0.05
  once weapon-weighting lands, see the backlog) times `points_per_kill`
  can land under 1 point long before rating's much larger skill-gap
  requirement produces the same effect. Fixed the same way as rating:
  `elo_points.points`, `elo_kill_event.attacker_points_delta`, and
  `elo_bonus_event.points_delta` are all `DECIMAL(12,2)`, not `INT`; the
  in-memory `livePoints` cache is `decimal`; `kill_points`/assist points
  round to 2 decimal places (`round2` above), not 0, before being added.
  `TryGetPoints` keeps its `out int points` signature, same reasoning as
  `TryGetRating` — a display/consumption read rounds once, correctly,
  where it's called. One knock-on fix caught while making this change:
  `ShowRankCommand`'s rank query (`!elorank`) had been comparing the
  *rounded* display value against the raw column (`points > @points`) —
  harmless while both sides were integers, but with an exact column and a
  rounded parameter, two players who round to the same displayed total
  could miscount each other's rank at the boundary. Fixed to compare on
  the exact value, rounding only for the printed line.

## GDPR erasure — decided, not assumed

`elo_rating` and `elo_kill_event` are keyed on `steamid64` and are therefore
personal data. OSWeb's `deleteAccount` flow already captures `steamid64`
before erasing an account, so it has what it needs to reach these tables —
the open question was only *how* it reaches them:

- **(a)** OSBase exposes some delete mechanism OSWeb calls, or
- **(b)** OSWeb deletes directly in these tables.

**Chosen: (b).** Every other piece of this cross-repo relationship already
works by both sides reading/writing the same shared MySQL tables directly —
`weapon_event_rules`/`kill`/`score`, `tournament_match` — there is no
RPC/API layer between OSBase and OSWeb anywhere in this system,
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
| `elo_kill_event`       | `attackerid64`, `victimid64` (**two** columns, no plain `steamid64`) — **ANONYMIZED, not deleted** (also scrubs `attacker`/`victim` name columns) | `VARCHAR(32)` each |
| `player_hit_stat`      | `steamid64`                                       | `VARCHAR(32)` |
| `player_weapon_shots`  | `steamid64`                                       | `VARCHAR(32)` |
| `player_round_stat`    | `steamid64`                                       | `VARCHAR(32)` |
| `player_duel_stat`     | `attackerid64`, `victimid64` (**two** columns, no plain `steamid64`) — **ANONYMIZED, not deleted** | `VARCHAR(32)` each |
| `player_clutch_stat`   | `steamid64`                                       | `VARCHAR(32)` |
| `player_multikill_stat`| `steamid64`                                       | `VARCHAR(32)` |
| `player_teambet_stat`  | `steamid64`                                       | `VARCHAR(32)` |
| `player_daily_stat`    | `steamid64`                                       | `VARCHAR(32)` |
| `player_duel_total`    | `steamid64`                                       | `VARCHAR(32)` |
| `skill_log`            | `steamid` (`GameStats.cs`)                        | `VARCHAR(32)` |
| `serverinfo_user`      | `steamid` (`ServerInfo.cs`, not part of its PK)   | `VARCHAR(32)` |
| `faceit_cache`         | `steamid64` (`Faceit.cs`, PK)                     | `BIGINT UNSIGNED` |
| `player_teambet_log`   | `steamid64` (`TeamBets.cs`)                       | `VARCHAR(32)` |
| `knife_taser_kill_event` | `killer_steamid64`, `victim_steamid64` (**two** columns, no plain `steamid64`) — **ANONYMIZED, not deleted** | `VARCHAR(32)` each |
| `elo_bonus_event`      | `steamid64` (**deleted**); `related_attacker_id64`/`related_victim_id64` (**anonymized**, `kind='assist'` rows only) | `VARCHAR(32)` |
| `weapon_event_kill`    | `attackerid64`, `victimid64` (**two** columns) — **ANONYMIZED, not deleted** (also scrubs `attacker`/`victim` name columns) — same shape as `elo_kill_event`, found late (2026-08-04) because this module's own review never crossed into `EventWeekend.cs` | `VARCHAR(32)` each, NULL-able |
| `weapon_event_score`   | `steamid64` (`EventWeekend.cs`, part of PK `(event_id, steamid64)`) | `VARCHAR(32)` |
| `knivhelg_admin`       | `steamid64` (`EventWeekend.cs`, PK) — **note the type**, this one is the odd one out like `faceit_cache`; 16 rows on prod | `BIGINT UNSIGNED` |
| `knivhelg_userstats`   | `steamid64` — legacy, not written by current code, reported by OSWeb (agent-chat #23), 22 rows on prod, column type **not independently confirmed** (verify before assuming `VARCHAR(32)` — `knivhelg_admin`/`knivhelg_event` are proof this table family defaults to `BIGINT`, not `VARCHAR`) | unconfirmed, likely `BIGINT` by analogy |
| `eventweekend_admin`   | `steamid64` — legacy, not written by current code, reported by OSWeb, table empty (0 rows) on prod, type unconfirmed | unconfirmed |
| `eventweekend_userstats` | `steamid64` — legacy, not written by current code, reported by OSWeb, table empty (0 rows) on prod, type unconfirmed | unconfirmed |
| `knivhelg_event`       | `attackerid64`, `victimid64` (**two** columns) — **ANONYMIZED with numeric `0`, not the string sentinel** (id columns confirmed `BIGINT`, string sentinel silently failed against them — see the 2026-08-04 correction below), also scrubs name columns (`VARCHAR`, string sentinel) — legacy, not written by current code, 157 real rows on prod | `BIGINT(20) UNSIGNED` (confirmed) |
| `eventweekend_event`   | `attackerid64`, `victimid64` (**two** columns) — **ANONYMIZED, not deleted**, same shape as `elo_kill_event` per OSWeb, also scrubs name columns — legacy, not written by current code, table empty (0 rows) on prod, id-column type still unconfirmed — **do not assume `VARCHAR`, `knivhelg_event` just proved this table family defaults to `BIGINT`** | unconfirmed, likely `BIGINT` by analogy |

**2026-08-04, urgent, found by OSWeb's `doctor.php` running against prod
(agent-chat #23): eight tables added, all belonging to `EventWeekend.cs` —
this list had never once crossed into that module.** Two are live,
currently-written tables this session should have caught by grepping
`EventWeekend.cs` directly rather than trusting the existing list:
`weapon_event_kill` (same two-SteamID-plus-names shape as `elo_kill_event`
— **anonymize**, not delete, an oversight this document already made once
before for `knife_taser_kill_event`) and `weapon_event_score` (plain
`steamid64`, delete). `knivhelg_admin` is also live (`EventWeekend.cs`'s
own `AdminTable`) and is the same odd-type-out case as `faceit_cache` —
`BIGINT UNSIGNED`, not `VARCHAR(32)`, confirmed from the actual
`CREATE TABLE`, not assumed.

The other five — `knivhelg_userstats`, `knivhelg_event`,
`eventweekend_admin`, `eventweekend_userstats`, `eventweekend_event` — are
**not written by any code currently in this repo**, so they couldn't be
found by grepping `EventWeekend.cs` the way the three above were; they're
legacy tables from before the KnifeWeekend→EventWeekend rename (see
`project-weapon-event-contract`) and were reported directly by OSWeb
rather than independently verified here.

**Row counts on prod, confirmed by OSWeb 2026-08-04 (agent-chat #41) —
this is what decides which of these are worth anything, not just which
exist:** `knivhelg_admin` 16 rows, `knivhelg_userstats` 22,
`knivhelg_event` **157** (real history — 157 knifings from the old knife
weekends that every erasure sweep so far has left untouched), all three
`eventweekend_*` tables **0 rows, empty**. Owner decided all six stay on
the erasure list regardless — an empty-table `DELETE` costs nothing, and
that's cheaper than reconsidering the scope again later.

**Column types: one of the five confirmed, four still open.**
`knivhelg_event`'s id columns are `BIGINT(20) UNSIGNED NOT NULL`
(`SHOW CREATE TABLE` on prod, agent-chat #41) — **not** `VARCHAR(32)`,
and this wasn't academic: OSWeb's own erasure code had bound the string
`'ANONYMIZED'` against it, which either errors or silently coerces to `0`
depending on SQL mode — the sentinel silently failed in exactly the table
it was built to protect, until someone checked. See the correction in the
GDPR erasure section below for the fix (type-dependent sentinel, not one
constant everywhere). The remaining four (`knivhelg_userstats`,
`eventweekend_admin`, `eventweekend_userstats`, `eventweekend_event`) are
still unconfirmed — do not assume `VARCHAR(32)` for any of them; two
separate tables in this exact family (`knivhelg_admin`, `knivhelg_event`)
have now both turned out to be `BIGINT`, which is no longer a coincidence,
it's the family's actual default. Verify each one's real `CREATE TABLE`
(or ask OSWeb to paste it) before running any erasure statement against
them for real — this is precisely the kind of assumption that already
broke one real erasure (`faceit_cache`, 2026-08-03) and one anonymization
(`knivhelg_event`, 2026-08-04).

**Why this group was missed, worth remembering as a standing risk, not
just a one-time gap:** OSWeb writes `weapon_event_rules` but never reads
the results, so nothing in their own work ever pointed at these tables
either — confirmed by them independently, not just asserted here. OSBase's
side has the mirror problem: this whole document is EloRating's own
contract file, and a review anchored to "the module I happen to be working
in" will keep missing tables that belong to a different module, no matter
how many passes it gets. The fix that actually holds is the same one
already adopted for the OSWeb side: `doctor.php`-style cross-checking
against `information_schema` for the *whole* database, not a table list
maintained by hand and updated only when someone remembers to.

**2026-08-04: two more tables added, from `osbase-stat-contracts.md`
(OSWeb's column-semantics contract for clutch/multikill/teambet plus a new
knife/taser table).** `player_teambet_log` is the per-bet browsable log
alongside the existing `player_teambet_stat` counter — straightforward
single-`steamid64` erasure, same as the counter it sits next to.

**Corrected same day, before this ever shipped:** `knife_taser_kill_event`
was first documented here (and answered back to OSWeb) as delete-both-
columns, on the reasoning that a single rare event with no victim
leaderboard has no "loses the other player's history" concern. **That
reasoning was wrong and OSWeb caught it**: deleting the row on the victim's
erasure request would also erase the killer's side of it — and a knife/
taser kill is exactly the kind of rare, memorable moment this table exists
to preserve *for the killer*, so losing it to someone else's unrelated
erasure request is the same harm the anonymize decision on
`player_duel_stat`/`elo_kill_event` already exists to prevent. Same shape,
same treatment: `'ANONYMIZED'` sentinel on whichever of
`killer_steamid64`/`victim_steamid64` matches the request, row stays,
aggregate (which only ever counts the dealer, never the victim, per the
contract doc) is unaffected either way. No name columns on this table
(unlike `elo_kill_event`'s `attacker`/`victim`), so there's nothing else to
scrub here.

**Also added same day:** `killer_side`/`victim_side` columns (same
`SideT`/`SideCT`/`SideUnknown` int scale as `player_duel_stat`'s
`attacker_side`/`victim_side`) — team kills stay included (a raw event
record, not an achievement counter, confirmed with OSWeb), but the site
needs to tell them apart per-surface: a highlight feed can show both, a
"best with a knife" leaderboard almost certainly shouldn't count a
team-kill toward it. Not personal data, no GDPR implication.

**2026-08-04: `elo_bonus_event` added, found via agent-chat (#10), and it's
the first table with a genuinely *mixed* erasure treatment — not because it
holds two people's data like `player_duel_stat`, but because it holds one
person's data (`steamid64`, the one who got the award) plus a reference to
somebody else's duel (`related_attacker_id64`/`related_victim_id64`, on
`kind='assist'` rows only).** These need different treatment because
they're different things:

- **`steamid64` erases → the row is deleted outright.** This is the
  assister's own earned bonus; nobody else's history depends on this row
  existing, so there's no third party to protect the way `player_duel_stat`
  protects the non-requesting duelist.
- **`related_attacker_id64`/`related_victim_id64` erase → those two columns
  are anonymized, the row stays.** Deleting the whole row because the
  underlying duel's attacker or victim asked to be forgotten would destroy
  the *assister's* legitimate, unrelated bonus history over a request that
  was never about them — the exact shape of collateral damage the
  anonymize decision on `player_duel_stat`/`elo_kill_event` already exists
  to prevent, just one level removed (a reference to a duel, not the duel
  itself).

`kind='round_win'` rows have neither reference column populated (no duel
behind a round win), so only the plain `steamid64` delete ever applies to
them.

**2026-08-03: three tables added above after a full-repo sweep, not a
DamageReport/EloRating/TeamBets-scoped one.** The original 12-table list
only ever covered that module family; it never claimed to cover every
steamid-bearing table in the codebase, and these three quietly weren't on
it. `skill_log`/`faceit_cache` turned out to already be on OSWeb's own
erasure list independently (added 2026-07-22 and 2026-07-27 on their side,
never reconciled against ours until now — two lists that had each silently
drifted from "the" list). **`serverinfo_user` was a genuine miss on both
sides**: live per-server presence/scoreboard state, `steamid` column added
2026-07-02, never erased by anything before this. Overwritten on reconnect,
which does not retroactively fix a row for someone who already asked to be
forgotten and never reconnects. `faceit_cache`'s `BIGINT UNSIGNED` (the one
column not `VARCHAR(32)`) is fine for MySQL storage but caused a real
erasure bug on OSWeb's side: a `DELETE ... WHERE steamid64 = ?` bound with a
`STEAM_1:`/`[U:1:]`-form string against a numeric column gets coerced by
MySQL to `0`, silently matching (and deleting) any row that happens to hold
`0` instead of matching nothing. Fixed on OSWeb's side by only sending the
numeric string form against numeric columns — noted here so the next person
touching `faceit_cache` from either side knows why that column is the odd
one out.

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

**2026-08-04: `player_duel_stat` and `elo_kill_event` are ANONYMIZED, not
deleted, on an erasure request — corrected from the DELETE-both-columns
statement this section originally had.** These two are the only tables with
two SteamIDs in one row; deleting the row on either side's request would
also destroy the other, non-requesting player's history, which they never
asked to lose. Article 21 doesn't clear the bar for keeping the row
untouched under legitimate interest either (arguable at best for a hobby
leaderboard, solid only for ban records) — anonymizing removes the personal
data instead of relying on a balancing test. Full reasoning and requirements
agreed with OSWeb 2026-08-04:

- Sentinel constant: the literal string **`'ANONYMIZED'`** — non-numeric,
  cannot collide with a real 17-digit Steam64, greppable by anyone reading
  the table directly. Same constant for `attackerid64`/`victimid64` in both
  tables **and** for `elo_kill_event`'s `attacker`/`victim` VARCHAR(64) name
  columns — catching the name columns was the one thing the initial ask
  missed: the in-game nickname is the more identifying of the two to a human
  reader (a raw SteamID means nothing on sight; a nickname is exactly how
  people already recognize each other), so scrubbing the ID while leaving
  the name would be anonymization in name only.
- Every anonymized row collapses to the exact same constant, on purpose —
  that's what makes it unidentifiable, not a bug to fix.
- **No reversible link may exist anywhere**: no hash of the original
  SteamID, no "previous value" column, no side-table mapping raw ID to
  anonymized row. Any of those would make this pseudonymization, not
  anonymization, and the erasure obligation wouldn't actually be met.
- OSBase doesn't currently read `attackerid64`/`victimid64`/`attacker`/
  `victim` back out of either table for anything (`!elorank`/`!elotop` read
  `elo_rating`/`elo_points`/`player_duel_total` etc. instead — confirmed by
  grep, not assumed) — so filtering `'ANONYMIZED'` out of nemesis lists/kill
  history display is entirely OSWeb's query-side concern, no OSBase code
  change needed for the anonymization mechanism itself. Anonymized rows
  should still count toward aggregate sums (a kill is still a kill) — they
  just shouldn't surface as a named opponent slot anywhere.

**Correction, 2026-08-04, found the hard way (agent-chat #41): the string
sentinel is not universal — it only works on `VARCHAR` columns.** OSWeb's
own erasure code wrote the literal string `'ANONYMIZED'` against
`knivhelg_event.attackerid64`/`victimid64` (confirmed `BIGINT(20) UNSIGNED
NOT NULL` via `SHOW CREATE TABLE` on prod) and got either an error or a
silent `0`, depending on SQL mode — the sentinel silently failed in one of
the exact tables it was built for, and it would only have surfaced the
first time someone's erasure actually touched that table. **The type
decides the sentinel now, not a single constant for every table:**
- `VARCHAR` id columns (`elo_kill_event`, `player_duel_stat`,
  `knife_taser_kill_event`, `weapon_event_kill`, `elo_bonus_event`'s
  `related_*` columns — all confirmed `VARCHAR(32)` from live
  `CREATE TABLE` reads) → the string `'ANONYMIZED'`, as above.
- `BIGINT` id columns (`knivhelg_event`, confirmed; possibly
  `eventweekend_event` if it shares `knivhelg_event`'s lineage, **not yet
  confirmed**) → a numeric sentinel, **`0`** — safe specifically because a
  real Steam64 can never be `0` (every valid one is a 17-digit number in
  the `7656119…` range), the same reasoning that already ruled `0` in for
  `faceit_cache`'s numeric bind, just applied here to an anonymize target
  instead of a delete match. Bind it as an actual numeric parameter, not a
  string that MySQL then coerces — a numeric-typed `0` bound correctly is
  a deliberate sentinel; a string silently coerced to `0` is the exact
  `faceit_cache` bug from 2026-08-03 happening again in a different table.

OSWeb's `deleteAccount` should run:

```sql
DELETE FROM elo_rating           WHERE steamid64 = ?;
DELETE FROM elo_points           WHERE steamid64 = ?;
DELETE FROM player_hit_stat      WHERE steamid64 = ?;
DELETE FROM player_weapon_shots  WHERE steamid64 = ?;
DELETE FROM player_round_stat    WHERE steamid64 = ?;
DELETE FROM player_clutch_stat   WHERE steamid64 = ?;
DELETE FROM player_multikill_stat WHERE steamid64 = ?;
DELETE FROM player_teambet_stat  WHERE steamid64 = ?;
DELETE FROM player_daily_stat    WHERE steamid64 = ?;
DELETE FROM player_duel_total    WHERE steamid64 = ?;
DELETE FROM skill_log            WHERE steamid = ?;
DELETE FROM serverinfo_user      WHERE steamid = ?;
DELETE FROM faceit_cache         WHERE steamid64 = ?;
DELETE FROM player_teambet_log   WHERE steamid64 = ?;
DELETE FROM elo_bonus_event      WHERE steamid64 = ?;
DELETE FROM weapon_event_score   WHERE steamid64 = ?;
DELETE FROM knivhelg_admin       WHERE steamid64 = ?;   -- BIGINT: bind numeric, not string

-- Reported by OSWeb (agent-chat #23), not independently verified here -- confirm the
-- actual column type on each before running these for real. Written as string-bound
-- DELETEs below only because that's the majority pattern in this document, NOT because
-- it's been confirmed correct for these three -- knivhelg_admin right above this block
-- is proof the assumption can be wrong for this exact table family.
DELETE FROM knivhelg_userstats     WHERE steamid64 = ?;
DELETE FROM eventweekend_admin     WHERE steamid64 = ?;
DELETE FROM eventweekend_userstats WHERE steamid64 = ?;

UPDATE elo_kill_event
   SET attackerid64 = 'ANONYMIZED', attacker = 'ANONYMIZED'
 WHERE attackerid64 = ?;
UPDATE elo_kill_event
   SET victimid64 = 'ANONYMIZED', victim = 'ANONYMIZED'
 WHERE victimid64 = ?;
UPDATE player_duel_stat
   SET attackerid64 = 'ANONYMIZED'
 WHERE attackerid64 = ?;
UPDATE player_duel_stat
   SET victimid64 = 'ANONYMIZED'
 WHERE victimid64 = ?;
UPDATE knife_taser_kill_event
   SET killer_steamid64 = 'ANONYMIZED'
 WHERE killer_steamid64 = ?;
UPDATE knife_taser_kill_event
   SET victim_steamid64 = 'ANONYMIZED'
 WHERE victim_steamid64 = ?;
UPDATE elo_bonus_event
   SET related_attacker_id64 = 'ANONYMIZED'
 WHERE related_attacker_id64 = ?;
UPDATE elo_bonus_event
   SET related_victim_id64 = 'ANONYMIZED'
 WHERE related_victim_id64 = ?;
UPDATE weapon_event_kill
   SET attackerid64 = 'ANONYMIZED', attacker = 'ANONYMIZED'
 WHERE attackerid64 = ?;
UPDATE weapon_event_kill
   SET victimid64 = 'ANONYMIZED', victim = 'ANONYMIZED'
 WHERE victimid64 = ?;

-- knivhelg_event: confirmed BIGINT(20) UNSIGNED NOT NULL (agent-chat #41, SHOW CREATE TABLE
-- on prod) -- numeric 0 sentinel on the id columns, bound as a real int/BIGINT parameter,
-- NEVER the string 'ANONYMIZED' (that's the exact bug OSWeb just found and fixed: a string
-- against this column either errors or silently coerces to 0 depending on SQL mode -- if it
-- coerces, it happens to be harmlessly idempotent with the sentinel chosen here, but only
-- because 0 was deliberately picked as the sentinel, not because the coercion was safe).
-- Name columns are still VARCHAR (only the id columns were confirmed BIGINT), so they still
-- take the string sentinel.
UPDATE knivhelg_event
   SET attackerid64 = 0, attacker = 'ANONYMIZED'
 WHERE attackerid64 = ?;   -- ? bound numeric here, not string
UPDATE knivhelg_event
   SET victimid64 = 0, victim = 'ANONYMIZED'
 WHERE victimid64 = ?;     -- ? bound numeric here, not string

-- eventweekend_event: type NOT confirmed. Reported by OSWeb as same shape as
-- knivhelg_event/elo_kill_event but the id-column TYPE was never independently checked --
-- and knivhelg_event just proved this exact table family doesn't default to VARCHAR. Table is
-- empty on prod (0 rows, confirmed #41) so nothing breaks today, but do not run this against
-- real data without a SHOW CREATE TABLE first -- string-sentinel-against-BIGINT is precisely
-- the failure mode this whole note exists to prevent.
UPDATE eventweekend_event
   SET attackerid64 = 'ANONYMIZED', attacker = 'ANONYMIZED'   -- CONFIRM TYPE FIRST
 WHERE attackerid64 = ?;
UPDATE eventweekend_event
   SET victimid64 = 'ANONYMIZED', victim = 'ANONYMIZED'       -- CONFIRM TYPE FIRST
 WHERE victimid64 = ?;
```

with `?` bound as the string form of the Steam64 ID (not a native int/BIGINT
parameter) in every table above except `faceit_cache.steamid64`,
`knivhelg_admin.steamid64`, and `knivhelg_event.attackerid64`/`victimid64`
(all confirmed `BIGINT`/`BIGINT UNSIGNED` — see the 2026-08-03/2026-08-04
notes above on why a string-form bind against a numeric column is unsafe,
not just wrong-typed, and note `knivhelg_event` additionally uses `0`
rather than `'ANONYMIZED'` as its sentinel value, not just a numeric bind
of the same text) — and possibly one or more of `knivhelg_userstats`/
`eventweekend_admin`/`eventweekend_userstats`/`eventweekend_event`, whose
types are still unconfirmed but increasingly unlikely to be `VARCHAR`
given two-for-two `BIGINT` so far in this exact table family.

`serverinfo_user` also gets a separate, unrelated, non-targeted cleanup:
OSBase itself now runs a daily retention sweep (`ServerInfo.cs`,
`retention_days` config, default 30) that deletes any row — across every
server, not just the one running the sweep — whose `last_seen` is older
than the configured window, regardless of whether an erasure was ever
requested. Agreed with OSWeb 2026-08-04: a per-request backfill isn't
possible (a completed erasure removes the account and nick history needed
to even identify which rows were theirs — by design, since keeping such a
list would itself be a re-identification mapping), but isn't needed either,
since this table is live/session state with no other retention lifecycle. A
server that stops running the plugin entirely would never trigger its own
per-host prune, which is why this sweep is deliberately unscoped by
host/port. 30 days was confirmed to sit well clear of OSWeb's own longest
read window against this data (14 days). `player_round_stat` and `player_duel_stat` were
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

## Primary-key audit (originally 2026-07-21, updated 2026-08-04 — Nineteen tables)

Every `CREATE TABLE` read fresh from the code (not from this doc, not from
memory) and checked against every ask that touches it. **Was "Thirteen
tables" through 2026-08-03** — the heading number was a snapshot, not a
live count, and OSWeb flagged (correctly) that a stale count in a heading
reads as a current fact to anyone skimming it. Now covers every table on
the GDPR list (see above) plus the one non-personal-data table
(`server_stat_season`) — nineteen total:

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
| `skill_log` | none — pure append log, no `PRIMARY KEY` clause at all (`GameStats.cs`) |
| `serverinfo_user` | `(host, port, name)` — `steamid` is a non-key column (`ServerInfo.cs`) |
| `faceit_cache` | `(steamid64)` (`Faceit.cs`) |
| `player_teambet_log` | `(id)` autoincrement — a log, not a counter, same reasoning as `elo_kill_event` (`TeamBets.cs`) |
| `knife_taser_kill_event` | `(id)` autoincrement, same reasoning (`DamageReport.cs`) |
| `elo_bonus_event` | `(id)` autoincrement — a log, not a counter, same reasoning as `elo_kill_event` |

All original thirteen match what asks 1-22 actually specified, including
the deliberate
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

**Second `skill_log` consumer found 2026-08-05, and it's cross-repo, so this
analysis couldn't have caught it by grepping OSBase alone.** OSWeb's
`SkillRepository::recentAverageBySteamId64` reads `skill_log` directly via
SQL, called by `TournamentAdminController` to seed fair teams at a LAN.
`RatingRepository` (the Elo-side equivalent) has no bulk lookup — the
profile form curve already moved to `player_daily_stat.rating` (ask 22,
correctly), but this second consumer was never on anyone's list until
OSWeb found it. Not an OSBase gap: `elo_rating`'s schema (`steamid64` PK,
`rating` column) already supports exactly the query needed
(`SELECT steamid64, rating FROM elo_rating WHERE steamid64 IN (...)`) — the
missing piece is OSWeb's own repository method, not a table or column here.

**But the sequencing has a trap the request as phrased doesn't quite name:
`elo_rating` is currently empty.** Nothing in this whole file has shipped
to production yet (see [[project-data-collection-launch-gate]] /
`osbase-console-message-contract.md`'s standing hold) — `elorating` isn't
enabled anywhere real. So "build the batch reader before `skill_log` goes
cold" is necessary but not sufficient: `GameStats`/`skill_log` must also
keep being written **after** Elo actually ships, until enough matches have
accumulated for `elo_rating` to be useful for LAN-seeding — the same
warm-up dependency `TeamBalancer.balancer_skill_source` already waits on
(see "TeamBalancer" above), just a second, previously-untracked consumer of
that same wait. Add `TournamentAdminController`'s LAN-balance use to
whatever checklist gates flipping `GameStats`/`skill_log` off — it wasn't
on it before this was found.

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
to a table another system already owns end-to-end (`weapon_event_rules`,
site-owned; `player_hit_stat`, `DamageReport`-owned).
