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
a server reporting `10.0.9.20` are the same machine, and a literal comparison
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

`VARCHAR(32)` holding **raw base-10 digits**: `"76561197960287930"`.

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

