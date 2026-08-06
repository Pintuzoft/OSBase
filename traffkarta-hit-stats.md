# Player stats collection — what OSBase should record

**RÄTTAT 2026-08-05:** skalan är CS2:s egna lagnummer, **2 = T, 3 = CT**. Texten nedan sa 0/1 och citerades vidare tills den lät som ett faktum — vår egen dev-seed skrev 0/1, så varje test hade rätt data för fel antagande. Ingen i kedjan läste någonsin en rad en modul skrivit. `bin/doctor.php` frågar numera tabellen i stället.

Written 2026-07-21, revised the same day once the real OSBase schema was read.
A handoff to the OSBase side (github.com/Pintuzoft/OSBase, C#) so the profile
features on OSWeb can be fed real data. **OSBase writes; OSWeb only reads.**

**Scope, so the size of this is clear.** It is not a side feature. The intent
(owner, 2026-07-21) is that OSBase's stats/ELO module becomes **the engine
behind the site's statistics** — profiles, the body-diagram heatmap, nemesis
lists, leaderboards — and that ELO covers *all* play rather than only tournament
matches. OSWeb's own log parsing (`ServerKillTracker` → `player_kill_stat`) is
an acknowledged stopgap and gets retired once these counters land. That is why
the dimensions below are worth arguing about now: they are the schema the whole
site's stats will be read through for years.

Supersedes the earlier draft of this file, which proposed a schema before the
existing one was known. Where the two differ, what is in OSBase wins — the notes
below are asks *on top of* what already exists.

## The one rule that shapes everything

**These are aggregate counters, and a missing dimension can never be recovered.**
Once hits or kills have been summed without `side` / `season` / `weapon`, there
is no way to split them apart afterwards. So the dimensions have to be in the
PRIMARY KEY *before* writing is switched on. Every evening without them is an
evening of history that does not exist.

This is the same rule `docs/STATS-MODULE.md` opens with, and it is the only
reason any of this is worth deciding now rather than later.

## What OSBase already has (confirmed, good)

Four OSBase-owned tables, all `CREATE TABLE IF NOT EXISTS` at module OnLoad,
InnoDB / utf8mb4_unicode_ci, `VARCHAR(32)` for every steamid (never BIGINT — a
Steam64 overflows JS's safe-integer range):

- **`player_hit_stat`** — `(steamid64, weapon, hitgroup, direction)` → `hits`,
  `damage`, `updated_at`. `direction` 0 = dealt, 1 = received. `hitgroup` 0–10
  (Body/Head/Chest/Stomach/L-Arm/R-Arm/L-Leg/R-Leg/Neck/U9/Gear).
- **`player_weapon_shots`** — `(steamid64, weapon)` → `shots`.
- **`elo_rating`**, **`elo_kill_event`** — the ELO module's state and duel log.

This already carries the body diagram's core: eight hitgroups **with left and
right kept apart** (which the heatmap needs — it shows left-vs-right asymmetry,
and merging arms/legs would make that permanently impossible), both directions,
per weapon, plus damage. And `player_weapon_shots` gives real accuracy
(`hits ÷ shots`) rather than an invented number. Nothing here needs changing.

## Asks

### 1. `player_hit_stat`: add `side` to the primary key

`side TINYINT` — CS2:s egna lagnummer: **2 = T, 3 = CT**. (0 = ej tilldelad, 1 = åskådare; ingen av dem skriver statistik.)

The profile shows a player's own operator with their hit zones on it, split by
the side they were playing. Without this column the T/CT choice is only a skin
picker, not two datasets.

### 2. `player_hit_stat`: add `season` to the primary key

`season VARCHAR(8)` — e.g. `2026Q3`, computed from the round's date at write
time.

For "this season vs last"; all-time is then just a SUM across seasons. Note the
hit data must **never be reset** — the quarterly reset is rank/ELO, a different
table. `season` here is only a filter, so nothing is ever lost.

### 3. A rounds counter (new) — for ADR

Something like `player_round_stat (steamid64, side, season)` → `rounds`.

`damage` is already recorded, but with no round count ADR (average damage per
round) — one of the most standard CS numbers — cannot be computed at all.

### 4. A duel counter (new) — for "nemesis"

```sql
player_duel_stat
  attackerid64  VARCHAR(32),
  victimid64    VARCHAR(32),
  attacker_side TINYINT,        -- 2 = T, 3 = CT (CS2:s lagnummer)
  victim_side   TINYINT,
  weapon        VARCHAR(32),
  kills         INT,
  headshots     INT,
  updated_at    DATETIME,
  PRIMARY KEY (attackerid64, victimid64, attacker_side, victim_side, weapon),
  KEY (victimid64),   -- "who kills me"
  KEY (attackerid64)  -- "who I kill"
```

This is the fun one: a top-10 of who kills you (with placing), broken down per
weapon — *"knifed by trewe ×12, 3rd place"* — per side, and the same data read
the other way for who you hunt. Both sides are stored rather than just the
victim's so either view can be filtered by *your* side; a teamkill falls out for
free as a row where both sides match.

**On `elo_kill_event`, and the ELO scope — decided, reversed, then decided
properly.** Worth reading the whole sequence, because the conclusion is the
opposite of what this file said for most of the day and the path matters.

1. An early draft proposed making `match_id` nullable so nemesis could be
   derived from `elo_kill_event`, asserting that ELO was meant to cover all
   play and marking it "confirmed with the owner". It was not confirmed. It read
   the owner's "OSBase drives the site's statistics" as a statement about ELO
   specifically, which it wasn't.
2. The OSBase side pushed back and was right to: `ELO-MODULE.md` gives real
   reasons for the tournament gate — pub play has bots, uneven teams and
   mid-round joins, and a rating assumes matched competition. The claim was
   withdrawn and the gating left alone.
3. **The owner has now decided it outright (2026-07-21): OSBase's ELO is meant
   to REPLACE cs2rank as the community's ranking.** That is not a tweak to a
   column; it settles what the number is for. A ranking that only counts
   tournament matches cannot replace the ladder people see every evening.

So ELO does need to cover pub play after all — but this is a decision arrived
at, not the earlier assertion being vindicated. The difference is that somebody
with the authority to make it has now made it.

**The fact that settles it** (owner, 2026-07-21): **oldswedes runs roughly one
tournament a year.** Neither side of this discussion had that when the gate was
defended. A tournament-scoped rating assumes tournaments happen often enough for
the number to mean something; at one a year it is not a ranking at all, it is an
annual event result — a number that changes once and then describes a member for
the following twelve months while they play several evenings a week. The gate
does not protect the rating's meaning. It starves it.

That is not the owner overruling the objection. It is the premise the objection
rested on turning out not to hold — `ELO-MODULE.md` reasons carefully about
matched competition and never asks how much matched competition there is.

**The rest of OSBase's objections still have to be answered rather than
overruled**, and one thing has changed since they were raised: ask 11 now
exists. Bots, warmup and
near-empty servers are already gated out of every other counter, and the same
gate can protect a rating. Uneven teams and mid-round joins remain genuinely
awkward for a rating that assumes matched competition — but HLstatsX ran exactly
this, on exactly these servers, for years, and the community remembers it
fondly enough to want it back. That is evidence, not proof, and it is the
owner's call to weigh.

**Two questions this opens that the schema cannot answer by itself:**

- **One rating or two?** If pub play and tournament play both move one
  `elo_rating`, that number means something different from what `ELO-MODULE.md`
  describes, and existing tournament ratings would start drifting on pub
  results. A second rating keeps both honest at the cost of explaining two
  numbers to members.
- **What happens to `skill_log`?** It is the owner's own performance estimate,
  written by GameStats after every map, and it is what the profile's form curve
  draws today. If ELO becomes the community's ranking, these two overlap — and
  the site should not show a member two competing "how good am I" numbers, for
  the same reason the log-parsed kill stopgap had to go.

Either way `player_duel_stat` stays the source for nemesis: it is unbound by
match scope by design, and deriving rivalries from a rating's event log would
tie them to whatever the rating decides to count.

So nemesis comes from `player_duel_stat` above, which is unbound by the
tournament window by design and needs no ELO change at all. `elo_kill_event`
remains what it is, and the counter table above is a requirement rather than an
optimisation. That is the cheaper shape anyway: a top-10 over years of a durable
event log is a GROUP BY across millions of rows, while the aggregate answers it
from a handful. The non-negotiable part was only ever that **side and weapon are
captured at write time**, because that is the part no later migration can
recover — and `player_duel_stat` does exactly that.

### 5. `player_duel_stat` needs `season` too — and it is the only one still missing

Added after the table was built (2026-07-21), so worth calling out separately:
`player_hit_stat` and `player_weapon_shots` both got `season`, but
`player_duel_stat` did not. It therefore has **no time dimension at all** — no
season, nothing to group by.

That blocks more than a filter. OSWeb now names a player's **nemesis** from
these counters, and without a time dimension the title is *lifetime*: whoever
owned you in 2019 keeps it forever, even if you have not met since. "Nemesis
this season" is the version people actually want, and it is the version that
cannot be added afterwards — same rule as everything else in this document. The
rows being written today are the ones that will be missing it.

`season VARCHAR(8)` in the PRIMARY KEY, computed at write time exactly as the
other two tables do it.

### 6. `player_duel_stat`: count HOW the kill happened, not just that it did

`EventPlayerDeath` already carries all of this and OSBase currently throws it
away. Each one is an `INT` counter beside `kills` and `headshots` — no new rows,
no new dimension in the key:

```sql
noscopes    INT,   -- noscope
wallbangs   INT,   -- penetrated > 0
blind_kills INT,   -- attackerblind  (they shot you while flashed)
smoke_kills INT,   -- thrusmoke
```

**Why this is worth a column each.** OSWeb's nemesis verdict weighs *how* someone
beats you, not only how often — a knife or taser counts for more than a rifle
kill because it stings more. A noscope belongs in exactly that company, and
right now the site cannot tell one from any other AWP kill. As always: these are
aggregates, so an evening recorded without them is an evening where it never
happened.

### 7. `dominated` and `revenge` — CS already has a nemesis mechanic

The same event carries `dominated` and `revenge`, which is Counter-Strike's own
built-in version of this idea: the game decides when someone is dominating you
and when you have taken revenge, and it has done so consistently for over a
decade. Two more counters:

```sql
dominations INT,   -- dominated
revenges    INT,   -- revenge
```

Worth having for two reasons. It is a rivalry signal that players already
recognise from in-game, so a profile agreeing with what the scoreboard told them
lands better than a number the site invented. And it is an independent check on
OSWeb's own weighting — if the site's computed nemesis is usually the same
person the game has been calling your dominator, the formula is sound; if they
disagree wildly, the formula needs work. That comparison is only possible if
both numbers exist.

### 8. Bomb work — plants, defuses, and the defuses that failed

Straight event counters, so they can ride along in `player_round_stat` (which
already has the right key):

```sql
bomb_plants   INT,   -- EventBombPlanted
bomb_defuses  INT,   -- EventBombDefused
defuse_fails  INT,   -- EventBombBeginDefuse with no defuse that round
```

**The failed defuses are the point.** Anyone can show plants and defuses; the
number people actually argue about is how often someone went for it and didn't
make it. That needs the *attempt* recorded, not just the success — `haskit` on
the begin-defuse event is worth keeping too, since going for a no-kit defuse is
a different decision from going for one with a kit.

### 9. Clutches — and why attempts matter more than wins

```sql
player_clutch_stat
  steamid64   VARCHAR(32),
  side        TINYINT,
  season      VARCHAR(8),
  opponents   TINYINT,     -- 1..5, the number alive against you
  attempts    INT,
  wins        INT,
  updated_at  DATETIME,
  PRIMARY KEY (steamid64, side, season, opponents)
```

A row per situation size rather than columns `clutch_1v1 … clutch_1v5`: it
extends without a migration, and 1v5 is not special enough to deserve its own
column.

**Record `attempts` as well as `wins`, always.** A bare win count is unreadable —
twenty 1v3 wins means something completely different for a player who has been
in twenty of them than for one who has been in three hundred. Wins alone would
also quietly reward *being in bad positions often*, which is the opposite of the
thing being measured. With both numbers a clutch rate falls out; with one, no
later migration can recover the other.

**The state for this already exists** (owner, 2026-07-21 — correcting an earlier
claim in this file that it would need new round-tracking infrastructure). OSBase
already holds a round's information until the round ends — that is how
`DamageReport` works, accumulating through the round and flushing at round end —
and it already handles `EventPlayerDeath`. So who is alive on each team is
knowable at every moment of the round without building anything new.

What is left is derivation, not infrastructure: on each death, if one side now
has exactly one player alive, that player is in a clutch against however many
are still up on the other side; record the attempt, then resolve it against the
round result at flush time. One caveat worth stating — the situation should be
counted from the moment it *arises*, not from the round result, or a clutch that
was lost gets no attempt recorded and the rate becomes meaningless.

### 10. Multi-kill rounds — 1k, 2k, 3k … and don't stop at 5

```sql
player_multikill_stat
  steamid64   VARCHAR(32),
  side        TINYINT,
  season      VARCHAR(8),
  kills       TINYINT,     -- how many the player got that round
  rounds      INT,         -- how many rounds ended on exactly that number
  updated_at  DATETIME,
  PRIMARY KEY (steamid64, side, season, kills)
```

Cheaper than the clutch counter: no round-state tracking, just a per-player kill
tally reset each round and written at round end.

Two things to get right, because neither can be fixed afterwards:

- **Exactly N, not "at least N".** Rows that mean "exactly" sum back to the total
  number of rounds the player got a kill in, and "at least 3" is then a SUM over
  3,4,5… Store it the other way round and the individual numbers are gone.
- **Do not cap at 5.** An ace is 5 only in 5v5. The oldswedes servers run bigger
  pub teams, so 6k and 7k rounds genuinely happen — a `TINYINT` column with no
  ceiling costs nothing, while a capped one silently folds every big round into
  the top bucket. The site can decide to *display* "5k+" grouped; that is a
  rendering choice, not a storage one.

### 11. When NOT to record — warmup, empty servers, bots

Decided with the owner 2026-07-21. These gates matter as much as the counters:
a lifetime aggregate has no way to unlearn a bad round, so anything let in here
is in forever.

- **No bots, anywhere.** Not in hits, shots, rounds, duels, clutches or
  multi-kills. A bot is not a person and has no business in a rivalry. A 1v4
  where three of them are bots therefore **is a 1v1** and counts as one.
- **Nothing during warmup.**
- **Nothing below a minimum human player count** — the owner's starting figure is
  **4**, and it should be a **config value, not a constant**: nobody knows the
  right number until real data exists, and changing it should not need a
  rebuild. Decide it at ROUND START, not round end, or normal play gets dropped
  whenever people log off towards the end of an evening. Two people warming up on
  an empty evening server produce a 100% headshot rate and a 1v1 clutch every
  round, straight into the same lifetime counters as real play.
- **After the round is decided: counted normally**, no flag, no special case —
  see below. This one changed twice during the discussion.

**On post-round kills: just count them, no flag.** Settled by the owner
2026-07-21 after some back and forth, and the simple answer won.

The concern was that the seconds after `EventRoundEnd` are mop-up — the losing
side walking out, free swings at people who have stopped playing — and that
letting them in inflates kill counts and turns a 2k into a 4k. Two intermediate
proposals came and went: dropping them entirely, then keeping them with subset
counters (`post_kills`, `post_hits`, `post_damage`) so the site could choose.

Both are dead. **A post-round kill is a kill, recorded like any other**, with no
extra column and no special case anywhere — including multi-kill rounds, which
count every kill in the round.

The reasoning: the fight genuinely does carry on for a few seconds, and the
in-game scoreboard counts those kills. Matching what the player saw at the end of
the round is worth more than a purity that would need explaining every time
somebody notices the site disagrees with the game. It also removes two fiddly
edge cases rather than solving them — the killing blow that ends the round, and
bomb-explosion deaths, are both simply kills now, with no cutoff for them to fall
the wrong side of.

The gates above still apply to them: no bots, no warmup, not below the player
threshold. Those are about whether the round counts at all, which is a different
question from when within the round something happened.

### 12. `teambets` — make the wagers worth something afterwards

The **teambets** module already lets dead players bet on which side wins the
round. Nothing is kept, so the whole thing evaporates at round end. Aggregate it,
named after the module so it is obvious where the numbers come from:

```sql
player_teambet_stat
  steamid64      VARCHAR(32),
  season         VARCHAR(8),
  bets           INT,        -- wagers placed
  wins           INT,        -- wagers that paid out
  staked         BIGINT,     -- total put in
  returned       BIGINT,     -- total paid back out
  biggest_win       INT,       -- largest single NET profit (returned - staked)
  biggest_win_stake INT,       -- what was risked to get it
  biggest_win_at    DATETIME,  -- when it happened
  updated_at     DATETIME,
  PRIMARY KEY (steamid64, season)
```

Three things that cannot be reconstructed later, which is why they are columns:

- **`staked` and `returned` separately, never a net figure.** Net profit is a
  subtraction away, but a net figure can never be split back into how much
  someone risked to get there — and the gambler who turns over ten times as much
  for the same profit is the more interesting one.
- **`biggest_win` cannot be derived from sums at all.** A single-bet leaderboard
  — "who has won the most on one wager" — is exactly the kind of thing people
  bring up years later, and no amount of totals will recover it. One row per
  player, so the leaderboard is an `ORDER BY biggest_win DESC` over a handful of
  rows rather than a scan of an event log.
- **`biggest_win` is NET, and the stake it took is stored beside it.** The ask
  was ambiguous and OSBase read it as total payout (2026-07-21); net is the
  right reading. A total payout leaderboard is really a bankroll leaderboard —
  staking 10 000 to get 10 100 back tops it, while risking 100 to win 4 900
  does not. The second one is the story people actually tell. Keeping
  `biggest_win_stake` alongside means the payout is still available (net +
  stake) and the odds are visible in the retelling, so neither reading is lost —
  which is the whole point, since the stake for that one bet is exactly the sort
  of thing no later migration can dig back out.
- **`season`**, so "this season's biggest win" exists at all.

Same gates as everything else in ask 11: no warmup, no sub-threshold servers.
A jackpot won on a two-player server is not a story.

Whatever `teambets` already tracks in memory to settle a round is presumably
most of this — the ask is to write it down before it is thrown away, not to
compute anything new.

### 13. `first_seen` — so a profile can say what period the numbers cover

A page full of lifetime totals with no dates on it invites the wrong reading:
someone sees 4 000 hits and has no idea whether that is a decade or a fortnight.

Most of this is already answerable without any change, because `season` is in
the key: `MIN(season)` says which quarter collection started in and
`MAX(updated_at)` says when it last moved. OSWeb does that now. But a quarter is
a coarse "since", and it gets coarser the moment somebody joins mid-quarter.

So, cheap and exact:

```sql
first_seen DATETIME   -- set on INSERT, never touched on UPDATE
```

on the counter tables beside the existing `updated_at`. The upsert already
writes `updated_at = NOW()`; this is the same value written only when the row is
created.

Non-retroactive like everything else here: a row that has been updated a
thousand times cannot tell you when it was first written, and `MIN(season)` will
never sharpen past the quarter. If it is worth having exact, it is worth having
before the counters go live.

### 14. `player_round_stat`: add `rounds_won`

```sql
rounds_won INT   -- of `rounds`, how many the player's side took
```

One column, and the round result is already in hand there — it is what resolves
a clutch attempt (ask 9). Losses need no column: `rounds - rounds_won`.

**This overlaps with the ranking plugin on purpose, and the overlap is the
point.** `cs2rank.lvl_base` already has `round_win` and `round_lose`, and an
earlier version of this document dropped the ask for exactly that reason. That
was too quick. What lvl_base holds is one lifetime pair per player. What
`player_round_stat` would hold is the same number split by **side** and
**season**, and behind the gates in ask 11.

That difference is the whole feature. "You win 62% as CT and 41% as T" says
something about how somebody plays; a single career win rate says almost
nothing, and a career win rate that includes warmup and near-empty servers says
less than nothing. Neither split can be recovered from lvl_base's totals, now or
ever.

The two will disagree, and that is expected rather than a bug — they count to
different rules. The site should read one of them for a given number and never
put both on the same page.

### 15. A small daily summary — because quarters cannot express "form"

`season` is the right grain for the big dimensioned tables, and wrong for
trends. A quarter is up to three months: in its first week the current season is
almost empty, so a season-vs-season comparison is noise, and halfway through
there is no way to ask "how have I been playing this week". OSWeb shows a form
block today built on quarters, and that is the honest limit of what the schema
can answer.

Daily buckets fix it, and they are cheap **as long as they stay summary-level**:

```sql
player_daily_stat
  steamid64  VARCHAR(32),
  day        DATE,
  hits       INT,
  damage     INT,
  headshots  INT,
  shots      INT,
  rounds     INT,
  updated_at DATETIME,
  PRIMARY KEY (steamid64, day)
```

**No weapon, no hitgroup, no side.** That is the whole trick. Those dimensions
are what make `player_hit_stat` big, and multiplying them by 365 would be
reckless; without them a row is one player's day. A community of 200 regulars
playing a hundred days a year is on the order of 20 000 rows a year — nothing.

What it unlocks that quarters cannot: last 7 days, last 30, this month, a form
curve on the profile instead of a single delta, "back after a break", and any
window somebody thinks of later. Quarterly figures remain derivable from it as a
sanity check against the existing tables.

**One dimension deliberately left out, which is itself a now-or-never call:**
`side`. Including it would double a tiny table and allow "my form as CT". I have
left it out because the dimensioned truth already lives in the quarterly tables
and this one is meant to stay small — but if per-side form is wanted at all, the
column has to go in before writing starts, like everything else here.

### 16. Two roll-ups, because some questions are expensive from the detail

Found while building against the real schema rather than imagined: these are the
places where the counters hold the answer but make it costly to get out.

**a) `player_duel_total (steamid64, season)` → `kills`, `deaths`**

The nemesis verdict's strongest factor asks whether an opponent does better
against YOU than against everyone else. Answering the second half means summing
that person's every duel row, in both directions — and it has to be done for
each candidate, on every profile view. `DuelStatRepository::generalForm()` does
exactly that today with a UNION over two GROUP BYs.

It is the same number OSBase already has in hand when it writes a duel row, so
maintaining a per-player total costs one more upsert per flush and turns a scan
into a point lookup. It also makes a server-wide "who kills most" leaderboard a
sort over one row per player instead of an aggregation across the pair table.

**b) `server_stat_season (season)` → the same summary columns, across everyone**

