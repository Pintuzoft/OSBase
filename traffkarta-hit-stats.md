# Player stats collection — what OSBase should record

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

`side TINYINT` — 0 = T, 1 = CT, 2 = unknown.

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
  attacker_side TINYINT,        -- 0=T 1=CT 2=unknown
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
at ELO, and keep the cs2rank seasons reachable as an archive, clearly labelled
as the old system. A member who topped a 2025 season should still be able to
find it. The two numbers must not be compared, since they measure different
things — which is an argument for labelling the archive plainly, not for
throwing it away.

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
  tally for multi-kills, and the alive-count that tells you a clutch has begun
- `EventBombPlanted` / `EventBombDefused` / `EventBombBeginDefuse` → the bomb
  counters (ask 8)
- round end → the rounds counter, the multi-kill row for each player's final
  tally (ask 10), and the resolution of any clutch attempt opened during the
  round (ask 9)

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

**Both lists now name the same twelve** (checked against OSBase's DDL
2026-07-21): `elo_rating`, `elo_points`, `elo_kill_event`, `player_hit_stat`,
`player_weapon_shots`, `player_round_stat`, `player_duel_stat`,
`player_clutch_stat`, `player_multikill_stat`, `player_teambet_stat`,
`player_daily_stat`, `player_duel_total`. `elo_kill_event` and
`player_duel_stat` are each cleared from BOTH the attacker and victim column;
the rest key on `steamid64`.

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
