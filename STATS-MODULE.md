# The stats module — a brief

Written 2026-07-20 for someone starting on this with fresh context. It is the
2026-07-19/20 investigation turned into a plan: measured against the real log
and the real database, not assumed. Where this contradicts older prose in
`docs/BACKLOG.md`, this is the one that was checked.

## The one rule that shapes everything

**Collection can never be done retroactively.** Every day a switch stays off is
a day of data that does not exist and cannot be recovered. So the cheap switches
are worth throwing long before anything is built to read them — the parser can
come later, the evening it recorded cannot.

The single exception is demos, which is why they matter so much: they are the
only route to the past.

## Where the data can come from

Four sources. They answer different questions and do not compete.

### 1. The log stream — free, flowing today, no plugin needed

Already piped in and already parsed for the connection map. A live kill line
looks like:

```
"Steel<6><BOT><TERRORIST>" [1946 1236 174] killed "Rebel<5><BOT><CT>" [1957 1028 167] with "glock" (headshot)
```

Killer, victim, team, **world coordinates for both**, weapon, headshot flag.

`src/Services/ServerConnectionTracker.php` parses connect, disconnect and token
lines and **throws the rest away**. So knife kills, zeus kills, per-weapon counts
and headshot rates need no plugin and no config change — only a parser that
stops discarding. This is the cheapest real win available.

**Hitgroups do NOT arrive over the log stream** — the `attacked … (hitgroup
"chest")` lines need `mp_logdetail`, which the current log doesn't have.
**Moot, and retracted (2026-07-20):** OSBase's `DamageReport` module already
receives hitgroup, weapon, damage and direction straight from the live
`EventPlayerHurt` game event, per shot, with no cvar and no log parsing.
That path is strictly better than `mp_logdetail` on every axis it was being
considered for — structured per-shot data instead of a log line to parse, no
added log volume, no cvar to remember to keep on. See the "cheap switches"
step below and `ELO-MODULE.md` / the hit-aggregate section for what
`DamageReport` now persists.

### 2. Demos — the only path to history

Years of them are already saved, and a demo records game events at tick level
regardless of logging settings. Hit zones, positions and damage are sitting in
files that already exist.

Caveats, both real:

- CS:S, CS:GO and CS2 use **different demo formats** — one parser per era.
- None of the usable libraries is PHP. They are Go and Rust. That means an
  out-of-band tool writing into the database, not something the web app does
  inline.

Size the job before starting: which years, which game, how many files.

**LAN placements may be recoverable from demos**, which matters for the medals
idea (bronze/silver/gold with the year on a profile) — it does not depend on
whether e107 ever stored placements or only rosters.

### 3. OSBase — the right long-term instrument

You own the schema, so a module can write exactly the counters you want,
directly. This is where it should end up. It is also already the thing that
records the demos.

### 4. `lvl_base` — what exists now

The activity ladder. Keep it as that. It has no kill-event stream, so none of
the dimensions below can come from it.

## ⚠️ Do NOT enable `tv_autorecord`

This looks like an obvious cheap switch and it is a trap.

A recording still **running** when the map changes takes the CS2 server down
with it, roughly three times in four. Valve almost certainly never sees it:
Premier runs one match per disposable cloud server, so their instances are torn
down rather than changing map. Do not plan around a fix — an architecture that
never meets the condition will not prioritise it. **Treat the workaround as
permanent.**

That workaround is OSBase, which is exactly why it records instead: it starts a
demo after each warmup and closes it when the match ends or the map changes, so
nothing is ever mid-write during the transition. Turning the built-in cvar on
"as well" would reintroduce the precise condition OSBase exists to avoid.

The per-match segmentation is a happy side effect, not the goal — but it is what
makes picking LAN matches out of years of demos tractable.

## What to actually build