There is currently no cheap way to ask "is this player above or below the
server's normal". Every comparison the site can make is against the player's own
past, which is useful but insular — "your 19% headshot rate" means nothing to
somebody who does not know that the server average is 14%.

Computing it live means aggregating every player's rows on every page view. As a
maintained roll-up it is a handful of rows in total, updated on the same flush.

**Not an ask: the top-N truncation is OSWeb's to fix.** `DuelStatRepository`
takes the 5 000 busiest rows and slices a top-list per weapon in PHP, because a
top-N-per-group needs a window function. For a veteran with many opponents and
many weapons, a rarely-used weapon's list could silently lose entries. That is a
query to rewrite on the reading side (MariaDB has had `ROW_NUMBER()` since
10.2), not a reason to store anything differently — noted here so it is not
mistaken for a schema problem.

### 17. `map` — narrowly, in `player_round_stat` only

Nothing recorded anywhere carries the map. So "your best map", "where you win",
"how much have I actually played Mirage" are all unanswerable, now and forever
after, because a round already summed without it cannot be split apart later.

This was discussed with the owner and then nearly lost: it was agreed in
conversation and never written down until this entry. Worth noting as its own
small lesson about why this file exists.

```sql
-- player_round_stat
map VARCHAR(32)   -- in the PRIMARY KEY
```

**Deliberately not in `player_hit_stat`.** That table is already the big one —
weapon × hitgroup × direction × side × season — and multiplying it by a map
rotation would be the one genuinely expensive thing in this document. Rounds
are cheap: a player has one row per (side, season, map), so a ten-map rotation
costs roughly ten rows per player per quarter.

What that narrow version still buys: rounds and win rate per map (with ask 14),
bomb work per map (ask 8), most-played map, "you've never actually played
Nuke". What it does not buy is a per-map heatmap or per-map weapon breakdown —
those need the dimension on the big table, and that trade can be made later if
anyone actually wants it badly enough to pay for it. Starting narrow is
reversible in the direction that matters: adding the map to more tables later
loses only the history before that point, whereas putting it everywhere now
costs row count forever.

### 18. `player_daily_stat` needs `kills` and `seconds` — and `headshots` needs defining

Driven by a concrete screen rather than a guess: the owner wants to rebuild the
old site's **"Gårdagens highlights"** widget, recovered from a web archive of the
previous oldswedes. Five rows: best and worst skill change, most headshots, most
kills, most time online — each naming one player and one number for yesterday.

Checking the widget against the schema is what found this, and it is worth
saying that the check only became possible because somebody produced the actual
screen. A table designed from "we want daily numbers" cannot be validated; a
table designed against five specific rows can.

**Two columns missing, both in `player_daily_stat`:**

```sql
kills   INT,   -- kills that day
seconds INT,   -- seconds connected that day
```

- **`kills`.** The daily table carries hits, damage, headshots, shots and
  rounds — everything except the one number a highlights board leads with.
  "Flest kills" is unanswerable today.
- **`seconds`.** "Mest online" has no source anywhere. `lvl_base` holds lifetime
  playtime and OSWeb's `server_connection` is pruned to a short window, so
  neither can say what yesterday looked like. It is also the row that rewards
  simply turning up, which is exactly what a community board should celebrate.

**And a definition that has to be settled before writing starts:** what does
`headshots` mean in this table? In `player_hit_stat` terms a headshot is a HIT
with hitgroup 1, and a player can put three of those into somebody wearing a
helmet without killing them. A scoreboard's "HS" means headshot KILLS. The old
widget's "Flest HS" almost certainly meant kills.

Both are defensible; they are just different numbers, and a column named
`headshots` that quietly means the less expected one is worse than either. If it
is hits, the widget's row is mislabelled forever. Ideally: `headshots` for
headshot kills, since that is what everyone reads it as, and the hit-level
figure already lives in `player_hit_stat` where hitgroup makes it unambiguous.

