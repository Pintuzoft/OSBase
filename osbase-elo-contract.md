# OSBase Elo ↔ OSWeb: the match-window contract

*OSBase keeps its own copy of the agreement in `ELO-MODULE.md`. This is OSWeb's
side. Two documents describing one thing is how the erasure lists came to differ
without either party noticing — so when this changes, say so across, and when
theirs changes, ask. Companion documents: `osbase-stat-contracts.md` (clutch,
multikill, the teambet log, knife/taser kills, and the round-end write delay) and
`osbase-demo-backfill.md`.*

What the Elo module reads from the site, confirmed against the real schema.
The module's own `ELO-MODULE.md` flagged these names as guesses made without
access to this repo — this is the confirmation, with the corrections.

## The table is `tournament_match`

Live in OSWeb as of migration 0158. Relevant columns:

| Column | Type | Notes |
|---|---|---|
| `id` | INT PK | **The match id.** Not `match_id` — that was the guess |
| `server_address` | VARCHAR(100) | ✅ guessed right. Nullable: a match with no server assigned yet |
| `server_name` | VARCHAR(100) | Display label, not an address. Do not match on it |
| `starts_at` | INT NULL | ✅ added by 0158. Unix seconds. NULL = not started |
| `ends_at` | INT NULL | ✅ added by 0158. Unix seconds. NULL = not finished |
| `tournament_id` | INT | Which tournament, if the module wants to scope a ladder |
| `home_team_id` / `away_team_id` | INT | Teams, if rating should ever be team-aware |
| `verified` | TINYINT | An admin confirmed the reported score. **Not** a match-live signal |

Indexed as `(server_address, starts_at, ends_at)` for exactly the lookup below.

## Deciding whether a kill counts

```sql
SELECT id FROM tournament_match
WHERE server_address = ?          -- this server
  AND starts_at IS NOT NULL
  AND starts_at <= ?              -- now
  AND (ends_at IS NULL OR ends_at >= ?)
ORDER BY starts_at DESC
LIMIT 1
```

No row → not a real match → **stay silent**: no rating change, no writes.

Two properties worth keeping:

- **NULL `starts_at` is the safe default.** Every match that predates 0158 has
  it, so nothing is ever rated retroactively by accident.
- **An open `ends_at` is deliberate.** A match that was started and never
  stopped keeps counting, which is a visible bug. The alternative — expiring it
  after N minutes — silently drops the end of a long overtime, which is not.

## `css_elo_match_start` / `_stop` write THIS row

Not a separate truth. The command sets `starts_at` / `ends_at` on the site's
row, so the site and the server can never hold different opinions about whether
a match is live. The site UI writes the same two columns.

## Address matching

`server_address` is whatever the admin typed when assigning the server, so it
may be a DNS name or an IP, with or without a port.

OSWeb already solved this: `ServerCredentials::canonicalHost` plus the cached
`HostResolver` collapse a DNS name and its IP to one key. **Match on the
canonical form, not on the string.** A match assigned to `cs.oldswedes.se` and
a server reporting its resolved IP are the same machine, and a literal comparison
would rate neither.

## Do not write to `player_kill_stat`

That table is OSWeb's, filled by `ServerKillTracker` from the log stream —
per-weapon kills, deaths and headshots per SteamID64. It is the "separate
per-weapon statistics" half of the Elo design decision, and it is already
running.

If OSBase ever persists kills as well, **it must write its own tables.** Two
writers on one counter double-counts silently, and a doubled kill count looks
exactly like a good evening.

## SteamID format — verified, not assumed

`VARCHAR(32)` holding **raw base-10 digits** — seventeen of them, e.g.
`"7656119XXXXXXXXXX"` written out in full, with no separators and no prefix.

Every write in OSBase goes through `ulong.ToString()` on the player's SteamID,
checked against a non-invariant culture as well so no thousands separator can
appear. Never `STEAM_1:0:…`, never `[U:1:…]`, no separators, no leading zeros —
a Steam64 in the valid range is always 17 digits, so nothing truncates against
VARCHAR(32) either.

So OSWeb can read and delete with `(string) $steamId64` straight off its own
BIGINT column, with no conversion in between.

## Personal data

*Settled 2026-08-03. This section used to end with an open question — whether
OSBase exposes a delete or OSWeb deletes directly. It is the second: OSWeb
writes to the OSBase database from `OsbaseStatsRepository`, which needs DELETE
and now UPDATE granted on it.*

