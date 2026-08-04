# Backfilling the stats from archived demos

A brief for the OSBase side, written 2026-08-03. Separate from
`osbase-stat-contracts.md` because it is a bigger piece of work than the four
tables in there, and because it can be handed over on its own once the rest has
landed.

## Why

The community has played since 2009 and the archive of demos goes back years. A
rating and a set of leaderboards that begin on the day the module ships describe
nothing — they say who has played *recently*, which is not what anyone means by
"the stats". Replaying the demos turns a fresh install into a record.

## The one architectural requirement

**Feed the same event handlers from two sources.** The live server emits
`player_death`, `round_end` and the rest; a demo parser emits the same events out
of a file. One implementation of every definition, two inputs.

The tempting alternative is a standalone tool that reads demos and counts kills
and headshots. It gives two implementations of the same rules, and they drift —
not maybe, but the first time somebody fixes a bug on one side. The result is a
history where 2019 counts headshots one way and 2026 another, and the seam is
invisible: every number looks plausible the whole way through.

This is also why it belongs on the OSBase side rather than anywhere else. Not
workload — OSBase already holds the definitions in working form, and a definition
fixed later is then fixed for both at once. Re-scan the demos and the history
follows, which is only true if there is one set of rules.

### The requirement above is not met today, and that is the real scope

*Checked by OSBase 2026-08-03, after this brief stated it as a principle without
anyone verifying it was reachable.*

`OnPlayerDeath` takes an `EventPlayerDeath` and reads everything off live
`CCSPlayerController` objects — `.TeamNum`, `.PlayerName`, `.Headshot`,
`.Assister`, `.Weapon`, plus `osbase.currentMap`. None of that exists when the
input is a `.dem` file being parsed offline, so **nothing can call this handler
with demo-derived data.** The requirement at the top of this section describes an
architecture the module does not currently have.

The fix is small and well-understood: extract the decision — which is plain
arithmetic in the Elo case — into a function taking plain data, and let both the
live hook and a demo driver call it. No new algorithm, no rewrite. An event
adapter separated from the rule it feeds.

**But it is not one refactor, and that is the part worth pricing before anyone
estimates.** This brief asks the backfill to reconstruct *every* stat, so every
handler behind a table it must fill has to take the same shape: kills and
headshots, clutches, multikills, duels, knife and taser kills, the daily
aggregates. Elo was read line by line; `DamageReport.cs` was flagged as
"almost certainly the same shape, by analogy, not verified". Before scoping,
**enumerate them** — the number of handlers is the size of the job, and one
verified example plus an analogy is not that number.

**Enumerated 2026-08-05, by reading every subscribed handler in both
modules, not by analogy this time: nine.**

| Module | Handler | Feeds |
|---|---|---|
| `DamageReport.cs` | `OnPlayerHurt` | `player_hit_stat` |
| `DamageReport.cs` | `OnPlayerDeath` | `player_duel_stat`, `player_duel_total`, `knife_taser_kill_event`, clutch/multikill round-state, `player_daily_stat` (kills/headshots) |
| `DamageReport.cs` | `OnWeaponFire` | `player_weapon_shots` |
| `DamageReport.cs` | `OnBombPlanted` | `player_round_stat` (bomb_plants) |
| `DamageReport.cs` | `OnBombDefused` | `player_round_stat` (bomb_defuses) |
| `DamageReport.cs` | `OnBombBeginDefuse` | round-state feeding `player_round_stat` (defuse_fails) |
| `DamageReport.cs` | `OnRoundEnd` | `player_round_stat` (rounds/rounds_won), `player_daily_stat` (rounds/seconds/rating snapshot), `server_stat_season`, resolves clutch/multikill into `player_clutch_stat`/`player_multikill_stat` |
| `EloRating.cs` | `OnPlayerDeath` | `elo_rating`, `elo_points`, `elo_kill_event` (the one already verified) |
| `EloRating.cs` | `OnRoundEnd` | `elo_points` (round-win points) |