**What the widget CAN already do:** best and worst skill change come from
`skill_log` — written by OSBase's GameStats module after every map, and already
read by OSWeb's `SkillRepository`, which is what draws the form curve on a
profile. It is live and has rows. The delta over a day is the last logged value
minus the first. So two of the five rows are buildable now and the other three
are one deploy away.

**Which "skill", though.** The old widget's number was HLstatsX points, and the
site now has three things that could answer to that name: `skill_log.skill`
(OSBase GameStats, what the profile graph uses), `lvl_base.value` (the cs2rank
ladder, what the Rank page uses), and `elo_rating.rating` (tournament-scoped).
The widget should use `skill_log`, because it is the number a profile already
shows and the only one with day-level history. Worth stating so nobody later
"fixes" the widget to read the ladder and quietly puts two different skills on
one site — the same trap the retired kill stopgap set.

### 19. ELO replaces TWO systems, and inherits what they both did

Confirmed by the owner (2026-07-21), and the clean statement of it is: **GameStats
and cs2rank both go, and ELO replaces the pair.** Its part one (the estimated
rating) takes over from GameStats' `skill_log`; its part two (the quarterly,
opponent-weighted points) takes over from the cs2rank ladder. Two plugins out,
one in.

That also answers the question left open in ask 4 about what becomes of
`skill_log`: it is superseded, not kept alongside. Which is the right outcome —
the site would otherwise show a member an ELO rating and a GameStats skill
estimate that disagree, both claiming to say how good they are.

The new ELO runs on **public play** — the same thing cs2rank measures.
One less plugin to maintain, and a better number: LevelsRanks-style ladders award
points partly for time played, so they measure attendance as much as skill and
the person who sits up longest climbs. ELO measures who beats whom.

So ELO does not sit beside the ladder, it replaces it, and it has to be able to
do what the ladder did.

**a) Seasons. `elo_rating` has no season dimension at all.**

Its key is `(steamid64)`, one row per player, one current rating. cs2rank
handles a reset by renaming the whole table aside — `lvl_base_20260401`,
`lvl_base_20260717` — and OSWeb has pages built on those frozen tables, so
members can look at past seasons.

Take the ladder away and that history has nowhere to live. Worse, the quarterly
rank reset is an established thing here: rank/ELO resets, lifetime stats never
do. A reset against a single-row-per-player table without archiving is simply
deletion.

Either shape works — `season` in the primary key, or freezing a copy the way
cs2rank does — but one of them has to exist before the first reset, not after.

**b) The site's readers have to move with it — there are nine.**

Counted rather than guessed (2026-07-21). Everything reading either superseded
system:

- `SkillRepository` → `skill_log`. Draws the **form curve** on every profile.
- `RankRepository`, `RankSeasonRepository`, `RankController` → `cs2rank.lvl_base`
  and its frozen season tables. The ladder and the seasons pages.
- `ProfileController` (skill card + history), `SettingsController`,
  `TournamentAdminController` (seeds a bracket from skill),
  `TournamentPlayerRepository` (matches players by skill).
- **`Services\Ai\RankSeasonReset`** — the automation that performs the quarterly
  reset, and `TronRegistry`, which schedules it.

That last one matters most for sequencing: the reset job is built around
cs2rank's rename-the-table trick. Whatever archiving shape ELO's points take
(19a), that job has to be rewritten in the same change, or the first quarter
boundary after go-live either does nothing or deletes the standings.

Leaving any of these pointed at the old system is the failure the log-parsed
kill stopgap created — two answers to "how good is this member", on one site,
drifting apart every evening. It cost a migration and a retirement to get out of
last time. Same resolution: one source wins, the others are retired. This is
OSWeb's work rather than OSBase's, and it is a bigger job than "point the Rank
page somewhere else".

**c) The old ladder's history should be kept, not dropped.**

When the plugin stops, `cs2rank` stops being written but does not stop existing.
`lvl_base` and every frozen `lvl_base_YYYYMMDD` are years of the community's
own record, and the site's north star is that content is never deleted — only a
GDPR erasure removes anything.

So the Rank pages have two jobs in that change, not one: point the live ladder
at ELO, and keep the cs2rank seasons reachable as an archive. A member who
topped a 2025 season should still be able to find it.

**On its own page, not the new system's** (owner, 2026-07-22, improving on the
draft here). This originally said "reachable as an archive, clearly labelled" —
the same page with a caveat on it. A separate page is stronger for the same
reason a chat line shows one number: two scoring systems side by side invite
comparison, and a label is only a request not to. Putting them on different
pages makes comparing them take effort instead of taking none.

**And it means `RankSeasonRepository` needs no rewrite at all.** It reads frozen
tables that stop being written on 1 October and then never change again. The
code already works, against data that is now permanent. It needs a new home and
a heading saying what it is — which moves one of the ten readers from "rewrite"
to "relocate".

### 20. Rewarding time played WITHOUT putting time back into the rating

The owner's requirement, stated after deciding ELO replaces the ladder
(2026-07-21): **someone who is online a lot should be rewarded too.** Putting an
hour into the server should let you climb. The system should be worth showing up
for.

That is a real gap in a pure ELO rather than a nostalgic preference. Two players
of equal skill get the same rating whether one has played fifty rounds or five
thousand: the number converges and then stops moving. There is nothing left to
chase, which is the opposite of what a community ladder is for.

The trap is fixing it by feeding time back into the rating. That is what
LevelsRanks does, and it is the reason the owner is replacing it — a number that
mixes skill and attendance describes neither. So the fix has to sit beside the
rating, not inside it.

**The owner's design (2026-07-21): the rank has TWO PARTS, and neither of them
is time.** My first reading of the requirement was wrong — I proposed an
"activity" component earned from hours played. That is not it, and the
difference matters.

**Part one — a rating, estimated from accumulated performance.** Kills,
headshots, assists, deaths and the rest are collected, and from them a
skill figure is estimated, in the spirit of a Premier rank. It answers *how good
is this player*, and like any rating it converges: playing more sharpens it
rather than inflating it.

**Part two — a points score, weighted by who you beat.** Points are awarded for
kills, assists, round wins and so on, and the award is scaled by the opponent:
**beating a stronger player is worth more than beating a weaker one.** This is
the HLstatsX mechanic the community remembers, and it is what makes an evening
on the server worth something.

That solves the "reward showing up" requirement far better than the
confidence-adjusted ladder proposed above, and without the flaw of a time-based
ladder: points accumulate by DOING things, not by being connected. Someone idling
in spectator earns nothing. The Glicko suggestion is left in the history above
because it was a reasonable answer to the wrong question — worth keeping only so
nobody re-proposes it.

**Store both parts separately and never only the total.** Same rule as
`staked`/`returned` in ask 12: a sum can never be split back into what made it.
One column with points folded into the rating makes "how good is this player,
ignoring how much they have played" permanently unanswerable — which is the
question that replacing cs2rank was about in the first place.

**Two tables, not two columns** — corrected by the OSBase side (2026-07-21),
who caught that the sketch here contradicted the paragraph above it. It said
`season` belongs on the points and not the rating, then drew both as flat
columns in one `(steamid64)` row with no season anywhere. That resolves nothing:
either season is absent and the points cannot reset cleanly, or season joins the
key and the never-resetting rating has to be copied forward by hand into every
new season row — a migration step no other table in this system needs.

```sql
-- elo_rating: unchanged key, no season. "Who is good", continuously
-- re-estimated, never reset.
steamid64, rating, matches, updated_at
PRIMARY KEY (steamid64)

-- elo_points: new table, season in the key. The quarterly contest.
steamid64, season, points, updated_at
PRIMARY KEY (steamid64, season)
```

This is the same shape as every counter in asks 1–17, which is the argument for
it: a new season is a new string. Nothing to rename, no migration code, no
ceremony anybody can forget to run, and the whole history stays directly
queryable (`... WHERE steamid64 = ? ORDER BY season`) rather than hidden behind
a table name you have to know to find. It also makes OSWeb's `RankSeasonReset`
much smaller than it is today: it stops renaming anything and simply starts
writing under a new season value.

**`elo_kill_event.match_id` has to become nullable after all** — but for a
different reason than the withdrawn claim in ask 4, and with an addition that
claim did not have. It is `INT NOT NULL` because every row needed a tournament
window. If ELO counts all play, ordinary evenings need a null (or a 0 sentinel)
— **while a real tournament match must still write the actual
`tournament_match.id`.** Without that, the history loses the ability to tell a
Tuesday pub round from the one tournament of the year, which is exactly the
distinction the tournament gate existed to protect.

**The rating formula is open, and the OSBase side has pushed back on the
framing.** The description above — "estimated from accumulated kills, headshots,
assists, deaths" — reads as summation, and their objection is that a career sum
only ever grows for an active player whether they improve or decline, which is
not what "how good is this player now" measures. CS2's own Premier rank is
Elo-like for the same reason: it moves up and down relative to your current
level rather than accumulating.

Their proposal, which is sound and is the lower-risk path: keep the existing
duel Elo as the core and add two terms rather than replacing the mechanic.
Kills and deaths are already handled — every kill is an attacker gain and a
victim loss in the same calculation, so "deaths" needs no separate weight.
Headshots become a bonus proportional to the delta already computed, which
preserves the calibration (headshotting a strong player still beats
headshotting a weak one). Assists get a small FLAT reward, not opponent-weighted
— an assist contributed to the duel rather than winning it, and should not be
able to move a rating the way a kill does.

That is a recommendation, not a decision. The weights are the same kind of
calibration guess this document is honest about elsewhere, and the owner should
settle the approach before it is built.

**One thing still open:** which number goes next to a member's name. The
quarterly points are the standings; the rating is what they *are*. Showing both
is easy — but the site has to pick which one leads, and that choice is what
members will argue about.

### 21. The team balancer is a tenth reader — and it is on OSBase's side

Raised by the owner (2026-07-21), and it is the consequence neither side had
counted: **OSBase's live team balancer is built on GameStats skill.** The list of
nine readers in ask 19 missed it completely, because that list was OSWeb's
consumers and this one lives in the plugin. Retiring GameStats takes the
balancer's input away with it.

**It must read part one, never part two.** The quarterly points look like a
ranking and are the wrong number here: they reset every three months, so in the
first week of a quarter every veteran on the server reads as a beginner and the
balancer deals teams from noise. It would do that four times a year, and the
symptom — "balancing is broken again for a while" — points nowhere near the
cause. The rating is the number that means *how good is this player*, it
persists across the boundary, and it is what the balancer wants.

**Cold start needs an answer.** A rating has to come from somewhere for someone
who has never played, and a default that reads as "bad" stacks every newcomer
onto one team. OSWeb's own tournament balancer already solves this and is worth
copying: `Services\BalancedDraft` treats an unrated player as the **roster
median**, so they neither drag a team down nor lift it. It is also entirely
relative — the comments are explicit that fixed skill-point tolerances "would be
wrong for a differently-skilled roster" — which means OSWeb's balancer survives
the change of scale for free. Anything on the OSBase side with an absolute
threshold in it does not: GameStats skill and an Elo rating do not share a
range, and a hardcoded number will silently mean something different the day the
source changes.

**The feedback loop this creates runs the right way, and it partly answers the
original objection.** OSBase argued that pub play is poor ground for a rating
because teams are uneven. If the balancer reads the rating, the rating is what
makes the teams even — it manufactures the matched competition it needs. That is
self-reinforcing rather than circular, with one caveat: early on the ratings are
noisy, so the balancing is too, and it is worth requiring some minimum number of
rated matches before the balancer trusts a player's figure rather than treating
them as median.

### 22. `skill_log` was a LOG; `elo_rating` is a STATE — the form curve has no source

Found while checking the built ELO package against the readers it replaces
(2026-07-21). Ask 19 said the rating supersedes `skill_log`, which is right
about what the numbers *mean* and wrong about their shape.

`skill_log` has a row per player per map with a timestamp, so
`SkillRepository::dailyHistory()` reads it as a time series — `GROUP BY
DATE(datestr)` over a 180-day window — and that is what draws the **form curve
on every profile**, plus the peak/change headline beside it. `elo_rating` holds
one row per player and one current value. It has no history at all.

So retiring GameStats removes the form curve's only source, and nothing in the
new design replaces it. Ask 18's highlights widget loses two of its five rows to
the same gap: "best and worst skill change yesterday" needs a rating *per day*,
and a single current value cannot produce a delta.

This is non-retroactive in the strongest sense in this document. Every other
missing dimension loses a split; this loses time itself. A rating that was never
written down on the day cannot be recovered from the rating it has since become,
by any query, ever.

**The cheap fix is one column on a table that already exists.**
`player_daily_stat` is already keyed `(steamid64, day)` — exactly the grain the
form curve uses, since `dailyHistory()` collapses to days anyway:

```sql
-- player_daily_stat
rating INT,   -- the player's rating at the end of that day
points INT,   -- and their season points, same snapshot
```

A snapshot on the day's last flush, not a running average — the rating is
already smoothed by being a rating. `points` alongside it costs nothing and
makes "who climbed most this week" answerable, which is the same question the
old HLstatsX board led with.