`elo_rating`, `elo_points` and `elo_kill_event` are keyed to a SteamID, which
makes them personal data under the same rules as everything else here. But they
are not erased the same way, and the split is the point:

**`elo_rating` and `elo_points` are deleted.** They describe one person.

**`elo_kill_event` is anonymised, not deleted.** The leaving person's SteamID is
replaced with the `'ANONYMIZED'` sentinel; the row stays. **And the nick goes
with it** — this table stores `attacker` / `victim` name columns beside each id,
which we did not know until OSBase wrote out its own erasure code. On a site
where everyone knows each other a nick identifies a person at least as well as
the number does, so both are set in the same statement: a window where the id is
gone and the name is still there is not an erasure. Same treatment as
`player_duel_stat` and `knife_taser_kill_event`, and for the same reason: a kill
event names two people. Deleting the row because one of them asked to be
forgotten also destroys the *other* player's history — their kills, their
nemesis, the ledger their own rating was computed from. That person never asked
for it and would never be told.

Anonymisation satisfies the request without that cost: the leaver becomes
unidentifiable, everyone else's record stands. It also avoids having to defend
the "compelling legitimate grounds" balance that Article 21 demands if the data
were simply kept — which is where a hobby community's leaderboards are on
weaker ground than, say, a ban list.

Two properties the sentinel must have, and they are not negotiable:

- **No way back.** No hash of the SteamID, no retained mapping, no "these were
  erased" table. Any of those is pseudonymisation, not anonymisation, and then
  nothing has actually been erased — only made inconvenient.
- **Everyone collapses into one.** All erased players share the sentinel. Being
  unable to tell them apart is the goal, not a defect. OSWeb filters the
  sentinel out of nemesis and rankings while still counting it in totals: a
  player's kills are their kills, but a place in a ranking is a name. In the
  duel lists the row stays and reads **"[okänd spelare]"** — dropping it would
  change somebody else's numbers because a third person left.

**Everything else is still deleted outright**, including the two tables built on
2026-08-03: `player_teambet_log` describes one person betting, and belongs with
the counters rather than with the duels. Our own list was missing it, and
`knife_taser_kill_event`, until this was written — see the commit for how that
kept happening.

## Rebuilding the rating from the ledger

**Answered 2026-08-05, from the code, not a guess: today, rating only
accumulates forward. No rebuild-from-ledger path exists.**

`EloRating.cs`'s only seed path (`SeedRating`, called once per player per
session before their first duel) reads the single current-state row —
`SELECT rating, matches FROM elo_rating WHERE steamid64=@id` — straight
into the in-memory `liveRating`/`liveMatches` cache, falling back to
`start_rating` if no row exists yet. That's the entire seeding story.
`elo_kill_event` is never read back by the module for anything — it's
write-only, an append-only ledger kept *for* a future rebuild, not one the
module can currently perform on itself. Grepped for any
rebuild/replay/recompute code path: none exists.

**Consequence for the demo backfill** (see `osbase-demo-backfill.md`):
a one-time chronological import is possible *today*, with no new engine
work, as long as it happens as a clean historical replay before any live
scoring starts for those players — the module has no notion of "source",
it just applies whatever kill event it's handed, in whatever order it's
handed, straight onto the current in-memory number. That satisfies the
demo-backfill doc's own "backfill before, or as part of, the Elo relaunch"
path directly.