**Built (2026-07-20).** `src/modules/DamageReport.cs` in OSBase now persists
this aggregate to a `player_hit_stat` table (`steamid64`, `weapon`,
`hitgroup`, `direction`, `hits`, `damage`), fed directly from the live
`EventPlayerHurt` event — no log parsing, no `mp_logdetail`. Writes are
buffered per round and flushed between rounds (same pattern as
`EventWeekend`/`EloRating`'s buffered writes). This table is owned by
`DamageReport` alone — nothing else should write to it, same never-a-
second-writer guardrail as everywhere else in this system. The rest of
this section is kept as the record of why it's shaped the way it is.
**Note added 2026-08-04 (agent-chat #18):** this section originally also
pointed at `player_kill_stat` as a second example of the same guardrail —
that table never existed (`ServerKillTracker`, its intended writer, was
never built; OSWeb dropped the placeholder table in migration 0169). See
the corrected headshot table below.

**Also built the same day: shots fired.** `EventPlayerHurt` only fires on an
actual hit, so it can't answer "accuracy per weapon" on its own — a miss
never generates it. `DamageReport` now also hooks `EventWeaponFire` (fires
once per trigger pull, hit or miss) into a second table,
`player_weapon_shots` (`steamid64`, `weapon`, `shots`).

**hits/shots is only a percentage for single-projectile weapons.**
`weapon_fire` fires once per trigger pull; `player_hit_stat` counts once per
*victim hurt*. Those match 1:1 for a rifle, pistol or sniper round, but not
for:

- **Shotguns** (`nova`, `xm1014`, `mag7`, `sawedoff`) — one trigger pull is
  ~9 pellets, each its own `EventPlayerHurt`.
- **Grenades** (`hegrenade`, `molotov`/`incgrenade`, and `flashbang` if flash
  damage is ever counted) — one throw can hurt several people at once.
- **Melee** — unconfirmed whether `weapon_fire` fires per knife swing at
  all; if it turns out it doesn't, knife accuracy simply has no `shots`
  denominator (not a >100% case, a missing-data one). Verify empirically
  before shipping a knife accuracy stat.

The data is correct in all these cases — the ratio just isn't a percentage
for them. Recommended handling (site-side, no OSBase change implied): only
render "accuracy %" for single-projectile weapons; show shotguns/grenades as
a plain "hits per shot" count with no percent sign, or hide the ratio for
them entirely. `DamageReport` doesn't compute the ratio itself, so nothing
here needs to change if the classification changes later.

**"HS%" means two different things — decide which one is shown before
building it, because they diverge:**

| Definition | Source | What people usually mean |
|---|---|---|
| headshot kills / kills | `player_duel_total.headshots` / `.kills` (OSBase-owned — **corrected 2026-08-04, agent-chat #18**: originally pointed at `player_kill_stat.headshots`, a site table that was never built) | ✅ the classic CS scoreboard stat |
| headshot hits / all hits | `player_hit_stat WHERE hitgroup=1 (Head) AND direction=dealt` | More precise, but not what people compare |

A player who chips the body and finishes with one headshot gets a high
kills-HS% and a low hits-HS%. Both numbers already exist without new work —
don't build a third version of either in OSBase.

**Expanded 2026-07-21: `side` and `season`, plus two new tables.** The
site's stats/ELO ambition grew from "a body diagram" to "the engine behind
profiles, leaderboards, and nemesis lists" — which meant revisiting the
dimensions before more history piled up without them, per the one rule
above. `player_hit_stat` and `player_weapon_shots` both gained `side`
(`TINYINT`, CS2's own team numbers: 2=T, 3=CT) and `season` (`VARCHAR(8)`,
e.g. `2026Q3`, computed at write time) in their primary keys — both added to
*both* tables together, deliberately, so accuracy stays computable per
season/side (a numerator split finer than its denominator would otherwise
silently break).

**Fixed 2026-08-05 (osbase-side-encoding-fix.md): `side` shipped as 0=T/1=CT,
not the 2=T/3=CT above.** The module used a private 0/1 scheme instead of
CS2's own `CsTeam` enum, which collided with what those digits actually mean
in the game (0=unassigned, 1=spectator) and made the encoding unverifiable
by anyone who didn't read this doc. `player_hit_stat.side`,
`player_weapon_shots.side`, `player_duel_stat.attacker_side`/`victim_side`,
and every other table sharing this column (`player_round_stat`,
`player_clutch_stat`, `player_multikill_stat`,
`knife_taser_kill_event.killer_side`/`victim_side`) now write CS2's real team
numbers. `player_hit_stat.side`, `player_weapon_shots.side`, and
`player_duel_stat.attacker_side`/`victim_side` never carry an "unknown" value
at all (guarded in `AddHitCounter`/`AddShot`/`AddDuel`/`AddKnifeTaserKill`) —
under the old scheme, 2 doubled as both a real team and "unknown", which
would have made every Terrorist-vs-Terrorist duel misread once 2 became a
real team number. `player_round_stat` still legitimately writes an unknown
side for spectators/mid-transition players (ask 11) — using CS2's own
`CsTeam.None` (0) for it, not an invented sentinel.

**Decided 2026-08-05: no migration, the affected tables were truncated
instead.** A migration would have needed a non-re-runnable `CASE` pass for
`player_round_stat` (it holds two old-encoding meanings under the same value
once a Terrorist round overlaps the old "unknown" sentinel) plus a strict
stop-module-first deploy order — real risk to take on for a module that had
only been live a few days (2702 hit rows, 188 rounds, 835 duels; all test
play, no production history worth protecting). `player_hit_stat`,
`player_weapon_shots`, `player_round_stat`, `player_duel_stat`,
`player_duel_total`, `player_clutch_stat`, `player_multikill_stat`, and
`knife_taser_kill_event` were truncated in the same deploy window as this
fix, module stopped first. `skill_log` (GameStats, no side column, unrelated
to this bug) and `player_teambet_*` (real balances, never on the affected
list) were deliberately left alone.
`season` is a filter only; hit/shot data itself is never reset — a
quarterly ELO reset, if built, is a separate table's concern.

Two new counter tables, same owner (`DamageReport`), same buffer-and-flush
pattern:

- **`player_round_stat`** — `(steamid64, side, season)` → `rounds`. Exists
  so damage-per-round is computable at all:
  `SUM(player_hit_stat.damage WHERE direction=dealt) / player_round_stat.rounds`.
  Damage alone was already recorded; there was no denominator for it.
  **Corrected 2026-08-04 (agent-chat #36): not "ADR".** `damage` is raw
  `DmgHealth`, confirmed uncapped against the victim's remaining HP (live-
  server test: a Zeus/taser's nominal 500 damage shows as 500 even though
  max HP is 100) — so this ratio isn't comparable to HLTV/Faceit/Leetify's
  ADR, which clamps. OSWeb calls the site-facing number "Skada/rond"
  (damage/round) for exactly this reason; use that term here too, not ADR,
  so a future reader doesn't assume this ratio means what the industry term
  means.

**Sealed decision, 2026-08-04 (agent-chat #36/#37/#38), same shape as the
Ace decision above — recorded so a missing "clamped damage" column doesn't
read as an oversight later.** A comparable-to-other-sites ADR would need
its own column: `player_hit_stat.damage` sums raw hits, and a sum can't be
retroactively clamped, so this is another "capture now or lose forever"
fork, same family as the victim-weapon and bonus-log fixes earlier this
week — flagged by OSWeb as time-critical for exactly that reason. **Owner
decided against it**, immediately after OSWeb raised it: chasing
comparability with sites this community doesn't compete against isn't
worth a column and a variable, and the number people actually want
("am I getting better") is already answered by the existing, uncapped
`Skada/rond` as long as it's honestly named — which it now is.

The formula, correct and worth keeping even though not built, in case this
is revisited: **the naive `100 - victim.health` is wrong** — it credits
the *victim's total health lost that exchange* to whoever lands last,
double-crediting anyone who also damaged them. The right measure is a
per-attacker delta against the victim's health *at the moment of each hit*:

```
// per victim, reset every round start
lastHealth[victim] = 100

// on each EventPlayerHurt
taken = lastHealth[victim] - event.Health   // event.Health is POST-hit
lastHealth[victim] = event.Health
// credit `taken` to the attacker -- clamping falls out on its own,
// a killing blow naturally yields exactly what was left
```

One int per victim, reset at round start (same lifecycle as the clutch/
round-scoped state already kept there). **The edge that would decide
whether it's actually comparable, not just different:** HLTV-style ADR
excludes team damage; `player_duel_stat` deliberately keeps team kills
unfiltered and should keep doing that, but a clamped-for-comparability
column would need team damage excluded specifically, or it stays
incomparable for a new reason instead of the old one.
- **`player_duel_stat`** — `(attackerid64, victimid64, attacker_side,
  victim_side, weapon)` → `kills`, `headshots`. Feeds nemesis lists ("who
  kills me" / "who I kill", per weapon, per side). Both directions of a pair
  are stored (not just the victim's) so either profile can filter by *their
  own* side; a team kill is simply a row where both sides match, not
  excluded. Runs off `EventPlayerDeath` on **every** kill on every server —
  deliberately not gated to a `tournament_match` window. That gate belongs
  to `EloRating`'s own scoring decision (see `ELO-MODULE.md`), not to
  whether a duel gets counted here; nemesis lists are exactly where ordinary
  server rivalries live, tournament or not. No `season` column here on
  purpose — rivalries read all-time.

**Weapon-name normalization, kept consistent across OSBase's own tables.**
`DamageReport` folds `knife_*`/`*bayonet*` → `knife`, `taser`/`zeus`/
`zeusx27` → `taser`, and strips a trailing `_projectile` suffix — the same
`NormalizeWeapon` used everywhere a weapon column exists in this system
(`player_hit_stat`, `player_weapon_shots`, `player_duel_stat`,
`elo_kill_event`), so a weapon reads identically no matter which table it's
joined from. **Corrected 2026-08-04 (agent-chat #18):** this note
originally said the goal was joining against OSWeb's `player_kill_stat`
(parsed by a `ServerKillTracker::normaliseWeapon` OSBase had no access
to) — that table and parser were never built; the normalization's actual
and only job is internal consistency across OSBase's own tables.

**Expanded again 2026-07-21: asks 5-10 (`docs/traffkarta-hit-stats.md`).**
Six more gaps found while side/season were being wired up on OSWeb's end.

- **Ask 5 (urgent — an already-written table).** `player_duel_stat` got
  `season VARCHAR(8)` added to its primary key. It had shipped without a
  time dimension at all, which would have made nemesis permanent — whoever
  owned you in 2019 keeps the title forever. Computed at write time, same as
  the other tables.
- **Ask 6 — how the kill happened.** `player_duel_stat` gained `noscopes`,
  `wallbangs` (`Penetrated > 0`), `blind_kills` (`Attackerblind`),
  `smoke_kills` (`Thrusmoke`), all straight off `EventPlayerDeath` — no new
  rows, existing per-duel counters.
- **Ask 7 — `dominations`, `revenges`.** Also on `player_duel_stat`, from the
  same event's `Dominated`/`Revenge` fields (both `Int32` in the CS2 API,
  treated as `> 0` triggers here) — lets OSWeb check its own tally against
  the game's built-in mechanic.
- **Ask 8 — bomb work**, added to the already-existing `player_round_stat`
  key: `bomb_plants` (`EventBombPlanted`), `bomb_defuses`
  (`EventBombDefused`), `defuse_fails` (began a defuse via
  `EventBombBegindefuse` — note the CS2 API's actual casing, not
  `BeginDefuse` — and never got a matching `EventBombDefused` for themselves
  that round). `Haskit` is available on the begin-defuse event but
  deliberately not split into its own column — a no-kit-defuse distinction
  was explicitly called out as a separate, later decision.
- **Ask 9 — clutches**, new table `player_clutch_stat`
  `(steamid64, side, season, opponents)` → `attempts`, `wins`. An attempt is
  logged the moment a side drops to exactly one player alive (checked after
  every death), against however many opponents are alive right that instant
  — not re-evaluated as the enemy is whittled down further, and not derived
  from the round's outcome (a lost clutch still has to produce an attempt
  row, or the win rate is meaningless). One clutch state per player per
  round, even if the round drags on afterward.
- **Ask 10 — multikills**, new table `player_multikill_stat`
  `(steamid64, side, season, kills)` → `rounds`. Exact N, no 5-cap (pub team
  sizes exceed 5v5, so 6k/7k happen); collapsing to "5k+" at write time would
  make the individual counts unrecoverable, same rule as everything else
  here.

**Sealed decision, 2026-08-04 (agent-chat #26/#27), so it doesn't get
re-litigated as an oversight later: "Ace" stays pinned to a plain `5k`,
not derived from enemy team size.** Raised and retracted in the same
exchange — OSWeb first asked for a `player_clutch_stat.opponents`-style
"enemies alive" dimension on this table (a true ace is eliminating the
*whole* enemy team, which a `5k` in an 8v8, or after a disconnect leaves
four, doesn't actually mean or fail to mean), then withdrew it: the site
owner decided Ace is what every player already calls a five-kill round —
a familiar name, not a strictly-correct-but-unrecognized derived term —
and the edge cases are things that happen to you, not the common case.
**No `enemies`/team-size column was added here on purpose.** If a
technically-accurate ace ever becomes wanted later, that's a new column
and a new ask, not a redefinition of this one — same non-retroactive rule
as everything else in this document, just applied to a decision *against*
capturing a dimension rather than for one.

**Two judgment calls made while implementing asks 5-10 — one confirmed, one
overturned (2026-07-21):**

- Multikills count enemy eliminations only, team kills excluded. **Confirmed
  correct** — keep. `player_duel_stat` still records team kills unfiltered,
  as asked; the exclusion is specific to the multikill count. The
  consequence that a round's multikill count doesn't sum to that round's
  total kills is expected and correct, not a discrepancy to fix.
- Clutch opponent counts (`opponents`) were implemented to include bots as
  alive opponents. **Overturned.** A bot is not a person — bots are excluded
  everywhere in this module (hits, shots, rounds, duels, clutches,
  multikills), not just from getting their own written row. Consequence: a
  1v4 where three of the four are bots IS a 1v1, and is recorded as one. The
  bot-inclusive alive-count (`IsAlive`) was rewritten to filter bots out
  entirely, same as every other alive/eligibility check in the module.

**Also settled the same day: post-round kills need no special case.** Two
earlier alternatives (drop them; keep them behind separate
`post_kills`/`post_hits`/`post_damage` columns) were both rejected. A kill
that happens after `EventRoundEnd` but before the next `EventRoundStart` (the
round-end freeze window — a mercy kill, the kill that actually decided the
round, a bomb death) is simply a kill, counted exactly like any other,
including toward multikills. Matching what the scoreboard shows the player
beats a cleanliness that would need explaining, and it removes two edge
cases (the decisive kill, bomb deaths) instead of special-casing them.

## Ask 11 — gates, not counters (2026-07-21, priority over everything else)

The most urgent item in the whole document: every round recorded without
these gates is warmup noise and empty-server noise mixed permanently into
real lifetime counters, and a lifetime counter can't unlearn bad history
once summed. Applies to **every** table above plus `player_teambet_stat`
(ask 12, below):

1. **No bots, anywhere** — not a new rule, but stated explicitly as the
   umbrella the rest sits under.
2. **Nothing during warmup.** Not configurable — hard rule.
3. **Nothing under N connected humans.** Configurable (`min_players` in
   `damagereport.cfg`/`teambets.cfg`, default 4) — the right threshold isn't
   knowable until real data exists to look at, and changing it must never
   require a rebuild.

**Decided at round start, not round end.** Evaluating at round end would
silently exclude normal play whenever people log off late in the evening
(headcount drops below the threshold as the night winds down, even though
the round was played at full strength). `DamageReport`/`TeamBets` each
compute a `statsGateOpen` bool once in their `OnRoundStart` handler and hold
it for the whole round regardless of who connects or disconnects while it's
live; every write path checks that flag, not a live recount. The canonical
example that motivated this: two players trading AWP shots on an empty
evening server would otherwise produce 100% headshot rate and a 1v1 clutch
every single round, indistinguishable in the data from a real close game.

## Ask 12 — TeamBets (2026-07-21)

`TeamBets.cs` discarded every bet's outcome at round end. New table,
`player_teambet_stat` (`steamid64, season`) → `bets`, `wins`, `staked`
(`BIGINT`), `returned` (`BIGINT`), `biggest_win` (`INT`), `biggest_win_stake`
(`INT`), `biggest_win_at`. Same gates as ask 11, duplicated into
`TeamBets.cs`'s own config/round-start check (separate module, no
cross-module state coupling).

- **`staked`/`returned` kept separate, never netted.** Net is one
  subtraction away and easy to add back; going the other direction — trying
  to recover how much someone actually risked from a stored net figure — is
  not possible after the fact. Someone churning 10x the betting volume for
  the same profit is the more interesting player, and that's invisible in a
  net-only number.
- **`biggest_win` is NET profit** on one winning bet (`returned - staked`
  for that bet), **not total payout — corrected 2026-07-21** (the ask was
  ambiguous, total payout was the first guess, net was the actual intent).
  Reasoning: a payout-sized leaderboard is really a wallet-size leaderboard
  — staking 10,000 to get back 10,100 would top it over risking 100 to win
  4,900, and the second one is the story people actually retell.
  **`biggest_win_stake`** was added alongside so the choice doesn't destroy
  information either way: the total payout is recoverable as
  `biggest_win + biggest_win_stake`, and the odds are visible in the
  retelling ("won 4,900 on a hundred-dollar bet") — the stake for that
  specific bet is exactly the kind of fact no later migration could dig
  back out otherwise, same rule as everything else in this document.
  `biggest_win`/`biggest_win_stake`/`biggest_win_at` are updated together in
  lockstep (all three or none) on every flush, via `GREATEST()` plus a
  matching `IF()` comparison for the other two against the pre-update value.
- **`season`** — otherwise "this season's biggest win" doesn't exist as a
  queryable fact.
- A voided round (`RefundAllBets`, no valid winning team) records nothing —
  a refunded bet never risked anything and resolves nothing, so there's no
  outcome to persist.
- `Bet` gained a `SteamId64` field, captured at placement time — needed
  because a bettor can disconnect before round end, and identity has to
  survive that even though `InGameMoneyServices`-based payout obviously
  can't.

## Asks 13-17 (2026-07-21, `traffkarta-hit-stats.md`)

Found while OSWeb built its readers against the real schema.

- **13. `first_seen DATETIME`** on all seven `DamageReport`/`TeamBets`
  counter tables (`player_hit_stat`, `player_weapon_shots`,
  `player_round_stat`, `player_duel_stat`, `player_clutch_stat`,
  `player_multikill_stat`, `player_teambet_stat`). Set once, on `INSERT`
  only — absent from every `ON DUPLICATE KEY UPDATE` clause, so it survives
  every later update untouched. Answers "what period do these totals
  actually cover" without which a page full of lifetime numbers invites the
  wrong reading (4,000 hits — a decade, or a fortnight?). **Not** added to
  the two new roll-ups (`player_duel_total`, `server_stat_season`) or to
  `player_daily_stat` — the latter's `day` column already pins the row to
  an exact period more precisely than `first_seen` could, and the roll-ups
  postdate ask 13 and weren't in its scope; flag if that scoping was wrong.
- **14. `player_round_stat.rounds_won INT`.** One column — the round result
  (`EventRoundEnd.Winner`) is already in hand at the exact point it's
  needed to resolve a clutch attempt (ask 9), so this is free to compute
  alongside. Losses need no column: `rounds - rounds_won`. Deliberately
  overlaps `cs2rank.lvl_base`'s lifetime `round_win`/`round_lose` — the
  overlap is the point, since `lvl_base` holds one number for a career and
  this holds the same number split by side, season and (ask 17) map, behind
  the ask-11 gates besides. The two will disagree; that's expected, they
  count to different rules, and the site should read one or the other for a
  given number, never both on one page.

**Two behaviors of `rounds`/`rounds_won`, confirmed 2026-08-04 against
`DamageReport.cs`'s actual `OnRoundEnd` loop (agent-chat #29), neither
documented anywhere before this:**
- **A round counts as "played" based on presence at round *end*, not round
  start.** The loop is `foreach (var p in Utilities.GetPlayers())` run when
  `EventRoundEnd` fires — there is no round-start snapshot compared
  against it. A player who connects mid-round and is still present when it
  ends gets a `rounds`+1 row for that round, same as someone who was there
  the whole time.
- **`rounds_won` doesn't check whether the player was alive.** The
  condition is `side == winnerSide` — a player who died mid-round still
  gets credited with the win if their team took the round. There is no
  separate "survived" dimension anywhere in this table.

- **15. `player_daily_stat` (steamid64, day) → hits, damage, headshots,
  shots, rounds.** A season is too coarse for "how have I played this
  week" — in week one of a quarter the season total is nearly empty, so a
  season-over-season comparison is noise. Deliberately narrow: no weapon,
  hitgroup, or side, which is what keeps `player_hit_stat` big; multiplying
  a per-day table by those dimensions would be the reckless version of
  this table. **Judgment call:** tracks dealt/offensive output only
  (hits/damage/headshots dealt, shots fired, rounds played) — ask 15's
  column list doesn't split by direction at all, and "form" reads naturally
  as personal output rather than what was received; flag if received stats
  were wanted here too. Fed from the same hook points as the dimensioned
  tables (`EventPlayerHurt` dealt branch, `EventWeaponFire`, round end), so
  no new event wiring, just one more narrow upsert per event.
  **`side` resolved, sealed, before any write (2026-07-21):** ask 15's own
  original framing called this "a now-or-never call" and left it open. No
  writes exist yet, so it was still open to decide — closed now, on
  purpose, not by letting the deadline pass unnoticed. **Decided: no
  `side` here.** No feature in this whole design ever asked for a
  day-resolution per-side form curve — not even `skill_log`, which
  `dailyHistory()` already reads and which has no side column either. The
  per-side truth already exists at quarterly grain
  (`player_hit_stat`/`player_round_stat`); doubling this table's row count
  for a view nobody requested would be exactly the kind of premature
  capacity this document otherwise argues against building. Unlike `map` on
  `player_round_stat` (ask 17), which is reversible in the direction that
  matters — added later, only past history is lost — a missing `side` on a
  daily row can never be split apart at all once summed, so this had to be
  an actual decision, not a default. Recorded here so nobody re-opens it by
  accident once rows exist.
- **16. Two roll-ups**, because some questions are expensive from the
  detail tables alone:
  - **`player_duel_total` (steamid64, season) → kills, deaths** — so "does
    this opponent do worse against you than against everyone else" (the
    nemesis verdict's strongest factor) is a point lookup against a
    two-column total instead of a `UNION`/`GROUP BY` over every duel row
    (what `DuelStatRepository::generalForm()` does today). Same scope as
    `player_duel_stat` — team kills included, since it's the same number
    already in hand when a duel row is written.
  - **`server_stat_season` (season) → hits, damage, headshots, shots,
    rounds** — the same summary shape as `player_daily_stat`, but
    server-wide and per-season, so "is your 19% headshot rate good" has
    something to compare against (a 14% server average) without
    aggregating every player's rows on every profile view. **Not personal
    data** — no `steamid64`, nothing to add to the GDPR erasure list; see
    `ELO-MODULE.md`.
- **17. `player_round_stat.map VARCHAR(32)`, added to its primary key**
  (`steamid64, side, season, map`). Deliberately **not** on `player_hit_stat`
  — that table is already the expensive one (weapon × hitgroup × direction
  × side × season), and multiplying it by a map rotation would be the one
  genuinely costly change in this whole document. Rounds are cheap: one row
  per (side, season, map) per player, roughly ten rows per player per
  quarter on a ten-map rotation. Buys rounds/win-rate per map (with ask 14),
  bomb work per map (ask 8), "you've never actually played Nuke" — does
  **not** buy a per-map heatmap or per-map weapon breakdown, which would
  need the dimension on the big table; that trade can be made later if
  anyone wants it enough to pay the row-count cost. Current map read via
  `osbase?.currentMap ?? Server.MapName ?? ""`, same expression
  `EloRating.cs` already uses for `elo_kill_event.mapname` (tournament
  matches only) — ask 17 covers the gap that leaves: ordinary pub rounds
  had no map recorded anywhere before this.

## Ask 18 — "yesterday's highlights" needs kills and seconds (2026-07-21)

Checking `player_daily_stat` against the old site's "Gårdagens highlights"
widget (five rows, one player and one number each) found two real gaps and
one ambiguity that had to be settled before writing, not after:

- **`kills INT`** — added. "Most kills" wasn't answerable from the table as
  it stood (only hits, damage, shots, rounds). Same team-kill exclusion as
  `player_multikill_stat`, for the same reason: a TK inflating a personal
  kill count is nonsensical.
- **`seconds INT`** — added. No existing source anywhere in this system
  answers "most time online yesterday": `lvl_base` only has lifetime
  playtime, and OSWeb's `server_connection` is pruned to a short window.
  Sampled at round end against a per-player last-seen timestamp rather than
  a connect/disconnect delta — a server crash or ungraceful disconnect just
  stops accumulating that way instead of losing the whole session's time.
- **`headshots` redefined as headshot-KILLS, not headshot-hits.** The
  column already existed (from ask 15) sourced from `hitgroup=1` in
  `EventPlayerHurt` — a body-diagram-appropriate definition, but the wrong
  one here. In `player_hit_stat` terms a headshot is any hit with
  `hitgroup=1`, and landing three of those on someone wearing a helmet
  without killing them is completely normal — not what a scoreboard, or
  the old widget, meant by "headshot". **Decided: headshot-kills**
  (`EventPlayerDeath.Headshot`), same source `player_multikill_stat` and
  `elo_kill_event` already use, and the same team-kill exclusion as `kills`
  above. Headshot-hit detail hasn't gone anywhere — it's still exactly
  answerable, unambiguously, via `player_hit_stat WHERE hitgroup=1`; this
  column just isn't the place for it. `server_stat_season` (ask 16b) got
  the same redefinition for consistency, since it's described as sharing
  "the same summary columns" as `player_daily_stat` — a divergent meaning
  for the same column name across two directly-compared tables would have
  silently broken exactly the "your rate vs server average" comparisons
  `server_stat_season` exists for.

## Ask 22 — `player_daily_stat` needed an Elo snapshot before anything writes (2026-07-21)

Found checking the Elo consolidation package against the readers it
replaces (`ELO-MODULE.md`'s "what it is" reversal). `skill_log` was a
**time series** — one row per player per map, timestamped;
`SkillRepository::dailyHistory()` reads it as `GROUP BY DATE(...)` over 180
days to draw the form curve on every profile, plus peak/change next to it.
`elo_rating` is a **single current-state row per player** — no history at
all. Retiring `GameStats` would have pulled the curve's only source out
from under it, and cost two of ask 18's five highlight rows too ("best/
worst skill change yesterday" needs a rating *per day*; a single current
value can't produce a difference).

**The strongest form of non-retroactive in this whole document.** Every
other missing dimension loses a breakdown that could in principle still be
filtered later; this one loses time itself. A rating never written down on
a given day cannot be reconstructed from whatever it later became — not by
any query, ever, because the information (what it was *then*) simply never
existed anywhere.

Fix: two columns on `player_daily_stat`, which was already keyed exactly at
the resolution the form curve needs anyway (`dailyHistory()` collapses to
days regardless):

```sql
-- player_daily_stat
rating INT NULL,  -- elo_rating.rating as of this day's last round-end write
points INT NULL,  -- elo_points.points, same moment -- "who climbed most this week" for free
```

**A snapshot, not a counter — overwritten every time, never summed.**
Rating is already smoothed by being a rating; re-averaging it across a
day's writes would smooth it twice. Read live from `EloRating` (a new
`TryGetRating`/`TryGetPoints` public API, see `ELO-MODULE.md`), never a DB
round trip — two independently-scheduled `EventRoundEnd` subscribers
(`DamageReport` and `EloRating`) have no guaranteed ordering relative to
each other, so a snapshot can't wait on the other module's own flush
landing first. `NULL` (not `0`) when `EloRating` isn't loaded that day —
"no data" has to stay distinguishable from "rating was zero".

The alternative — keeping `GameStats` alive solely so the curve has a data
source — is exactly what this whole consolidation exists to avoid.

## Ask 24 — `player_duel_total` needed assists and headshots too (2026-07-22)

Found while scoping the release plan against `!elorank`. `player_duel_total`
(ask 16a) was built as a roll-up of `player_duel_stat` — kills/deaths only,
because that's what "how do you do against this opponent overall" needed at
the time. But it's the one table already keyed at exactly `(steamid64,
season)` — no side, no weapon, no opponent — which makes it the natural
home for the player's whole-period summary, not just the duel-specific
slice of it. `!elorank` needs kills/headshots/deaths/assists as period
totals; headshots existed only per-opponent-pair in `player_duel_stat`
(an expensive `SUM()` for a number that's already sitting in hand at the
`OnPlayerDeath` call site), and assists didn't exist anywhere outside
`EloRating`'s in-memory rating bonus — never persisted.

Another now-or-never: every evening after this ships without the columns is
an evening of assists gone for good, same shape as ask 13's `first_seen`
and ask 22's rating/points snapshot.

Fix: two columns, same scope as the existing kills/deaths (team kills
included, not filtered — matching `player_duel_stat`'s own scope, since
this table is a roll-up of it):

```sql
-- player_duel_total
headshots INT NOT NULL DEFAULT 0,
assists   INT NOT NULL DEFAULT 0,
```

Assist eligibility mirrors `EloRating`'s own assist-reward check (real
human, not the attacker or victim themselves) — kept as an independent
check in `DamageReport.cs` rather than a shared helper, since it's three
lines and the two modules otherwise have no reason to share code.

With this, `player_duel_total` is honestly the player's period summary
table, not just a duel roll-up — the comment on its `CREATE TABLE` says so
now.

## Teamkill/suicide: a scoreboard penalty and two new counters (2026-08-04)

Direct ask, not from OSWeb: old CS:Source took a player's kill count down by
one for a teamkill or a suicide. Three separate pieces, kept separate on
purpose after an explicit "don't touch the existing counter" instruction:

- **`TeamDamage.cs`** now decrements `ActionTrackingServices.MatchStats.Kills`
  by 1 (verified via `ilspycmd` against the real CounterStrikeSharp DLL, not
  guessed) for both a teamkill and a suicide. **Purely cosmetic, purely the
  native CS2 scoreboard** — this line never touches any OSBase-owned table.
  Suicide detection had to be added here too: `TeamDamage.cs` previously only
  handled friendly fire (attacker ≠ victim, same team); a suicide is either
  no attacker at all (world damage — fall, drowning) or attacker == victim
  (own grenade, the `kill` command), neither of which the old
  `IsValidFriendlyFire` check matched.
- **`player_duel_total` gained `teamkills`/`suicides`**, additive-only,
  explicitly *not* subtracted from `kills`/`deaths` — the user was clear
  that the existing counter must keep meaning exactly what it already
  means. A suicide increments the victim's own `suicides`; a teamkill
  increments the attacker's `teamkills`. Both gated by `statsGateOpen`,
  same as everything else here.
- Fixed in the same pass, found while touching the adjacent code: the
  retry-merge path for a failed `player_duel_total` flush only re-merged
  `kills`/`deaths` back into the pending counter, silently dropping
  `headshots`/`assists` from a failed batch whenever new activity had
  already recreated the same key in the meantime. Pre-existing, unrelated
  to this ask, fixed because it was the exact block being edited anyway.
- **Elo side** (rating penalty, not points — see `ELO-MODULE.md`'s
  `elo_bonus_event` section) is a separate, explicitly requested addition,
  not implied by the scoreboard/counter pieces above.

## `!elorank` / `!elotop` — the community's own chat commands (2026-07-22)

Distinct from the admin-facing `css_elo_top`/`css_elo_points_top` console
commands (`EloRating.cs`, unchanged) — these are HLstatsX-style commands
typed in all-chat, matching `TeamBets`' own `bet` command mechanism
(`EventPlayerChat`, plain text match, no `!`-trigger plugin needed since
the raw chat text already contains it).

Command **names are config values**, not hardcoded, specifically so they
can run as `!elorank`/`!elotop` through the 2026Q3 trial season — cs2rank
still owns the literal `!rank`/`!top` names until it unloads — and get
renamed in `elorating.cfg` on Oct 1 with no rebuild.

`!elorank` prints the caller's own stats, one `PrintToChat` call per line
(deliberately not one fused format string, so reordering which stat leads
is a cut-and-paste, not a rewrite — the owner's decision on lead stat is
still pending):

```
Din placering: #3/387
Poäng: 12 480          (Rating: 1 842)
Kills: 412 (Headshots: 178) | Deaths: 355 | Assists: 96
Vunna rundor: 240 | Förlorade: 198
Headshot: 43.2% | KD: 1.16
Speltid denna period: 2 dagar, 4 timmar
```

Field sources:

| Field | Source |
|---|---|
| Placering #N/M | rank + count over `elo_points WHERE season=?` |
| Poäng | `elo_points.points` |
| Rating | `elo_rating.rating` (via `TryGetRating`) |
| Kills/Headshots/Deaths/Assists | `player_duel_total` (ask 24) |
| Vunna/Förlorade rundor | `SUM(player_round_stat.rounds_won)` / `SUM(rounds) - SUM(rounds_won)`, summed across every side+map for the season |
| Speltid | `SUM(player_daily_stat.seconds)` within the season's calendar-day range |

`!elotop` is name + points only, top 10, same query `css_elo_points_top`
already runs — just triggered by chat text instead of a console command.
Confirmed before building: `elo_points` already has a `name` column (it
was added alongside `elo_rating.name` from the start), so no new column
was needed to print a leaderboard.

Both commands live in `EloRating.cs` because that's the module that
already straddles `elo_rating`/`elo_points`, and reads the three
`DamageReport`-owned tables above with plain read-only SQL on its own
`Database` instance — same "shared table, no RPC" pattern OSWeb already
uses against OSBase's tables. Nothing writes back the other way.

## Status: nothing deployed yet

Same situation as everything else in this document — see the
"what's deployed" answer given directly to OSWeb: nothing is committed or
released. All of asks 1-18 and 22 exist only as code in the working tree.

### The shape: weapon × hitgroup × direction

Three dimensions, counted per player:

- **weapon** (ak47, awp, knife, taser, …)
- **hitgroup** (head / neck / arms / chest / stomach / legs)
- **direction** — damage **dealt** and damage **received**

Received matters as much as dealt. "Where do I keep taking rounds when I peek"
is a question nobody can currently answer.

### Hits, not kills — this is the important one

A player dies maybe twenty times an evening but is **hit** hundreds of times.

Built from kill events, a body diagram is twenty dots and mostly noise. Built
from hit events it shows a pattern worth acting on. The same applies in the
other direction: "where do I land AK rounds versus AWP rounds" is a per-shot
question, not a per-kill one.

`player_hurt` in demos, `EventPlayerHurt` live in OSBase, and (if it were ever
needed) `attacked` lines under `mp_logdetail` all carry the same fields
(attacker, victim, weapon, hitgroup, damage) — so the same aggregate shape
serves whichever source feeds it. OSBase's live event is the one actually
used; see above.

### Storage: counters, not raw events

Raw per-shot rows run to millions in a year. Rolled-up counters are a few
hundred rows per person, and the counters are enough to draw the figure.

Keep raw events only if the point is to answer questions nobody has thought of
yet. Decide that deliberately; do not drift into it.

### A body diagram, not a map heatmap

A heatmap needs each map's `pos_x`, `pos_y` and `scale` to turn world
coordinates into pixels, and workshop maps often have neither radar nor overview
file. It would work on some maps and silently fail on others.

One silhouette, drawn in-house as SVG, works for every map forever and raises no
licence question. Start there.

### The player-count sampler, and its trap

`server_connection` is a live snapshot — the row disappears on disconnect — so
there is nothing to plot over time. The missing piece is a sampler
(host, port, stamp, players).

**Three states, not two: a count, a genuine zero, and no data.** During a CS2
update window every source that lives on the game server goes quiet at once.
Recording that as `0 players` would bake a fake collapse into the trend line
permanently, and there is no way to tell it from a real empty evening
afterwards. Model "unknown" explicitly from the first row.

### The API rollup

`api_call` is pruned at 90 days, which is why the usage page deliberately stops
there. A year button today would draw an empty chart and call it history. Needs
a monthly summary written **before** the prune runs.

## Suggested order

1. ~~Throw the cheap switches now — `mp_logdetail` on.~~ **Retracted.** Not
   needed: OSBase's `DamageReport` already gets the same fields live, per
   shot, with no cvar. (Not `tv_autorecord` regardless. See above.)
2. **Stop discarding kill lines** in `ServerConnectionTracker`. Free, immediate,
   no config change. Still open.
3. **The sampler**, with the three states designed in from the start. Still open.
4. ~~The hit aggregate (weapon × hitgroup × direction counters)~~ **Done** —
   `player_hit_stat`, written by `DamageReport`. See above.
5. **The body diagram** over those counters. Still open — the data it needs
   now exists and is accumulating; this is drawing the SVG on the site side.
6. **The API rollup**, before another 90 days of history is pruned. Still open.
7. **Demos** last, and scoped: pick one era, one question (LAN placements is the
   highest-value one), and size the file count first. Still open.

## House rules that apply here

- **Migrations self-apply.** Add `database/migrations/NNNN.sql`; the web
  bootstrap runs pending ones. Do not hand-edit schema. See
  `src/Database/Migrator.php`.
- **Nothing is deleted.** Content is soft-deleted and restorable; only a GDPR
  erasure removes personal data. A stats aggregate keyed to a player is personal
  data — it has to be reachable by the erasure path.
- **Free/open-source only.** Every dependency and asset must be free to use with
  its licence shipped alongside. This already killed MaxMind's GeoLite2 (the
  EULA needs an account and forbids redistribution); DB-IP City Lite under
  CC BY 4.0 was used instead. A Go or Rust demo parser must clear the same bar.
- PHP 8.3. Use `php8.3` explicitly — the default `php` on the dev box is broken.