The alternative — keeping GameStats alive purely so the curve has data — is the
thing this whole consolidation exists to avoid: two skill numbers on one site,
drifting apart every evening.

**Built (OSBase, 2026-07-21)**, with two decisions worth keeping: the columns
are `INT NULL` where NULL means "the ELO module is not loaded" rather than a
rating of 0 — a gap in the curve instead of a false collapse to zero — and they
are read from EloRating's in-memory cache rather than the database, because
`DamageReport` and `EloRating` both subscribe to `EventRoundEnd` independently
with no guaranteed order between them.

**What this costs OSWeb's reader, which is fine but not nothing.**
`SkillRepository::dailyHistory()` returns `maps` and `best` per day alongside
the skill: how many maps that day rests on, and the best single map result. A
snapshot has neither. `rounds` on the same table is the better activity figure
anyway, but the *best map of the day* is simply gone as a concept — a rating has
no per-map high score to take the max of. The peak in `summarise()` becomes the
highest end-of-day rating, which is still the number people mean by "my best".
The rewrite also has to skip NULL rows rather than read them as zero.

### 23. The cutover: run both, switch at a reset we choose

The owner's sequencing (2026-07-21), and it removes the hardest part of this
change: **cs2rank runs as a separate plugin, so it can keep running until we
reset.** There is no big-bang cutover. ELO starts writing, cs2rank keeps writing,
the site keeps reading the old numbers, and the switch happens at a season
boundary we pick.

**This does not break the one-number rule.** "Two competing skill numbers" is a
statement about what the site *displays*, not about what the plugins write. Two
writers with one reader is fine, and it is the only version of this change that
can be verified before anyone sees it.

**Correcting an overstatement made here earlier:** `Services\Ai\RankSeasonReset`
was described as a job that would fire at the next quarter boundary against a
table nobody writes any more. It cannot. It is off until armed
(`rank.auto_reset`, unset), and it is not calendar-bound — it fires only once
`rank.season_days` (90) have passed since `rank.last_reset_at`, which is also
unset. It is a lever, not a deadline. The rewrite still has to happen before it
is ever armed against ELO, but nothing forces the date.

**The argument for deploying OSBase now rather than at the switch: a rating
needs a running start.** Points can begin from zero — that is what a season is.
A rating cannot. It converges from matches, so on day one everyone sits at their
default, and the ladder shows a flat field while the balancer treats the entire
server as median. Run it for a quarter first and the day we flip, the ratings
are already true and the standings mean something immediately.

**And it is the only chance to calibrate.** Every number in this document marked
as a guess — `headshot_bonus_pct`, `assist_reward`, the balancer's seven derived
thresholds, the nemesis weights and style ceilings — has nothing to check
against until real rows exist. A parallel quarter provides them while cs2rank is
still the number on the page, so a wrong weight is an observation instead of an
incident. The two ladders can be compared directly: if ELO's top twenty is
unrecognisable next to cs2rank's, that is worth knowing before it is the only
ladder.

**The gates are the exception to "it can wait".** Ask 11 is not calibration —
a round recorded without them is permanently contaminated, parallel run or not.
They are built, so this costs nothing, but it is the reason the parallel run is
safe at all.

**The next reset is a week away (owner, 2026-07-21), and the recommendation is
to let it pass.** Switch at the one after it. A reset is the worst possible
moment to also change systems: everyone's standings go to zero anyway, so a cold
ELO launched the same day makes the two disruptions indistinguishable — nobody
on the server can tell "new season" from "the new thing is broken", and every
complaint becomes unattributable. A cold rating on top of a zeroed ladder looks
like a failure even when it is working correctly.

**What the week IS for: release OSBase's writing side AT that reset, not after.**
If ELO starts mid-quarter its first season is a stub, and comparing it against
cs2rank's full season compares different things. Starting both clocks in the
same moment is what makes the two ladders directly comparable, which was the
entire point of running them in parallel. The site changes nothing that week:
cs2rank resets as usual and stays the number on the page.

**The schema is the only thing that must be right before that button.** After
the first write the dimensions are locked forever — the rule this document opens
with. It is the last cheap moment to read the primary keys once more.

**It is THREE switches, not one** (owner, 2026-07-21, correcting a conflation
made here). The reset zeroes cs2rank's ladder and nothing else — `skill_log`
keeps being written straight through it. So the balancer's current source
survives the reset untouched, and the three moves come apart:

1. **The visible ladder**, cs2rank → `elo_points`. Happens at a reset, because
   that is when standings go to zero anyway.
2. **The profile form curve**, `skill_log` → `player_daily_stat.rating`. Waits
   for enough daily snapshots to draw a line at all.
3. **The team balancer**, `skill_log` → `elo_rating`. Waits for the ratings to
   converge — and can wait, precisely because GameStats is not what gets reset.

Keeping GameStats alive a while past the ladder switch costs nothing and keeps
the balancer on a known-good source while the ratings warm up.

**Don't date switch 3 — measure it.** A month is a guess, and the thing that
actually matters is how many of the regulars have passed `min_rated_matches`.
Switch while most of them are still below it and the balancer treats nearly
everyone as roster median, which is the inert-balancer failure in a new costume.

Better, and nearly free while both sources are live: **run the balancer's Elo
path in shadow.** Each time it balances, compute the division it *would* have
made from ratings alongside the one it actually makes from skill, and log the
divergence. That turns "is it ready" into an observation instead of a
prediction, and it is the same argument as the parallel ladder — a wrong
threshold found in a log is not an incident. The overlap month also gives the
only direct check the form curve will ever get: `skill_log` and the daily rating
snapshots describing the same players over the same days.

**A bug that only existed because the switch was made safe** (OSBase,
2026-07-21, found and fixed while adding `balancer_skill_source`). The seven
relativised thresholds used `ratio * currentRosterSpread` unconditionally, and
that spread is always on the Elo scale — so with `gamestats` as the default it
would have fed GameStats-scale gaps against Elo-scale thresholds from day one.

Worth recording as its own lesson. In the original build, where the source was
replaced outright, the code was self-consistent: Elo scale throughout. Adding
the default-off mode created a mixed-scale state that neither the old design nor
the new one had. It would have shipped as "we deployed with the safe default and
the balancer went strange immediately" — exactly what the safe default existed
to prevent, and nearly unattributable, since everyone would have been looking at
the Elo path on the assumption that the inert default could not be the cause.

The general shape: making a switch toggleable creates states that neither
end of the switch has, and those states are the ones nobody designed. The
thresholds are now properties branching on the mode — literal constants for
`gamestats`, ratio × spread only for `elo`.

**Which leaves the same question one level in, for `shadow`.** That mode
balances on GameStats and computes what Elo *would* have done. Those are two
divisions on two scales in one pass, so it needs BOTH threshold sets live at
once — literals for the division it actually makes, ratio × spread for the one
it only logs. If the property returns the `gamestats` literals throughout, the
shadow log measures the wrong thresholds rather than the rating, and reports
divergence that is an artefact. The entire point of shadow mode is that its
numbers can be trusted without anyone watching.

**The two systems do not share a season boundary.** cs2rank resets on a rolling
90-day clock from whenever it was last run — so a reset in late July lands in the
middle of Q3, while ELO's `2026Q3` has been running since 1 July. They sit about
a month apart. The parallel run is still worth having, but it compares shapes
rather than the same dates.

**That makes 1 October the cutover, not "cs2rank's next reset"** (which would be
late October and arbitrary). On 1 October ELO's points roll to `2026Q4` by
themselves, with nothing to run — the whole point of season-in-the-key. cs2rank
gets a real final season rather than the three-day stub an August cut would have
archived forever, and ELO has roughly nine weeks of warm ratings behind it on
the day it becomes the visible ladder.

**What quietly disappears at that boundary: the announcement.**
`Services\Ai\RankSeasonReset` does two jobs, and only one of them is the rename.
The other is editorial — it posts a news article naming the winner, computes the
season's awards (K/D, headshot rate, accuracy, win rate, each with a 50-kill
floor so one lucky round cannot take a title) and links the frozen board. That
is the part members actually see.

Its trigger was "90 days have passed since the last reset". With season-in-the-
key nothing fires at all: the rows simply start carrying a new string. So the
mechanical half vanishing is exactly what makes the editorial half easy to lose
— the boundary passes cleanly, the new season starts fine, and nobody announces
that the old one ended. Success and silence look identical.

It needs its own calendar trigger on the quarter boundary. One property makes
that much safer than what it replaces: the closing season's data never moves, so
the job reads `WHERE season = '2026Q3'` whenever it happens to run. Under the
rename, running late meant new rows landing in the table being frozen. The new
shape cannot corrupt the boundary by being late — it can only be late.

**The first season will be a partial one, and it is labelled as a full one.**
Writing starts 1 August; `season` is computed from the round's date, so those
rows carry `2026Q3` — a label that means 1 July to 30 September. Nothing in the
row says collection began two months in.

For the trial season itself this costs nothing, since nobody is reading it. The
label is what outlives it: a year from now `2026Q3` sits in the table looking
like any other quarter.

**Ask 13 is what stops that becoming a lie**, and it is already built.
`first_seen DATETIME`, written on INSERT and never touched again, makes the
partial season describe itself rather than depending on someone remembering.
Without it `MIN(season) = 2026Q3` would read as "since July" permanently — which
is precisely the wrong reading ask 13 was added to prevent, arriving earlier
than expected.

**The rule this puts on the reading side: season-over-season may compare rates,
never totals.** ADR, headshot rate, accuracy and win rate all survive a short
season. "Kills this season versus last" compares two months against three and
renders as a slump that never happened. This applies exactly once — `2026Q4`
and everything after it is a full quarter.

### 24. The in-game commands are an eleventh reader, and OSBase's

Raised by the owner (2026-07-22): cs2rank contributes chat commands — `!top`
for where you sit on the ladder, `!rank` for your own figures. Switch the plugin
off and they vanish from the server that second. Every reader counted so far is
a web page; this is the one players actually touch, and its absence is noticed
immediately by everyone rather than eventually by somebody.

**The real output, captured from the server (owner, 2026-07-22), and the
command list is closed at these two — nothing else matters:**

```
[ Ranks ] Your position in the top: #1/387
[ Ranks ] Experience: 164123 (Rank: 18K+)
[ Ranks ] Kills: 7274 (Headshot: 3133) | Deaths: 6024 | Assists: 1890
[ Ranks ] Winning rounds: 4400 | Losing rounds: 3787
[ Ranks ] Percentage Headshot: 43.07 | KD: 1.21
[ Ranks ] Total Play Time: 8 Days, 2 Hours, 9 Minutes, 45 Seconds

[ Top Players ]
1. Pintuz [18K+] - experience: 164123
2. Skabbräv [18K+] - experience: 153922
```

That is not two commands, it is a statistics screen — and reading it against the
new schema found three things.

**a) `assists` are stored nowhere. Now-or-never.** They appear in this document
only as an INPUT to the rating formula (`e.Assister` earns a flat reward), never
as a counter. So `Assists: 1890` has no source after the switch, and neither
does any future assist leaderboard. The event is already handled; the count is
simply not kept.

**Where they go changed once writing out the command spec.** The first draft put
them on `player_round_stat`. That works but is the wrong home: writing `!rank`
line by line shows it wants a player's period totals in one place, and
`player_duel_total (steamid64, season) → kills, deaths` is already exactly that
shape. Adding `assists` and `headshots` there turns most of `!rank` into a
single-row lookup instead of an aggregation over every opponent pair the player
met that season.

That table was created in ask 16a for the nemesis query. `!rank` is a second,
independent reason for the same roll-up, arriving from a completely different
direction — which is usually the sign that a roll-up is real rather than an
optimisation for one caller. Worth acknowledging that the name is now narrower
than the contents: it is the player's period summary, not only a duel total.

**b) The day-one regression was over-called here, and the owner's correction
removes most of it.** This was written up as the biggest perception risk in the
project: `!rank` printing 7 274 kills today and two months' worth on switch day,
with the heaviest players losing the most visible history.

That misread the screen. **`!rank` already shows the current period, not a
lifetime** — those 7 274 kills and eight days are since the last reset. So the
numbers going to zero is not a regression at all; it is what every quarter
already does, and what everybody on the server already recognises.

The mistake is the same one made about `[18K+]` in the same reading: the figures
were large, so they were assumed to be lifetime. Twice in one screenshot.

Cutting over exactly on a season boundary is what makes this land softly, and
that was already the plan for other reasons. `!rank` goes from "Q3 nearly over"
to "Q4 just started", which is what it does anyway. The change hides inside
something that already happens.

**A division was proposed here and immediately corrected** (owner, 2026-07-22).
The draft said "the period lives in chat, the lifetime lives on the web", which
is too tidy: the site has a whole rank section — the ladder, the seasons
archive, the skill card on a profile — and it reads the period too.

The real difference is not the data but how much each surface holds. A chat line
shows one thing, so `!rank` shows the period. A web page has room and
navigation, so it can show period and lifetime together and page back through
seasons.

**What stays true is that nothing is merged across the boundary.** cs2rank's
totals were counted with no gates at all — warmup, bots and two-player servers
all included, which is what ask 11 exists to exclude — so folding them into the
new counters would corrupt them permanently. Both are shown; neither is added to
the other.