**What does *not* exist today, and would need new code:** the demo-backfill
doc separately requires re-running to be safe ("assume the backfill will
run more than once... must be possible to remove an imported range and
rebuild it, not only to append"). That's a genuine rebuild-from-scratch
capability — reset a rating to a known baseline, then deterministically
replay a full ordered ledger (demo-imported plus everything live since) —
and nothing like it exists in `EloRating.cs` now. Building it is a real
scoping question (does the module gain a `RebuildFromLedger()` path that
re-derives `elo_rating`/`elo_points` by replaying `elo_kill_event` end to
end?), not a config flip. Flagging back to whoever scopes the backfill
work rather than guessing at a design here.

### The scoping answer: keep it before launch and most of that cost disappears

*Written by the site side 2026-08-03, answering the question OSBase handed back.*

`RebuildFromLedger()` as described — reset to a baseline, replay demo-imported
and live events interleaved in order — is only necessary if the backfill runs
**after** live scoring has started. Before it, there is no live rating for those
historical periods worth preserving, and "re-runnable" collapses into three
things that mostly already exist:

- each imported row carries a demo identifier, **which the brief already
  requires** for a different reason (so a re-run replaces rather than doubles);
- a correction is therefore *delete what this import wrote, re-import*;
- and `elo_rating` is re-derived by running the import again from empty — which
  is not a rebuild engine, it is the import running a second time.

Nothing has to reconcile imported history against live play, because there is no
live play yet. That is the whole difference between a script and a redesign.

**So the ordering argument and the cost argument point the same way**, which is
worth stating plainly because they were arrived at separately. The brief wanted
the backfill before the relaunch so nobody watches a rating change under them.
The code says the cheap path is only available before the relaunch. The moment
the backfill slips past launch, the expensive version becomes the only one that
works — so "before" is not a preference to trade away under time pressure. It is
the thing keeping the work small.

**One caveat that is ours, not OSBase's.** This assumes nothing on the site
reads Elo ratings as a live number during the backfill window. Today nothing
does — the module is not deployed. If that changes before the backfill runs,
this scoping is void and the expensive version is back.

**A second, separate gap found checking this scoping against the code —
2026-08-05, OSBase side:** the demo-backfill brief's "one architectural
requirement" is to feed the same event handlers from two sources. Read
`EloRating.cs`'s `OnPlayerDeath` end to end to check whether that's already
true. It is not. The signature is `OnPlayerDeath(EventPlayerDeath
eventInfo)`, and every value it uses — `eventInfo.Attacker`/`.Userid` as
live `CCSPlayerController` objects, `.TeamNum`, `.PlayerName`,
`eventInfo.Headshot`, `eventInfo.Assister`, `eventInfo.Weapon`,
`osbase.currentMap`/`Server.MapName` — only exists on a running CS2 server
with real connected players. A demo parser produces plain values (a
SteamID, a name, a team number, a headshot bool, a weapon string, a
timestamp) from a `.dem` file offline; it cannot construct a
`CCSPlayerController` or an `EventPlayerDeath`, so nothing can call this
method with demo-derived data as it's written today.

This is smaller than `RebuildFromLedger()` — no new algorithm, the Elo math
itself (expected score, K-factor, deltas, headshot bonus) is already
plain arithmetic over plain numbers — but it is real, separate,
unscoped work: extracting that arithmetic into a plain-data function (say
`ScoreKill(attackerId, attackerName, attackerTeam, victimId, victimName,
victimTeam, headshot, assisterId, assisterName, weapon, mapName, matchId,
stamp)`) that `OnPlayerDeath` calls with values pulled off the live event,
and that a demo-replay driver could call identically with values parsed
from a `.dem` file. Same shape of problem almost certainly exists in
`DamageReport.cs`'s `OnPlayerHurt`/`OnPlayerDeath`/`OnRoundEnd` (hits,
damage, clutches, multikills) — not checked line-by-line here, flagging
by strong analogy rather than claiming it as verified the way the
`EloRating.cs` finding above is.

## Hitgroups: OSBase wins, and `mp_logdetail` is off the table

`docs/STATS-MODULE.md` said the body diagram needed `mp_logdetail`, because the
log carries no hitgroup lines without it. That was true of the log and wrong as
advice: **`DamageReport.cs` already captures weapon, hitgroup, damage and
direction live** via `EventPlayerHurt`. It just never persists any of it —
it writes to chat and drops it at round end.

So the body diagram is not a capture problem, only a persistence one, and the
OSBase route is strictly better than the cvar: per shot, structured, no log
parsing, and none of the log volume `mp_logdetail` would add.

Shape to persist — counters, not raw events (millions of rows a year otherwise):

```
steamid64 × weapon × hitgroup × direction(dealt|received) -> hits, damage
```


## The weapon weights are not being applied — measured, not suspected

*Written 2026-08-16 from the live ledger, after the owner asked whether the Elo
data looked right. This is the one thing in it that is measurably broken.*

Migration 0243 created `weapon_point_weight` in the **OSWeb** database and
settled who owns what: the site owns the values so a HeadAdmin can retune them
without touching the plugin or restarting a server, and **OSBase multiplies the
weight into the points at the moment of the kill** — because the number is
already in the right place there, and recomputing it afterwards would be
throwing away a correct value to rebuild it from notes.