Every one of the nine reads its data off live `CCSPlayerController`/
`EventX` objects the same way the verified `OnPlayerDeath` case does
(`.TeamNum`, `.PlayerName`, weapon/hitgroup fields, `Utilities.GetPlayers()`
inside `DamageReport.cs`'s clutch check) — checked per handler, not assumed
from the first one. Nine extractions, not one, is the actual size of "feed
the same event handlers from two sources."

**Two more methods gate the above rather than fill a table, and need a
different kind of fix, not extraction:** `DamageReport.OnRoundStart` and
`EloRating.OnRoundStart` decide `statsGateOpen` (no bots, no warmup, a
connected-humans minimum) from live server state
(`Utilities.GetPlayers()`). A demo replay needs its own equivalent decision
per round (a demo has warmup periods and a player list too) — not a
plain-data function to extract, but a rule to define for what "gate open"
means when replaying a file instead of watching a live server.

**Explicitly out of this count, and worth saying why:** `TeamBets.cs`'s
handlers aren't included — a demo has no record of who bet what, so
`player_teambet_log`/`player_teambet_stat` cannot be backfilled from
replay at all, by nature of what a demo contains, not as a gap to close.
`GameStats.cs`/`skill_log` also excluded — it's being superseded by Elo,
not carried forward, so extending it for a backfill would be investment in
a table on its way out.

### Three consequences of that enumeration, from the site side

*Added 2026-08-03, after OSBase produced the count. None of these are visible
from the plugin side, which is why they are written here rather than assumed.*

**1. Nine correct extractions do not make a correct replay.** Row 7 snapshots a
`rating` into `player_daily_stat`, and that rating is what row 8 just finished
computing. So the demo driver has to invoke the handlers in the same relative
order the live event bus does — *across* modules, not only within one. Nine pure
functions that are each right can still produce a wrong day if the snapshot runs
before the kills it should reflect. Worth naming because "extract nine
functions" sounds like nine independent jobs, and the composition is a tenth.

*Confirmed 2026-08-05, OSBase side, against `EventBusHandler.DispatchToEventBus`
rather than taken on trust: it synchronously invokes every subscriber for one
event, in a plain `foreach`, before the caller moves on to the next event — no
subscriber runs concurrently with another, and nothing about one event overlaps
the next. That's exactly the discipline the demo driver has to replicate: one
event, dispatched to every relevant handler across every module, fully
completed, before advancing to the next chronological event. Not "run
`DamageReport`'s nine handlers over the whole file, then `EloRating`'s" — that
ordering would have `player_daily_stat`'s rating snapshot read a rating from
the wrong point in time on every single row, silently.*

**2. The gate is the most likely first failure of the verification test, and it
will look like a definitions bug.** `OnRoundStart`'s "gate open" rule has to be
re-decided for replay, and if the demo-side version differs even slightly —
counting a warmup round, a different connected-humans threshold — then every
number comes out wrong together, uniformly, in a way that reads as "the parser
counts kills differently". It is not. Check the round *count* matches before
comparing anything else; that isolates the gate from the definitions and turns a
confusing failure into a one-line one.

**3. Retiring `skill_log` costs us something with no successor, and it is not
the profile curve.** GameStats being superseded by Elo is the right call and we
agree with it — but `skill_log` feeds two things here, and only one has a
replacement:

- the profile's form curve → already replaced by `RatingRepository::dailyHistory`
  reading `player_daily_stat`, built for exactly this (ask 22). Fine.
- **tournament team balancing** → `SkillRepository::recentAverageBySteamId64`,
  called by `TournamentAdminController` to seed fair teams at a LAN. There is no
  equivalent on `RatingRepository`. When `skill_log` stops being written, the
  balance button quietly has nothing to balance on.

Not a request to keep GameStats alive. It is a request that the Elo side grow a
bulk "recent rating for these SteamIDs" lookup before `skill_log` goes cold —
`elo_rating` already holds the number, it just has no batch reader. Cheap now,
and the alternative is finding out at a LAN with everyone waiting.

Two things make this worth doing even if the backfill were cancelled tomorrow:

- **The definitions become testable at all.** Right now a rule can only be
  exercised by running a server and killing someone on it. That is why this
  project needed a whole contract document to pin down what `headshots` means —
  the definitions were not reachable from a test, so they were reachable only
  from an argument.
- **It is the boundary the verification below actually needs.** "Scan a demo the
  live module already recorded and require an exact match" compares two paths
  through the same function once this exists, rather than two implementations
  that merely ought to agree.

## Prove it before trusting it

**Scan a recent demo the live module already recorded, and require the two to
match exactly.**

This is the whole verification, and it is available for free: pick a match from
last week, run the parser over its demo, and compare every number against what
the plugin wrote live — kills, headshots, rounds, multikills, the lot. If they
agree, the parser implements the same definitions. If they do not, the difference
is a bug found against a known answer rather than a discrepancy discovered in
2027 in data nobody can check.

Do this on several matches, including a messy one: a match with a disconnect, a
surrendered round, a knife round. The clean matches agree easily; the awkward
ones are where two implementations part company.

Until that test passes, the backfill is a guess about the past.

## Elo is order-dependent, so the order of operations is not free

A rating is a sequence — each kill moves two numbers, and the result depends on
the order events are processed in. Importing old demos *after* live rating has
been running produces a number that is not what it would have been: the rating
"as if every match from 2019 were played last night".

The only correct path is a full recompute in chronological order: import the demo
events with their real timestamps, then rebuild the rating history from the whole
ledger. `elo_kill_event` already **is** that ledger, which is the good news.

**Answered 2026-08-03 by OSBase, out of `EloRating.cs` rather than memory: there
is no rebuild. Rating only accumulates forward, and `elo_kill_event` is written
but never read back.**

Practical consequence, and it is now sharper than when this was a preference:
**backfill before, or as part of, the Elo relaunch.**

Doing it afterwards forces a recompute that does not exist and would have to be
built — reset a rating to a baseline, then replay imported and live events
interleaved in order. Doing it *before* needs none of that: there is no live
rating to reconcile against, so a correction is "delete what this import wrote
and import again", and `elo_rating` falls out of running the import from empty.

So the two arguments for going first are independent and agree. People should
not watch a rating change under them — that is the one thing that makes players
stop believing the number. And the cheap path is only on the table before
launch. See `osbase-elo-contract.md` for the full scoping.

## Re-running has to be safe

Assume the backfill will run more than once: a definition gets corrected, a batch
of demos turns up, a bug is found by the test above. That means it must be
possible to **remove an imported range and rebuild it**, not only to append.

Concretely, each imported row needs to carry enough to identify where it came
from — a demo identifier, or at minimum match and timestamp — so a re-run can
replace rather than double. A backfill that can only be run once is a backfill
that will be run once, wrongly, and left.

## What the demos have to be matched against

A demo knows what happened; it does not necessarily know which of your servers it
happened on, or which season it belongs to. Before importing, decide how a demo
maps to `match_id`, to a server, and to a season, and what happens when it maps
to none of them — old demos from a server that no longer exists are exactly the
ones worth keeping.

Two more that are easy to get wrong and hard to notice afterwards:

- **Warmup and knife rounds.** They are in the demo. They are not in the stats.
- **Partial and corrupt demos.** A file that ends mid-match should import the
  rounds it has or none at all — but it has to be a decision, not whatever the
  parser happens to do when it hits the end of the file.

## One decision that is free today and impossible later

Old demos contain the SteamIDs of everyone who played, including anyone who
erases themselves before the backfill runs. An import would re-introduce data an
erasure removed, and it cannot be filtered — erasure deliberately leaves no list
of who was erased.

Nobody has erased yet, so this costs nothing today. But the choice locks in at
the first request: if erasure leaves no trace, no later import can honour it.

The precedent already exists in OSWeb: `mail_suppression` keeps an address for
the sole purpose of never mailing it again. A one-way suppression list that only
lets an import skip somebody is a different thing from a mapping that
re-identifies them, and it is the ordinary way to keep a promise already made.

If that is wanted it belongs in the erasure path as a deliberate, documented
exception — added before the first request arrives rather than discovered after.