**Two site features the owner wants from this** (2026-07-22): a **stats page**
listing everyone in a proper leaderboard, and the **current standing on the
profile** — "third this period" visible where you already look someone up.

That makes the Rank work a rebuild rather than a repoint, which is worth being
honest about in the estimate: today's page is built around cs2rank's columns and
its frozen tables, and the new one shows two numbers per player against a season
column.

**One design point to settle now, of the same kind as OSBase's NULL-versus-0
call on the rating: a player with no rows this season has NO position, not last
place.** `#387/387` for somebody who has not played since March is wrong in a
way that looks right, and once the code computes a number anyway the distinction
cannot be recovered by a later reader. The position itself is cheap — `COUNT(*)
+ 1 WHERE season = ? AND points > ?` over a few hundred rows — so it needs no
stored column.

**The seasons page gets simpler, not harder.** `/rank/seasons` currently
discovers frozen `lvl_base_YYYYMMDD` tables by looking for table names of the
right shape. With season in the key it becomes `SELECT DISTINCT season FROM
elo_points ORDER BY season DESC` — a query instead of table-name archaeology,
and seasons can be compared against each other in one statement, which was not
possible at all when each lived in its own table.

**c) `[18K+]` needs no replacement, and reading it as one was a mistake worth
recording.** It was written up here as a named tier — the badge people identify
with, a thing ELO would have to reproduce and calibrate thresholds for. The
owner corrected it: it means "more than 18 000 points", the top bracket of a
threshold list and nothing more.

The evidence was in the same screenshot and got walked straight past. **All ten
players in `!top` carry `[18K+]`** — Pintuz on 164 123 and Bl@ck on 70 097 wear
the same label across a 2.3× gap. A badge shared by everyone in the top ten is
not an identity; it is a ceiling that was passed years ago and has said nothing
about anybody since.

That dissolves the question rather than answering it: no tier system to design,
no thresholds to guess. It is also a small argument for the change, since a
rating separates those ten and the badge cannot.

**It forces the question left open in ask 20 — which number goes next to a
member's name.** On a profile both can be shown with room to explain. A chat
line has no such room: `!rank` prints one thing. So the answer has to be decided
before the commands are written, not after.

The shape that probably fits: `!rank` leads with the quarterly points, because
that is the standings and the thing that moves tonight, with the rating beside
it as what you *are*. `!top` ranks by points for the same reason — a leaderboard
people climb has to be climbable. But that is a recommendation, and it is the
owner's call.

**Starting collection early is free; a reset mechanism is not.** Beginning to
write before 1 August costs nothing and buys the rating a longer run-up, which
is the only part that needs one. Building a way to zero the points on demand
would put back exactly what season-in-the-key removed: a reset stops being an
operation at all, since a new season is just a new string. On-demand zeroing
means either deleting `2026Q3`'s rows, against the rule that nothing here is
deleted, or inventing a season label that is not a quarter, at which point it no
longer falls out of the round's date. The boundary on 1 October arrives by
itself and cannot be forgotten.

**Open, and the owner's call: what happens to the trial quarter's points.**
They are a real season's standings written while nobody was looking at them.
Discarding them is the tidy answer; keeping them as an archived, plainly
labelled trial season is the one that matches the rule that nothing here is
deleted.

### 25. GameStats is two things in one module — and one gate fails open

Answered by the OSBase side (2026-07-22) after the owner pointed out that
GameStats does more than write stats: it holds the match state. The research
confirms it and closes the question. GameStats bundles two responsibilities:

- **The skill-scoring engine** — `calcSkill()`, `skill_log`, the 90-day cache.
  This is the part being migrated, and only `SkillResolver` reads it from
  outside. `TeamBalancer` never touches it directly.
- **A de-facto shared match-state and team-roster service** — the warmup flag,
  the round counter, the team rosters, raw per-player counters, the swap
  immunity. No migration path exists for any of it, and it is load-bearing for
  five other modules independently of skill: `WeaponRestrict`, `TeamBets`,
  `DamageReport`, `EventWeekend` and `EloRating` itself.

So GameStats cannot be retired by this project, and the Elo cutover does not
bring its removal any closer. That is the better outcome, as noted in the
balancer discussion: `balancer_skill_source` stays a way back permanently rather
than only during a transition.

**The finding inside the report that matters more than its conclusion: the
warmup gate fails open.** `EloRating.cs:218` reads `gameStats?.IsWarmup`. With a
null-conditional the expression is `null` when GameStats is absent, and `null ==
true` is `false` — which reads as "not warmup". The same shape appears in
`TeamBets.cs:745`, `DamageReport.cs:262` and `EventWeekend.cs:407`.

Warmup is one of ask 11's three gates, and gates are the one category in this
whole document that cannot be repaired afterwards. A gate that fails open,
silently, into lifetime counters is the worst available failure mode here.

**Three of those four were wrong, and the mistake is worth recording because it
is the one this document keeps catching in other people.** `EloRating`,
`TeamBets` and `DamageReport` all write `gameStats?.IsWarmup ?? true` — the
`?? true` closes the gate exactly as it should. The analysis above is correct
about the bare `?.` expression and simply never saw the rest of the line.

The cause: the claim was reasoned from the line references and the fragment
quoted in the dependency report, not from the source. OSBase's code is not
readable from this side at all, so every statement here about it is inference
from a second-hand summary — and it was still phrased as a finding rather than a
question. "Check these four places" would have been true; "these four are broken"
was not.

**One was real.** `EventWeekend.cs:407` read `ignoreWarmup && gameStats != null
&& gameStats.IsWarmup`, where a null GameStats makes the whole condition false,
so the kill is scored without knowing whether it was warmup. Pre-existing, and
fixed to the same fail-closed shape as the other three. `weapon_event_kill` is
exactly as non-retroactive as everything else here.

That one hit does not make the method sound. One in four, stated confidently, is
a bad rate for assertions about code you cannot read.

None of it was a live bug in any case: `gameStats` is constructed unconditionally
in `OSBase.cs:42` and is never null today. The principle stands for whatever
comes next — a gate should fail closed, because absent state means we do not
know, and "do not know" must never resolve to "yes, count it".

### 26. What happened to the bomb you planted

Ask 8 counts the plant and stops there. A plant that ticks down to a detonation
and a plant a CT walks up and defuses are the same row today, and the difference
is the whole thing people argue about — planting early into a lost site and
planting when the site is actually held look identical in `bomb_plants`.

```sql
-- player_round_stat, alongside bomb_plants (ask 8)
plants_exploded INT,   -- of the bombs THIS player planted, how many detonated
plants_defused  INT,   -- ... and how many a CT got to in time
```

**Credit the planter, not whoever is standing there when it goes off.**
`EventBombExploded` does carry a `userid`, but nothing should depend on it:
remember the planter's steamid64 in the round state that already exists for
`defuse_fails`, and resolve it on `EventBombExploded` / `EventBombDefused`. A
stored id also survives the planter disconnecting before the timer runs out,
which a live player slot does not.

**Both outcomes get their own counter — do not derive one from the other.**
`bomb_plants - plants_exploded` is *not* "got defused": a round can end with the
bomb still ticking (`mp_restartgame`, map change, match end, everyone leaving),
and those plants have no outcome at all. Two observed counters make the third
derivable *and* visible; one counter quietly dresses up server operations as CT
skill. Same rule as `attempts` beside `wins` in ask 9.

**It rides in `player_round_stat` on purpose** — same key as `bomb_plants`, so
it inherits side, season and map (ask 17) for free, and "my plants survive on
Mirage and die on Nuke" is one query rather than a new table.

Not asked for, so it does not read as an oversight later: **no bombsite (A/B)
dimension.** That is a different question from this one, and this one is worth
having first.

Priority-wise this belongs with 8/9/10 below: nothing is being written *wrong*
in the meantime, it simply does not exist — but every plant played before the
column does is one whose outcome can never be recovered.

Note it is unrelated to bomb-explosion *deaths*, which are plain kills and stay
that way (recorded decision, `STATS-MODULE.md`).

**Built (OSBase, 2026-08-06), and both cautions above were honoured.** The
columns arrive via an `EnsureColumn` migration rather than a fresh
`CREATE TABLE`, since `player_round_stat` is already live — so there is no
backfill and no history before this deploy, exactly as the priority note says.
The planter is held in a single `plantedBySteamId64` slot (only one bomb can be
live at a time), resolved on a new `EventBombExploded` subscription or on
`EventBombDefused`, and cleared in both plus at round start as a safety net —
the same shape as the existing `roundDefuseBegan` state. `EventBombExploded`'s
`Userid` was deliberately not used.

Two things they did beyond the ask, both worth recording:

- **`plantedBySide` is stored with the planter**, so the row lands on the side
  the bomb was planted from even if that player switches team mid-round. The ask
  said the columns inherit `side` for free; that is only true if the side is
  captured at plant time, which is a detail the ask left implicit.
- **The slot only opens on a *counted* plant**, which is what makes the outcome
  inherit ask 11's gates automatically. Opening it on every plant would have let
  a warmup plant resolve inside a real round — a gate leaking sideways through
  state that outlives the event it came from.

They also found and fixed a pre-existing hole while wiring it: the DB-outage
retry-merge path around `unwrittenRounds` would have silently dropped both new
fields on a failed flush retry. Worth noting as its own small pattern — a merge
written against a column list is a place every future column has to be added by
hand, and nothing fails loudly when one is forgotten.

**The team-stat objection was raised and waved through** (owner, 2026-08-06),
recorded so it is not re-opened as though nobody had thought of it. The share of
your plants that detonate measures the round *after* the plant as much as the
plant itself — your team holding the site, a retake you had no part in. The
owner's answer: true, and it is still a stat worth having. Same family as the
Ace decision — a number people recognise and enjoy beats a stricter one nobody
asked for. The obligation it leaves is on the **wording**, not the data: the
heading must not claim it measures the planter alone.

### 27. What the knifed player was carrying

`knife_taser_kill_event` records the rarest moments on the server and keeps them
forever, deliberately — a couple a day is a thousand rows a year. It records who,
with what, where and when. It does not record **the wallet**, and that is the
part of a knife kill people actually retell: not that you knifed him, but that
you knifed him while he was sitting on 12 500.

```sql
-- knife_taser_kill_event
victim_money INT,   -- the victim's cash at the instant they died
killer_money INT,   -- the killer's cash at the same instant
```

**A premise that has to be checked before the columns are named, not after.**
The ask arrived as "knifing steals the victim's money" (owner, 2026-08-06). That
may be a plugin on these servers, or it may be the game's ordinary knife kill
award being remembered as a transfer. From the OSWeb side there is no way to
tell — OSBase's source is not readable here, and ask 25 is this document's own
record of what happens when that gets forgotten and an inference is written down
as a finding. **So this is a question for the OSBase side first: does anything
actually move the victim's cash to the killer, or is the reward the game's own?**

The columns above are the answer that survives either way, which is why they are
worded as wallets rather than as loot. `victim_money` is the story regardless of
where the money goes. ~~If there IS a transfer, the amount is `victim_money` and
needs no column of its own~~ — **that clause was wrong, and the answer below is
what proves it; see "the cap" further down.** The rest holds: a `stolen` column
written before the mechanic was confirmed would have been a number the site
invented, and this document's one rule means it would have been wrong forever
rather than briefly.

**The trap, and it decides what the numbers mean:** money is read at
`OnPlayerDeath`, and the kill award is applied by the game around the same
moment. Whether `killer_money` is before or after the reward is not obvious from
inside the handler, and both readings look identical in the column. Whichever it
is has to be established by observation on a live server — knife someone with a
known balance and read the row — and written down here, in the same spirit as
the side-encoding fix: an encoding nobody can verify from the data is one that
will be quoted wrongly later.

**No roll-up is asked for.** "Most stolen this season" is a SUM over an event
table that gains a thousand rows a year — cheap from the detail, and worth
leaving there rather than maintaining a counter for it.

**The rule from section 4 of `osbase-stat-contracts.md` applies here with more
force, not less: aggregates count the dealer, never the victim.** A total of
what you have knifed off people is a boast. A leaderboard of who has been robbed
most is a permanent record of somebody's worst evenings, and this site praises
rather than embarrasses. Both names stay on the individual row, as they already
do; only one of them is ever summed.

**Answered and built (OSBase, 2026-08-06). The premise was true: it is a real
transfer, and it is theirs, not the game's.** `Mug.cs` subscribes to
`EventPlayerDeath` and moves money whenever the weapon name contains `knife`
(a taser moves nothing at all): knife an opponent and their whole balance goes
to you; knife a team-mate and it runs backwards as a penalty, the killer paying
the victim.

**The read-order trap resolved itself rather than being guessed at**, which is
the part worth keeping. `AddKnifeTaserKill` reads the wallets inside
`DamageReport`'s own `OnPlayerDeath`; the event bus runs subscribers in
registration order, and modules load alphabetically by class name in
`OSBase.cs`, so `DamageReport` is guaranteed to have read before `Mug` moves
anything. Deterministic, not a race. It is also **a guarantee that breaks
silently**: any future module sorting before `DamageReport` that touches money
in the same event moves the numbers without any test failing. They put that
warning in the code beside it, which is the right place for it.