The table holds 47 rules. The plugin behaves as if it held none.

### What the ledger says

Measured over `elo_kill_event` for 2026-08-07 11:57 → 2026-08-15 21:48 — 7 969
kills, every one of them, no sampling. Mean `attacker_points_delta` per weapon,
against the multiplier the site would have handed out:

| Weapon | Kills | Mean points | Site weight | Expected |
|---|---|---|---|---|
| `ak47` | 2 064 | 9.74 | 1.00 | 9.88 |
| `awp` | 135 | 9.59 | 1.00 | 9.88 |
| `deagle` | 599 | 9.71 | 1.20 | 11.86 |
| `hegrenade` | 75 | 10.23 | 1.80 | 17.78 |
| `taser` | 27 | 9.68 | 1.80 | 17.78 |
| `knife` | 16 | 10.73 | 2.00 | 19.76 |
| `knife_t` | 12 | 10.60 | 2.00 | 19.76 |
| `smokegrenade` | 1 | 10.29 | 5.00 | 49.40 |

Every weapon in the game pays the same. A knife kill is worth an AK kill, a
Zeus is worth an AK, and the one smoke-grenade kill of the season — the row the
curiosity class exists for — paid 10.29 instead of 49.40.

### Why this cannot be "applied, but not logged"

The obvious defence is that the weight goes into the in-memory accumulator while
the ledger records the raw value. It does not, and the ledger proves it from the
other end: **`SUM(attacker_points_delta)` plus the bonus rows equals
`elo_points` exactly for all 87 players**, to the öre, and
`1000 + SUM(deltas)` equals `elo_rating` to four decimals for all 87. The
accumulator *is* the sum of the ledger. There is nowhere else for a multiplier
to have been applied.

That property is worth keeping deliberately, by the way — it is what makes the
rebuild-from-ledger path in the section above real rather than aspirational, and
it is how this bug was found in an afternoon. **When the weight lands, it must
land in `attacker_points_delta` too**, not only in the accumulator.

### The two formulas, confirmed from the data

Nothing here needs changing — recorded because they were derived rather than
documented, and the weight has to slot into the first one:

```
points = 10 · clamp(victim_rating / attacker_rating, 0.5, 2.0) · weapon_weight
rating = K · (1 − expected) · (headshot ? 1.2 : 1),  K = 50 provisional, else 32
```

**The weight multiplies the clamped ratio; it does not go inside the clamp.**
Written out because the two readings differ exactly where the weights matter
most: fold the weight in and a knife kill against a much stronger opponent hits
the 2.0 ceiling and the weapon stops counting at all — the curiosity class at
5.00 would never once pay 5.00. The clamp bounds how much *the opponent* can be
worth. It has nothing to say about the weapon.

The points formula fits all 7 969 kills with a mean error of 0.000. The weapon
weight is the only factor missing from it. **Rating must stay unweighted** — the
weights are for the seasonal points, and a rating that moved by weapon would
stop answering "how good is this player".

### Matching rules, which are load-bearing

Read `weapon_point_weight (pattern, match_type, multiplier)` and resolve in this
order, first hit wins, default `1.00`:

1. `exact` — `weapon = pattern`
2. `prefix` — `weapon` starts with `pattern`
3. `suffix` — `weapon` ends with `pattern`

**The order is not a convenience.** `knife` has an exact row and `knife_` a
prefix row; read prefix-first and every knife skin still resolves, but a future
row saying `knife_t` is worth something other than 2.00 silently stops applying.
`WeaponWeightRepository::ruleFor()` is the reference implementation, and the
admin page runs the same lookup so a weapon name can be tested against the rules
before anyone plays a round on them.

### What it costs today

Applying the table to the season as played would have moved the kill points from
78 738 to 89 836 — **+14.1 %**, distributed towards exactly the play the weights
exist to reward. Not evenly: knife and Zeus rounds, the grenade kills, the
curiosities.

Two questions went back to OSBase, and both came back the same day.

**1. Never implemented, rather than implemented and failing.** `EloRating.cs` carries
comments at lines 604-605, 636-639 and 970-972 saying "once weapon-weighting
lands" and pointing at the HLstatsX backlog. There is no lookup to fix; there is
a feature to write. The schema was already prepared for it — `victim_active_weapon`
and `victim_best_weapon` were added 2026-08-04 — and the weapon name is already
normalised and on the row (`EloRating.cs:1026`), so the multiplier has a place to
go. Their side of the formula, confirmed verbatim:

