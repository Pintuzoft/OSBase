# OSBase → one player's console: what the site would need

The site wants to tell a kicked or banned player something they can still read
**after** they have been removed from the server. Everything reachable today fails at that, and the
reasons are recorded in `config/rcon.php`:

- `kickid <userid> "text"` and SimpleAdmin's `css_kick #<userid> <reason>` both
  land in the same fixed **"Kicked by server."** dialog. CS2 picks that string
  from an engine enum; the argument is ignored.
- A HUD panel does not outlive the kick. Tested with the admin menu open on
  purpose — the panel went with the session.
- Chat does not either. A `css_psay` was sent a beat before the kick and the
  client's own console was empty afterwards.

A plugin *can* write to a single player's console — `PrintToConsole` exists in
CounterStrikeSharp, the way it did in SourceMod. Nothing in the plugin set the
site can reach exposes it over RCON: SimpleAdmin gives `css_say` (chat, all),
`css_psay` (chat, one), `css_csay` (centre, all) and `css_hsay` (HTML HUD, all).
None of them is a console.

So this is the ask. It is small.

## Answer this before building anything

**Does the CS2 client keep console output across a disconnect?**

This costs nothing to find out and decides whether the rest of the document is
worth reading. Join a server, let some console output accumulate, leave
normally, and press `` ` `` on the main menu.

- Output still there → build the command below.
- Console cleared → **stop**. No plugin can put a message somewhere the client
  is about to wipe, and the site should keep announcing kicks to the room
  instead, which is what it does today.

The earlier chat test does not answer this on its own. It was sent with no delay
before the kick, so an empty console afterwards is equally consistent with "chat
is never mirrored to console" and with "the line never arrived in time" — and
neither of those is the question above.

## The command

```
osbase_console #<userid> <text to end of line>
```

- **Registered as a server command**, callable over RCON. Not a chat command:
  the site talks to the server through RCON and has no player to type as.
- **`#<userid>`** — the same handle every other moderation command here takes,
  and the one the site already holds for each row in its roster. A steamid64
  form may be added, but is not needed: this is only ever sent to somebody who
  is on the server right now.
- **Text runs to the end of the line**, unquoted, like `css_say`. The site
  strips `"` `;` CR and LF before sending, so the argument is safe to pass
  through, but do not assume it: an unknown userid, an empty message or a player
  who has already left should be a no-op, not an error and not a broadcast.
- **A literal `\n` in the text means a new line** — two characters, backslash
  and n, not an actual newline, which RCON cannot carry anyway. Split on it and
  print each piece separately.

  This is not cosmetic. The site's RCON layer opens a connection, authenticates,
  sends one command and closes it, every single time — so a seven-line banner
  sent as seven commands is seven connect-and-auth round trips in the moment
  before a kick, and any one of them can be the one that arrives too late. One
  command, one round trip, and the block either lands whole or not at all.
- **Goes to that player's console only.** Not chat, not the server console, and
  not anybody else's.
- **Sends before it returns.** This is the part that matters and the part that
  is easy to get wrong. The site sends this command and then `kickid` in the
  same breath — if the plugin queues the print for the next tick, the player is
  gone before it leaves the server and the whole feature does nothing. If it
  cannot be flushed synchronously, say so, and the site will keep a small delay
  on its side instead.

## What the site will do with it

One command, sent immediately before the one that removes them, drawing a block
that is hard to scroll past in a console full of engine chatter:

```
osbase_console #2 ****************************************\n* YOU WERE KICKED FROM OLDSWEDES.SE\n* REASON: Griefing\n* ADMIN:  Pintuz\n* Appeal at oldswedes.se\n****************************************
```

which the player sees as:

```
****************************************
* YOU WERE KICKED FROM OLDSWEDES.SE
* REASON: Griefing
* ADMIN:  Pintuz
* Appeal at oldswedes.se
****************************************
```

A ban is the same block with a length in it, and it matters more there — a
kicked player can reconnect and find out, a banned one cannot, and how long is
the first thing they will want to know:

```
****************************************
* YOU WERE BANNED FROM OLDSWEDES.SE
* LENGTH: 1 week
* REASON: Aimbot
* ADMIN:  Pintuz
* Appeal at oldswedes.se
****************************************
```

Permanent bans say `LENGTH: permanent`. The site already holds all of it: length
and reason from the modal, the admin from the session, the target's name from the
roster.

ASCII only, and no colour tags — a console is monospace text, `{green}` would
print itself, and this is not the place to find out whether an em dash survives
the encoding.

The room still gets its own announcement in chat, and that stays either way —
different audience. The console line is for the person who was removed; the chat
line is for the people who were being bothered by them.

Nothing else changes. If this command never arrives, the site behaves exactly as
it does today.

## Where it plugs in

`ServerAdminController::command()` already has the block: it resolves the
target's name, builds a line and sends it before the kick command, inside its own
try so a failure there never stops the removal. It sent a `css_psay` there until
that was tested and found not to survive. Swapping in the new command is a few
lines, plus the same block extended to the ban actions, plus one entry in
`config/rcon.php`.

Bans go through four actions rather than one (`ban`, `banid`, `banid_engine`,
`banid_engine_steam`), and two of those target somebody who has already left —
there is no console to write to there, so those two get nothing and should not
pretend otherwise.