**Still open, and only a live server can close it:** whether CS2's own economy
has already credited a knife bonus to `killer_money` before the event reaches
plugins at all. Unreadable from source on either side.

**The cap is what makes `stolen` a real question again, and it is why the
struck-through clause above was wrong.** The transfer is bounded by the killer's
headroom under a local $16 000 ceiling, so what actually moves is not
`victim_money` but something like
`min(victim_money, 16000 - killer_money)` — and for a knifed team-mate the
whole thing inverts. Three consequences, in order of how much they matter:

1. **The site must not print "stole X" from the two wallet columns.** That
   formula hardcodes a constant owned by a different module, in a different
   repository, on the reading side — precisely the shape this document keeps
   catching. If the ceiling ever changes, every historical row silently starts
   being read against the wrong rule, and nothing anywhere says so.
2. **The wording carries it instead, exactly like ask 26's heading.** "Hade
   12 500 på sig" is true from `victim_money` alone, needs no constant, and is
   the sentence people actually say. That is the version being built unless
   somebody asks for the other one.
3. **A true "stolen" figure has one correct owner, and it is not
   `DamageReport`.** The ordering guarantee that solves the read problem also
   means `DamageReport` reads *before* the transfer and can never observe its
   outcome. Recomputing Mug's rule in a second place would be a copy that drifts
   the day the ceiling moves. If the number is wanted, **`Mug` should report
   what it actually moved** — the module that moves the money is the only thing
   that knows. Not asked for yet; recorded so the shape is settled if it is.

### 28. `Mug` has to report what it moved — because "bästa mugg" is a real board now

The owner wants a **best and worst mug** on the site (2026-08-06). That turns the
parked item at the end of ask 27 into an actual requirement, so it is written up
as its own ask rather than left as a note.

**Why `victim_money` alone cannot carry the board.** A superlative built on it
ranks *whose wallet was fattest when they died*, not what the knifer came away
with — and the cap is what separates those two exactly where a leaderboard is
most visible. A knifer already near the $16 000 ceiling tops the board on a
mugging that paid them almost nothing. The number would be wrong in its top row,
which is the one row everybody reads.

```sql
-- knife_taser_kill_event
money_moved INT NULL   -- signed, from the KILLER's side:
                       --   > 0  taken from the victim
                       --   < 0  paid to a knifed team-mate (Mug's penalty)
                       --   = 0  the transfer ran and moved nothing
                       --  NULL  Mug never ran for this kill: taser only, as of
                       --        the bayonet fix in v0.0.537. Rows written
                       --        BEFORE that release also carry NULL for
                       --        bayonet kills -- see the note below.
```

**`NULL` and `0` mean different things, deliberately** — the same call OSBase
made for `player_daily_stat.rating`, and for the same reason. A taser row is not
a mugging that came up empty; it is a kill the mechanic never touched. Collapsed
into one value, every taser kill would quietly join the "worst mug" board.