```
ratio      = clamp(victimRating / attackerRating, 0.5, 2.0)   // pointsRatioMin/Max
killPoints = round(pointsPerKill * ratio, 2)                  // pointsPerKill = 10
```

Note the **clamp**, which the measurement could not see because no duel in the
season came near it: a 2 500 against a 1 000 pays 5.0 rather than 4.0, and the
reverse pays 20 rather than 25. Ours to know about, not to change.

**2. Yes, into `attacker_points_delta`** — the value written to the ledger is the
value added, and it must stay that way.

### And a correction back the other way

The section above said `weapon_point_weight` lives "in the **OSWeb** database" in
a way that reads as a second server to cross to. OSBase answered that it is one
shared schema and no RPC is needed. Half right, and the half that is wrong
matters at the keyboard:

**Same MySQL server, two separate schemas.** OSWeb's is `oldswedes`
(`newswedes` in dev); OSBase's is `osbase` (`osbase_dev` in dev) — which is why
OSWeb keeps a second connection for it at all (`config/osbase.php`,
`OsbaseDatabase`), and why the prompt over the ledger dumps reads
`MariaDB [osbase]>`. An unqualified `SELECT … FROM weapon_point_weight` from the
`osbase` schema finds nothing.

So the operational conclusion is right — no RPC, one connection is enough — but
it needs a **schema-qualified name and a `SELECT` grant**, and the schema name
must come from config rather than a literal, because dev and prod do not agree on
it.

**The same question applies to `tournament_match`, and nobody has answered it.**
That table is OSWeb-owned and read the same way. Across all 7 969 kills,
`match_id` is NULL every single time. That is fully explained without a bug —
the lookup only matches a *started* match, and none appears in the window, which
is what nine days of pub play looks like — so it is not evidence of anything
broken. But it does mean **the cross-schema read has never once been observed to
succeed.** It will be exercised for the first time on a tournament night, which
is the worst possible moment to find out a grant is missing. Worth proving
deliberately before then, with one started match and one kill.

### One more requirement, which the ownership split implies

The point of the site owning the values is that a HeadAdmin retunes them "without
touching the plugin or restarting a server" (migration 0243). A weight read once
at plugin load breaks that promise quietly: the admin page saves, the table
changes, and the servers keep paying the old rate until someone happens to
restart them. **Re-read on a timer** — a cached read with a short TTL is plenty;
this is a 47-row table.

### How the site will check that it landed

Written down before the work starts, so the target is not a matter of opinion
afterwards. It is the same measurement that found the problem, run again against
a day of real play — no test server needed, and no cooperation required beyond
the ledger already being written.

1. **A knife kill pays about twice a rifle kill at the same rating ratio.** The
   ratio has to be held roughly equal for the comparison to mean anything, since
   it moves the points by ±2× on its own. Grouped by weapon over a day's kills,
   the means should land near `10 · ratio · weight` — knife and bayonet at 2.00,
   Zeus and grenades at 1.80, the curiosity class at 5.00, everything else at
   1.00.
2. **`SUM(attacker_points_delta)` plus the bonus rows still equals `elo_points`
   exactly.** This is the one that matters most, and it is not about weapons at
   all: if it stops holding, the weight went into the accumulator but not the
   ledger, and the rebuild-from-ledger path quietly stopped being real. Four
   decimals, all players, no tolerance.
3. **`attacker_delta` is unchanged in character** — no weapon term anywhere in
   the rating. The implied `K · (1 − expected)` should still come out at exactly
   32, or 50 inside the provisional window, for every weapon in the game. That
   is how the current data reads, and it is how it should still read.

A fourth, cheaper than all of them: change one multiplier on `/admin/vapenvikter`
and watch the next kill with that weapon pay the new rate, without anybody
restarting a server. That is the whole reason the values live on the site, and it
is the one property a code review cannot confirm.

### When to land it

Nothing needs recomputing for the current season: the points are internally
consistent, merely unweighted, and since every raw event is on the ledger the
season *could* be replayed with weights if the owner wants it.

But the cheap moment is now, and for the same reason the backfill section above
argues for "before launch": the ladder is not public until 1 October. Landing the
weights before then changes numbers nobody has seen. Landing them after moves
every member's season total by an average of 14 % under their feet, in a table
they have started to care about.