**And the taser's exclusion is a decision, not the bayonet bug wearing a
different hat** (owner, 2026-08-06: *"man muggar inte nån med taser bara
kniv"*). Recorded because the two are indistinguishable in the data — both are
`money_moved = NULL`, both are "a weapon that didn't move money" — and this
document now tells a prominent story about a knife that should have mugged and
silently didn't. Somebody reading that, then finding the taser, has every
reason to file it as the same bug found again.

It is the opposite. The bayonet *is* a knife to everyone holding one, and
excluding it matched nobody's expectation. A taser is not a knife to anyone,
and mugging is a knife mechanic. The shared classifier is what makes the
difference legible in code rather than incidental: `NormalizeWeapon` folds
every skin to `knife` and the taser spellings to `taser`, so `Mug` asking for
`knife` excludes the taser *by saying so*, not by a substring happening not to
match.

**`DamageReport` stays the table's only writer. `Mug` reports, it does not
write.** Two writers on one table is the guardrail this whole system keeps —
and here it is also the practical answer, since the row is `DamageReport`'s to
begin with.

**The ordering question is yours to answer, and it is a question, not a
finding.** `DamageReport.OnPlayerDeath` runs *before* `Mug` in the same event —
that is the guarantee ask 27 rests on — so the amount does not exist yet when
the row is built. Two shapes work, and only OSBase can see which fits:

- If knife rows are buffered to the round-end flush like the rest of
  `DamageReport`'s writes, `Mug` can fill the figure into the pending row within
  the same event, long before it reaches the database.
- If the row is written immediately, `Mug` hands the figure to `DamageReport`
  through a small call and `DamageReport` does the writing.

Either way the ordering that made the wallets trustworthy is what makes this
work: `Mug` runs second, so by the time it reports, the money has actually
moved.

**What the site will and will not do with it**, so the shape of the board is
agreed before the column exists:

- **Best mug: `MAX(money_moved)`,** naming the knifer. The victim appears on the
  individual row as they already do — a story with two people in it — but never
  as a ranked column. Section 4 of `osbase-stat-contracts.md`, unchanged.
- **Worst mug is two rows, not one** (owner, 2026-08-06, offered a choice
  between them and took both): the **stingiest** haul and the **inverted** one,
  where you knifed a team-mate and *paid* them (`money_moved < 0`). They are
  different jokes and neither subsumes the other, so the block runs to three
  rows: best, stingiest, inverted.
- **The stingiest row needs `money_moved > 0`, or it is not a row at all.**
  Plenty of people die broke, so a plain `MIN` lands on a zero shared by
  hundreds of muggings and picks a winner arbitrarily — a named "record" that
  changes every evening for no reason anyone can see. Restricted to muggings
  that actually paid something, it becomes what it was meant to be: the person
  who knifed someone for their last 50. Same family as the 50-kill floor on the
  season awards — a superlative over a degenerate set is not a superlative.
- **"Most robbed" is not a board and will not become one**, in any of the three.

Non-retroactive like everything else: every knife kill from now until this
column exists is a haul nobody can recover. It is *approximately* derivable
today from `min(victim_money, 16000 - killer_money)` — but that formula lives on
the reading side and hardcodes another module's constant, which is exactly what
point 1 of ask 27 says not to build.

**Answered by the OSBase side (2026-08-06): built, and one thing found along the
way weakens the stated NULL invariant above.** `Mug.cs` now reports the signed
figure into `DamageReport`'s already-buffered row through a small public
method (`ReportKnifeMoneyMoved`) rather than writing the table itself --
`DamageReport` stays the only writer, per the ask. The ordering this rests on
is the same guarantee ask 27 already established (module discovery sorts
`DamageReport` before `Mug`), so the row exists by the time `Mug` reports into
it.

**The gap: `Mug.cs` detects a knife kill by checking whether the raw weapon
name contains `"knife"`, and one real knife does not pass that check.**
`DamageReport`'s own `NormalizeWeapon` needs a *separate* `.Contains("bayonet")`
branch precisely because the bayonet's raw event weapon is `weapon_bayonet` --
it does not start with `knife`, and does not contain it either. `Mug`'s
condition is that same substring check, unmodified, so a bayonet kill never
enters `Mug`'s logic at all: no mugging, no punishment, nothing moved, for a
skin that is neither rare nor obscure. The row `DamageReport` still writes
(weapon classifies as `knife` there) ends up with `money_moved = NULL` --
not because the mechanic doesn't apply to knives, but because `Mug` silently
never saw the kill. **This is the same shape of bug ask 25 already catalogued
once: a plausible-looking condition that is wrong for one real case, found by
reading the source rather than assumed.**

**Owner's call (2026-08-06): fixed, and reframed on the way in.** Not a
balance decision nobody had made -- a bug nobody had chosen. A bayonet is a
knife to everyone holding one; nobody on the server knew that one skin made
them immune to the teamkill penalty and untouched by the mugging, silently,
for as long as `Mug` has existed. So: yes, bayonets are muggable, and the fix
is not "teach `Mug` about bayonets too" -- that repeats the exact mistake,
a second hand-maintained knife list that can drift from the first again on
the next skin that isn't `knife_*`. `Mug`'s own `.Contains("knife")` check is
deleted; it now asks `DamageReport.NormalizeWeapon` (made `public` for this)
the same question `DamageReport` already answers correctly. One definition,
asked from two modules, not two definitions kept in sync by hand. `NULL`
means taser again, exactly as first written -- the bayonet no longer produces
a `knife`-classified row with an unreported mug.

Shipping with 26/27/28, but as its own commit and its own line in the release
notes: the three stat asks change nothing anyone on the server notices, this
changes how the game behaves mid-session. Its own verification once live:
`money_moved` should stop being `NULL` on bayonet rows -- that was the bug's
signature, and it going away is the confirmation the fix landed.

**The fix shipped in v0.0.537, and it gave `NormalizeWeapon` a second job — that
is the thing to remember, not the bayonet.** It is now both a *display fold*
(every knife skin reads as `knife` in the stats) and a *gameplay gate* (Mug asks
it whether to move money). One function, two callers, two entirely different
consequences for being changed.

The trap that sets: the moment anyone wants per-skin knife stats — "which knife
do people actually get knifed with" is an obvious future ask — the natural fix
is to stop folding the skins together. That change is made for a stats reason,
looks like a stats change, and silently switches mugging off for every skin it
unfolds. Nothing fails, no test goes red, and the symptom appears on the server
as "the bayonet thing is back" months later.

Not a reason to keep two copies — the two copies are what caused the original
bug. It is a reason for the gameplay caller to be *visible from the classifier*,
so that whoever unfolds it is told, at the point of the edit, that money depends
on the answer. Same family as the load-order warning in ask 27: a guarantee that
holds today and breaks quietly is worth a comment where it would break, not
where it was discovered.

### 29. The map result — score per player per map, because a session is not a sum

The owner wants **best and worst score on a map** (2026-08-06). Nothing in this
schema can answer it: `score` appears in no table, and a map session is a
single-event figure that quarterly counters cannot be worked backwards into.
Same category as `biggest_win` in ask 12 — records are not derivable from
totals, ever.

```sql
player_map_result
  steamid64  VARCHAR(32),
  map        VARCHAR(32),
  season     VARCHAR(8),
  stamp      DATETIME,     -- when the map ended
  kills      INT,          -- what the board actually leads with
  deaths     INT,          -- so a 35-kill map can say what it cost
  score      INT,          -- the game's own scoreboard score, alongside
  rounds     INT,          -- how many rounds the map actually ran
  side_start TINYINT,      -- optional; which side they began on
  PRIMARY KEY (steamid64, map, stamp),
  KEY (map, score),        -- "best on Mirage"
  KEY (steamid64, stamp)   -- "my history on Mirage"
```

**A log, not a counter — and the row count is why that is affordable.** One row
per player per finished map: twenty players across five maps an evening is a
hundred rows a day, roughly 36 000 a year. That is the same order as
`player_daily_stat` and nothing next to `player_hit_stat`. Storing only a
best-so-far column would answer this one question and kill every other one:
score distribution ("is 40 good on Nuke?"), a player's own trend on a map,
"you have never broken 30 on Train".

**Kills lead, score rides along** (owner, 2026-08-06, giving the example that
settled it: *"om nån lyckas få 35 kills så kan man se det"*). That is the
sentence people say about a map — nobody retells a score of 78. So `kills` is
the column the board sorts on and the headline prints, and `deaths` sits beside
it so a monster map can say what it cost. Neither is derivable from anything
that exists: `player_daily_stat.kills` is a whole day and `player_duel_total` is
a whole season, and a map session cannot be cut back out of either.

`score` is still worth capturing in the same row — it is free at that moment,
it is the number on the screen everyone just looked at, and a board that
disagrees with the scoreboard is a support question. It simply is not the
headline.

**`score` must be the game's own number, read rather than computed.** CS2's
scoreboard score is not kills — it folds in assists and objective play, and the
weighting is the game's, not ours. If the site prints "score" and it disagrees
with what people watched on the end-of-map scoreboard, the number is wrong no
matter how defensible the formula was. Read the controller's score; do not
reconstruct it from the counters this document already collects.

**The timing trap, and it is the same shape as ask 27's:** it has to be read at
map end but *before* the scoreboard resets or players start dropping. Whichever
event that is, the row's meaning depends on it, so it belongs written down here
once it is known — not inferred later from the numbers.

**`rounds` is on the row so a short map cannot quietly set a record.** A map
abandoned after five rounds produces a small score, and an admin-restarted or
crash-shortened map produces a partial one; without the round count both sit in
the same board as a full 30-round map and are compared as equals. With it, the
reader can require a real map. Ask 11's gates apply on top as usual — a record
set 2v2 during warmup is not a record.

**The display decision the data does not settle, and it is the owner's**
(flagged 2026-08-06, deliberately not resolved here): a *server-wide* "worst
score on a map" board is the one feature in this document that runs against the
site's own rule — it praises rather than embarrasses, which is why ask 27 bans a
"most robbed" ranking and why the Ace and mug decisions went the way they did.
"Sämsta mugg" survives that test because it is a funny thing you *did* once;
"worst score on Mirage" is a ranking of who is worst at the game, held
permanently, with a name on it.

The version that keeps the joke and loses the pillory: **worst is personal.**
Your own worst map sits on your own profile beside your best — the honest
"where am I bad" number people actually want — while the *server* board only
ever ranks bests. The storage above supports either, so nothing is lost by
deciding this after the rows exist, which is the rare case in this document
where waiting costs nothing.

**Answered by the OSBase side (2026-08-06): built, one correction made to the
sketch, one trap left open exactly like ask 27's.**

`kills`/`deaths`/`score` are read from CS2's own fields, not computed:
`player.ActionTrackingServices.MatchStats.Kills`/`.Deaths` (the same
`m_iKills`/`m_iDeaths` schema members `TeamDamage.cs` already writes to for
the teamkill-penalty scoreboard adjustment -- confirmed to exist by
decompiling the installed CounterStrikeSharp API, not assumed) and
`player.Score` (`m_iScore`) directly on the controller.

**Correction:** indexed on `(map, kills)`, not `(map, score)` as the SQL
sketch had it. The sketch's own index comment ("best on Mirage") and the
prose two paragraphs later ("kills is the column the board sorts on and the
headline prints") disagree with each other -- read as the sketch predating
the settled decision, not as two separate requests, so the index follows the
prose.

**The open trap, same shape as ask 27's:** read at `Listeners.OnMapEnd`,
because `ServerInfo.cs` already established (its own grace-window comment)
that this specific listener fires before the map-change disconnect churn.
Whether CS2 has reset `MatchStats`/`Score` by that point is *not* verified
from source -- there was nothing in this codebase that needed to read final-
match numbers before this ask. Needs the same live check ask 27's
`killer_money` ordering does: finish a map with a known kill count on a known
player and read the row.

`rounds` counts only rounds where ask 11's gate was open (not the engine's
own round counter), so a map that spent most of its time under-populated
reports a low number instead of a misleadingly-real-looking one. `side_start`
is captured on the map's *first round start*, not `OnMapStart` itself -- team
assignment isn't guaranteed settled the instant a map loads -- and is
`SideUnknown` for anyone who joined after that point, which the ask already
called optional.

`player_map_result` is a brand new table (no migration needed, unlike asks
26-28's already-live ones).

### 30. How the round ended — `rounds_won` says that, never how

Raised by the owner (2026-08-06) while looking at the bomb columns: *"rundor kan
ju sluta på 3 olika vis, bomben sprängs, man skjuter alla fiender eller tid"*.
Correct, and it is more than three — a defusal is a fourth and a hostage rescue
a fifth if those maps are in rotation.

**The reason exists on `EventRoundEnd`, is used at that instant, and is thrown
away.** Ask 14 records `rounds_won`; nothing records how. So "you win on the
bomb, he wins on the timer" is unanswerable, permanently, for every round played
before this column exists.

**This one is already half-built on the site side, which is the argument for
it.** `docs/rank-and-points-design.md` pays *differently* per round end — bomb
detonated 5, defused 5, everyone eliminated 2, hostages rescued 10 — precisely
because the community distinguishes them. A points system that grades the
outcome, over data that cannot say what the outcome was, can never show anyone
why their round points differ.

```sql
-- player_round_stat, joining side/season/map in the PRIMARY KEY
end_reason TINYINT   -- the GAME's own EventRoundEnd reason value
```

**A key dimension, not five columns** — same call ask 9 made for clutch
`opponents` over `clutch_1v1 … clutch_1v5`. A new reason value is then a new
row rather than a migration, and `rounds`/`rounds_won` keep summing back to
exactly what they mean today. Row cost stays in the cheap category ask 17
established for this table: a player holds one row per side × season × map ×
reason, so a ten-map rotation runs to roughly a hundred rows a quarter.

**Store the game's own value. Do not invent an encoding.** This document has
already paid for that lesson once — `side` shipped as a private 0/1 scheme and
had to be truncated and rewritten as CS2's real team numbers, because an
encoding nobody can verify from the data is one that gets quoted wrongly and
tested against its own wrong assumption. Same rule here, same reason: write
whatever `EventRoundEnd` carries, then **verify the mapping against real rows on
a live server and write the observed table into this document** — not the enum
somebody remembers.

**An asymmetry worth knowing before anyone reads a chart — and the first
version of this paragraph got it wrong.** It said "the CTs take the round when
time expires, so *won on time* is a CT-only row". **The owner corrected it
(2026-08-06): on a hostage map the clock running out is a T win** — the CTs are
the side under the deadline there, because rescuing is their job.

So "time ran out" is not one outcome with a fixed winner. It is **two different
reasons with opposite winners**, and which one can occur is decided by the map
type. That is a better argument for the shape than the wrong version was:
`map` is already in this key from ask 17, so a hostage map's reasons simply
never appear on a `de_` row, and the pair never has to be told apart by
guesswork.

What survives from the mistake is the display warning, now for the right
reason: **do not label a column "wins by time" and put the two sides beside
each other.** Which side that favours depends on the map, so a single bar
labelled that way is two different facts stacked into one, and a chart is the
worst place to discover it.

The mistake itself is the one this document keeps recording: a rule that is
true for the maps you happen to think of first, stated as if it were the game's
rule. Bomb maps are the default in everyone's head — and the servers run
hostage maps too.

**It also gives the bomb columns their denominator.** Ask 26 counts what
happened to *your* plants; this counts how *rounds* ended. Together they answer
the thing neither can alone: whether a map is decided on the objective at all,
or whether the bomb is mostly a formality on a server that fights it out.

**Settled 2026-08-06 by truncating the counter tables, so `end_reason = 0`
means exactly one thing.** The 225 rows that existed when the migration ran
were pre-`end_reason` rows collapsed onto the default — harmless in
themselves, and distinguishable by `first_seen` predating the deploy. What
made them worth removing is what happens *next*: an unresolved round drains
under `0`, and for a player who already had a `0` row on the same
`(side, season, map)` that is an `UPDATE` of the old row, not a new one.
`first_seen` is INSERT-only, so from that moment the row would have meant both
"recorded before the column existed" and "this round never resolved", with no
way left to separate them. A documented ambiguity that repairs itself by being
deleted while it is three hours old is not worth keeping.

**What was deliberately NOT reset, and why the list matters more than the
truncate.** `player_hit_stat`, `player_weapon_shots`, `player_round_stat`,
`player_duel_stat`, `player_duel_total`, `player_clutch_stat`,
`player_multikill_stat` and `knife_taser_kill_event` all went — days-old test
play with no reader on the site. Left alone:

- **`elo_rating` / `elo_points` / `elo_kill_event`.** Ask 23's entire argument
  for running ELO in parallel since 1 August is that a *rating needs a
  run-up* — points can start from zero, a rating cannot. Truncating it would
  put every player back on the default, leave the balancer treating the whole
  server as roster median, and the only way to earn that week back is to play
  it again. It is the one thing here that costs time rather than rows.
- **`player_teambet_*`.** Real balances, exactly as in the side-encoding
  truncate a day earlier.
- **`skill_log`.** Untouched by any of this, and the form curve's only source
  until ELO takes over.
- **`player_daily_stat`.** The judgement call, kept on purpose. It holds
  end-of-day rating and points snapshots, and ask 22 says the strongest thing
  in this document about any table: it loses *time itself*. Five days of curve
  is not much, and it is five days nobody can reproduce. The price is a known
  inconsistency — the daily table carries history the quarterly tables no
  longer do — which is acceptable only because this document already forbids
  summing the two together, and `first_seen` answers "since when" for the new
  ones.

**This one was written, then lost to a concurrent edit, then rewritten**
(2026-08-06) — both sides had the same file open in the same working tree, and
neither had committed. Worth a line of its own: `osbase-contracts-readme.md`
argues for one home rather than drifting copies, and it is right, but one home
with two uncommitted writers is not version control — it is last-write-wins with
no conflict to notice. **Commit between hand-offs.** Git can merge two edits to
different sections of a file; an unsaved working tree cannot.

**Correction (OSBase side, 2026-08-06): "same working tree" was itself the kind
of unverified claim this document keeps catching.** Checked from OSBase's side:
one repository (`github.com/Pintuzoft/OSBase`), the file lives at the repo
root, commit `4013832` is real and verifiable there
(`git cat-file -t 4013832` → `commit`). There is no `docs/` copy anywhere in
this environment, no second working tree, nothing else under the same parent
directory. OSWeb's side reported a different path (`docs/traffkarta-hit-stats.md`)
and that the same hash does not resolve for them. Two independently-checkable
facts, same conclusion: **these are two separate files in two separate
repositories**, not one shared working tree — whatever has been keeping them in
sync is not git. A commit on OSBase's side protects OSBase's copy only; it
cannot protect OSWeb's. Both sides need their own commit, in their own repo, and
neither can verify the other's from where they sit — which is itself the
argument for writing down what was actually checked (`git cat-file`, a path, a
hash) rather than restating what felt true, the same discipline ask 25 already
cost a rewrite to learn.

**Answered and built (OSBase side, 2026-08-06).** `RoundEndReason.Unknown = 0`
is confirmed real, not asserted from memory — decompiled directly off the
installed `CounterStrikeSharp.API.dll`
(`CounterStrikeSharp.API.Modules.Entities.Constants.RoundEndReason`), same
standard ask 25 asked for and the side-encoding fix set. Pre-existing rows
migrate to `end_reason = 0`, the game's own "we don't know" value, not an
invented one.

The primary-key change goes through its own migration
(`EnsureEndReasonInPrimaryKey`), not the `EnsureColumn` helper the other asks
used — `ADD COLUMN end_reason ..., DROP PRIMARY KEY, ADD PRIMARY KEY (steamid64,
side, season, map, end_reason)` as one statement, so there is no window where
the column exists but the old four-part key is still live. **Safe against the
active-writer concern by construction, not by a manual deploy step**: this only
runs from `CreateTables()`, which the module's own lifecycle (`OnLoad()` before
`RegisterHandlers()`) already guarantees happens before this module has any
event subscription open, on both a cold start and a hot reload (`Unload()`
tears down the old handlers and flushes first). The deploy-ordering caution was
right to raise; it turned out to already be enforced by code that predates this
ask, not something to add.

**One consequence the ask's SQL didn't spell out, worth recording because it
touches already-live counters, not just this one:** `end_reason` joining the key
means EVERY column on this table splits by it — `bomb_plants`, `bomb_defuses`,
`plants_exploded`, `plants_defused` too, not only `rounds`/`rounds_won`. Those
four are written from events that fire mid-round (`EventBombPlanted`,
`EventBombDefused`, `EventBombExploded`), before `EventRoundEnd` has told
anyone how the round ended. They now land in a round-scoped staging area first
and get folded into the real, `end_reason`-keyed counters only once
`EventRoundEnd` fires and the reason is known — one extra step, not a change to
what any of the four mean.

**One thing the staging layer opens, and it is a question rather than a
finding:** the round-scoped buffer holds the bomb counters until
`EventRoundEnd` names the reason. So what happens to a round that never
produces one — a map change mid-round, `mp_restartgame`, a crash, the server
emptying? If the staged counters are simply dropped, a plant that genuinely
happened stops being counted, where under the pre-`end_reason` shape it was
written the moment it occurred. That is a real behaviour change hiding inside
a schema change, and it would show up only as bomb totals being quietly a
little lower than before.

The fix, if that is what happens, needs no new concept: **flush the staged
counters under `RoundEndReason.Unknown` (0)** — the value already confirmed to
exist, and already what pre-migration rows carry. An unresolved round then
becomes a visible bucket instead of a silent subtraction, which is the same
rule as `attempts` beside `wins` and as a plant with no outcome in ask 26.

**If that is done, `0` carries two distinct meanings and the document should
say so once**: "this row predates `end_reason`" and "this round never
resolved". They are different facts, and one value holding both is the exact
shape that made the old `side` encoding unreadable. Here they stay separable,
but only because of something already built — ask 13's `first_seen` is on this
table, so a pre-migration row is the one whose `first_seen` precedes the
deploy. Worth writing down, because that is not obvious to anyone reading the
column alone.

**Confirmed and fixed (OSBase side, 2026-08-06): that is exactly what would
have happened, and it is fixed the way this section already worked out.** The
`OnRoundStart` safety net was clearing the staging buffer outright, on the same
line as the genuinely-in-progress state it sits beside (`roundDefuseBegan`,
`roundClutchCandidates`, …) — those are correct to drop, since their outcome
never happened without a round end. The bomb counters are not the same shape:
pre-`end_reason` they wrote unconditionally, so dropping them here was the
regression this section predicted, hiding inside a schema change exactly as
described.

Now drains under `RoundEndReasonUnknown` (a named constant, `= 0`) instead of
clearing, with the two-meanings-of-0 point written directly on it in code —
`first_seen` against the migration's deploy date is how a reader tells a
pre-migration row from a round that genuinely never resolved. Nothing else in
this table changed shape: `rounds`/`rounds_won`/`defuse_fails` were already
only ever written from inside `OnRoundEnd` itself, before or after this ask, so
they were never at risk the way the bomb counters were.

### 31. The other objective — hostages

Raised by the owner (2026-08-06) straight out of the hostage-map correction in
ask 30: if the clock behaves differently on `cs_` maps, the maps are in
rotation — and **nothing anywhere counts a rescue.** The bomb has five columns
between asks 8 and 26. The hostages have none, while
`docs/rank-and-points-design.md` already pays 5 for a rescue and 10 to the team
for rescuing them all. Same gap as ask 30's, one objective over.

```sql
-- player_round_stat, beside the bomb counters (asks 8 and 26)
hostages_rescued INT,   -- hostages this player brought home
hostages_killed  INT,   -- and the ones they shot
```

**It counts hostages, not rounds** — a round can hold several, and the event
fires per hostage. Worth saying because the two are different numbers and only
one of them is being stored: "rounds in which you rescued someone" cannot be
derived from a hostage count afterwards. If that turns out to be the number
people actually mean, it is a separate column and a separate ask, not a
reinterpretation of this one. Same rule as the Ace decision.

**`hostages_killed` is recorded and will never be ranked** (owner, 2026-08-06:
"hostages killed kanske också är statistik" — yes, and it is worth being
precise about what that permits). Shooting a hostage is a real thing that
happens, mostly by accident, and the game already punishes it — HLstatsX
scored it −15. Keeping it costs one column and makes a profile honest about a
bad night; building a "most hostages shot" board out of it would be the
mug-victim leaderboard in a new hat.

**The line this is the third example of, so it is worth stating once as a
rule.** The site praising rather than embarrassing has never been an argument
against *recording* an unflattering number, or against *showing someone their
own*. It is an argument about one thing only: **sorting people against each
other by it.** Your own worst map on your own profile (ask 29), the mugging
that paid you nothing (ask 28), the hostage you shot (here) — all three are
fine, and the first two were argued from scratch before this was said plainly.

The test that separates them: **does the number appear on a page the subject
did not open?** A profile is somewhere you look someone up, and a bad figure
there sits inside the whole picture of a player. A leaderboard is a page about
everyone, and being on the wrong end of one is a thing that happens *to* you,
repeatedly, without your ever visiting it.

So: capture everything the events carry — the unflattering half is
non-retroactive exactly like the rest, and a column withheld out of tact is a
column nobody can add back. Decide what gets *sorted* separately, and later.

**Only one of these two columns is CT-only, and saying "these" was the same
mistake as ask 30's first draft, one ask later** (owner, 2026-08-06): a rule
true of the case in front of you, stated as if it covered the rest. "Hostages
are a CT thing" is true of the *objective* — a Terrorist cannot rescue, so
`hostages_rescued` really is zero on every T row, a fact rather than a gap,
and a chart that lines the two sides up beside each other for it will look
broken for exactly the reason the "wins by time" one would.

`hostages_killed` is not that column. Nothing stops a Terrorist from shooting
a hostage, and the event credits whoever pulled the trigger — `Userid` on
`EventHostageKilled` — not CTs specifically. It can carry either side, and
`side` is in the key precisely so the two columns are never forced to agree.

**And they are zero on every `de_` map**, which `map` in the key (ask 17)
makes readable rather than mysterious. A player with no hostage numbers has
either never played `cs_` or never gone for it, and the map breakdown is what
tells those two apart.

**Event names deliberately not asserted here.** CS2 reworked hostages from
"follow me" to carrying, so whatever the API calls the rescue, the pick-up and
the death is for the OSBase side to read off the actual enum rather than for
this document to guess — the rule ask 25 exists to enforce. What is being asked
for is the two counts, not a particular handler.

With this, the objective picture is complete: what you did with the bomb
(ask 8), what became of it (ask 26), what you did with the hostages (here), and
how the round actually ended (ask 30).

### Priority between these asks

Not all of them are equally urgent, even though all of them are aggregates:

- **5, 6, 7 touch a table that is already being written.** Every evening that
  passes without them is history that cannot be reconstructed — and the writing
  has already started.
- **8, 9, 10 and 12 are new counters.** Their history is equally unrecoverable,
  but nothing is being lost *incorrectly* meanwhile; they simply do not exist
  yet. They can wait for a second pass without any of it being written wrong.
- **11 is the most urgent of the lot.** It is not a counter but a set of gates,
  and every round recorded without them is a round of warmup, bot and
  empty-server noise permanently mixed into the real numbers. Gates first, then
  counters.
- **9 needs the most thought, but not new plumbing.** Bomb, multi-kill and the
  duel flags are all "the event already carries it, write it down". A clutch has
  to be *derived* — but from round state OSBase already keeps until round end,
  so it is logic on top of what exists rather than a new subsystem.

**Row cost is fine.** These are counters, not events: pairs who actually met ×
~2 side combinations × the weapons they actually used on each other. A player
dies maybe twenty times an evening, so this stays orders of magnitude smaller
than `player_hit_stat`.

## Conventions to keep

- `VARCHAR(32)` steamids, InnoDB, utf8mb4_unicode_ci, `CREATE TABLE IF NOT
  EXISTS` at OnLoad — same as the existing four.
- **Counters, not raw events** (raw would be millions of rows a year).
- **Record every hitgroup, including 0/9/10 — filter at render, not at write.**
  Of the eleven slots only eight are body places worth drawing (Head, Chest,
  Stomach, L-Arm, R-Arm, L-Leg, R-Leg, Neck); Body(0) is the generic fallback,
  Gear(10) an equipment hit, U9(9) unused. But a generic or gear hit is still
  real damage and a real hit, so dropping them at write time would quietly break
  the totals that read across all of them — damage, ADR, and
  accuracy (`hits ÷ shots`, where a round that landed in someone's kit *is* a
  hit). OSWeb already splits it the right way: the diagram filters to the eight,
  while the weapon/damage totals sum every group.
- **Weapon names normalised the same way the server log is** — every knife skin
  folds to `knife`, taser spellings to `taser`, `_projectile` suffix stripped —
  so these join cleanly against OSWeb's own `player_kill_stat`. See
  `ServerKillTracker::normaliseWeapon`.

## Hook points

- `EventPlayerHurt` → `player_hit_stat` (+ `player_weapon_shots` on fire)
- `EventPlayerDeath` → `player_duel_stat`, including the kill-flag counters
  (ask 6) and `dominated`/`revenge` (ask 7); also the running per-player kill
  tally for multi-kills, and the alive-count that tells you a clutch has begun.
  A knife or taser kill additionally writes `knife_taser_kill_event` with both
  wallets read *before* `Mug` moves anything (ask 27), which `Mug` then reports
  back into as `money_moved` (ask 28) — the ordering is load-order, not luck
- `EventBombPlanted` / `EventBombDefused` / `EventBombBeginDefuse` → the bomb
  counters (ask 8); the plant also opens round state holding the planter, which
  `EventBombExploded` / `EventBombDefused` then resolve into `plants_exploded` /
  `plants_defused` (ask 26)
- hostage rescue / hostage death → `hostages_rescued`, `hostages_killed`
  (ask 31 — event names to be read off the API, not assumed)
- round end → the rounds counter, the multi-kill row for each player's final
  tally (ask 10), the resolution of any clutch attempt opened during the round
  (ask 9), and `end_reason` (ask 30), which is also the moment the round's
  staged objective counters are drained into their real rows
- **round aborted without an end** (map change, `mp_restartgame`, the server
  emptying) → the same drain, under `RoundEndReason.Unknown` (0), so an
  interrupted round is a visible bucket instead of a silent subtraction
  (ask 30)
- `OnMapEnd` → one `player_map_result` row per player, read before the
  scoreboard resets (ask 29)

## What OSWeb does with it

Read-only, via `OsbaseDatabase` (`config/osbase.php`). Built and running as of
2026-07-21:

- **`HitStatRepository`** aggregates `(hitgroup → hits)` and `(hitgroup →
  damage)` per player, filtered by direction (dealt = the offensive tab,
  received = the defensive one), weapon and side. That vector of eight numbers is
  exactly what the locked heatmap engine takes. It also reads
  `player_weapon_shots` for real accuracy and `player_round_stat` for ADR.
- **`DuelStatRepository`** builds the per-weapon top lists ("dödat" / "dödad
  av"), the head-to-head balance, the teamkill ledger, and the **nemesis
  verdict** — a weighted judgement over five factors with a 20-meeting floor:
  difficulty 50% (how often they beat you, and how much worse you do against
  them than their other victims do), history 30% (how much you have actually
  fought, on a square-root curve to 500 meetings), style 20% (knife/taser and
  headshot share). The
  "worse-against-you-than-against-everyone" factor is what stops the server's
  best player being everyone's nemesis at once; the history factor is why a
  four-hundred-fight rivalry outranks a twenty-five-fight one at the same loss
  rate — a grudge is built over hundreds of fights rather than inferred from a
  ratio, which is why history carries nearly as much as difficulty, while
  staying bounded so that "whoever you play with most" can never win outright.
  **Everyone with a qualifying opponent gets a nemesis**, including a
  player who beats the whole server — theirs is simply whoever comes closest. An
  earlier version required a losing record and left the best players with no
  verdict at all, which is the opposite of what the feature is for. Weights and
  the two style ceilings are calibration guesses marked as such in the code —
  nobody has real numbers yet.

Two things about the reads, since they shape what OSBase can safely change:
a missing `side` column is detected rather than assumed (`SHOW COLUMNS`), so an
older build degrades to side-less totals instead of reading as "no data"; and
every read returns empty on failure, which means a genuine SQL error looks
exactly like an empty table. Until the tables have rows the diagram shows "ingen
data än" — never a fabricated body.

**The log-parsed stopgap is gone** (2026-07-21, migration 0169).
`player_kill_stat` and `ServerKillTracker` existed only because OSBase computed
hit data each round and discarded it. Keeping both would have put two different
kill totals on one profile page, widening every evening — they count to
different rules by design, since the gates in ask 11 are OSBase's and this
parser never had them.

The log stream itself stays. It still feeds the live connection map, the nick
history and the scoreboard, and it is the only source that sees a player's IP,
which is what the map's geo lookup is built on. Those are presence, not
statistics, and nothing competes with them.

Weapon-name normalisation therefore now lives on the OSBase side alone. OSWeb's
only remaining knowledge of weapon ids is the display map in
`public/assets/js/traffkarta.js`, which turns them into names for the profile —
so if OSBase folds two spellings differently, that map is where it shows up.

## GDPR

Everything above is keyed to a SteamID, which makes it personal data. OSWeb's
`OsbaseStatsRepository::ERASE` lists the OSBase tables an account erasure must
clear — **any new table here has to be added to that list**, or it leaks past an
erasure. It needs `DELETE` granted to the OSWeb DB user on the OSBase database.

**`skill_log` was missing from this list until 2026-07-22**, and it is the
oldest OSBase table of the lot — GameStats' per-map skill history, keyed on
`steamid` (not `steamid64` like the rest). Nobody decided against it: the list
was assembled while building the träffkarta, and `skill_log` predates all of
that, so nothing about the work pointed at it. Exactly how `player_weapon_shots`
came to leak, and a second demonstration that a list assembled from whatever is
being built at the time will miss whatever was already there.

Worth stating because unloading GameStats will not fix it — the table stops
growing and stays, holding years of it.

**This section used to copy the list out, and the copy went stale — so it
does not any more.** It read "both lists now name the same thirteen (checked
against OSBase's DDL 2026-07-21)" until 2026-08-06, by which point the real
list held **twenty-eight tables**: 22 deleted outright, 7 anonymised, with
`elo_bonus_event` in both because it names a person in one column and a
counterparty in two others. Missing from the snapshot were, among others,
`knife_taser_kill_event`, `player_teambet_log`,
`player_teambet_matchup_stat`, `player_map_result`, `elo_bonus_event`, the
`weapon_event_*` family and the theme-weekend tables.

**Nothing had leaked.** Every one of those had been added to the code on the
day it started mattering — that discipline held. What drifted was this
paragraph, which nobody had a reason to open while doing it.

**So the authority is `OsbaseStatsRepository::ERASE` and `::ANONYMISE`, and
this document deliberately does not restate their contents.** A prose copy of
a list that changes weekly is a second home for a fact — the exact thing
`osbase-contracts-readme.md` argues against — and it fails in the worst
direction, because a stale erasure list reads as reassurance. Read the
constants; they carry a comment per table saying why it is on the list, which
is the part worth writing down and the part that does not go stale.

**Found by OSBase diffing this paragraph against their own table constants
(2026-08-06), not by anyone here noticing.** Same shape as the `skill_log`
miss it already records, one level up: that was a table nobody's current work
pointed at, this was a paragraph nobody's current work pointed at. The rule
that catches both is the one the teambet tables demonstrate — ask whether the
list covers what was just built, in the same change, rather than trusting a
sentence that was true once.

The split itself is stable and worth stating in prose, because it is a
judgement rather than an inventory: **a table that names one person is
deleted; a table that names two is anonymised**, so an erasure never destroys
the other person's half of a shared moment. Every knife kill, duel and bonus
event has two people in it.

`server_stat_season` is deliberately absent from both lists and should stay
that way: it holds server-wide totals with no steamid column, so there is
nothing in it belonging to any individual to erase.

OSWeb names the last five **before the tables exist**, which needed one
deliberate change: the erasure tolerates "unknown table" (SQLSTATE 42S02) and
nothing else. A table that is not there holds nothing to erase, so skipping it
is safe — while unreachable, refused and permission-denied still abort the
erasure, because those mean the data is there and we failed to reach it.

That tolerance is what lets the list run ahead of the schema instead of behind
it. Adding each table on the day it goes live is precisely how
`player_weapon_shots` came to leak: nothing anywhere notices a table nobody
remembered.
