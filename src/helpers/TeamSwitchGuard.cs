using System.Collections.Generic;
using CounterStrikeSharp.API;

namespace OSBase.Helpers;

// Switching team while alive makes the engine kill you, and that death arrives as
// player_death with no attacker -- indistinguishable from a fall or a drowning to anything
// that classifies suicides. Owner's rule (2026-10-24): a team change is never a suicide,
// whoever caused it -- not on the scoreboard (TeamDamage), not in player_duel_total
// (DamageReport). One shared stamp table so both consumers agree on what a team switch is.
//
// Two sources stamp a userid (the key player_death gives us):
//   - the jointeam command, seen Pre so it lands BEFORE the death it causes (self-switch)
//   - player_team with a real old->new team change (admin swap, ChangeTeam from another
//     plugin, anything that never went through jointeam) -- which the engine fires AFTER
//     the death. Consumers therefore either defer their penalty (TeamDamage) or undo it
//     when the stamp arrives late (DamageReport, whose counters are still in memory).
// Stamps are not consumed on read -- there's more than one reader -- they simply age out
// of the window. The window is generous relative to the real gap (same tick) but far too
// short for a genuine suicide to follow a team switch: you're dead by then. A refused
// switch (same team, team full) leaves a stale stamp the window ages out on its own.
//
// Each module registers its own jointeam listener / player_team handler and forwards here,
// rather than one module owning the feed: a disabled TeamDamage must not silently turn
// DamageReport's suicide counter back on for team switches. Double stamping is harmless.
public static class TeamSwitchGuard {
    public const int GraceTicks = 64;

    private static readonly Dictionary<int, int> stamps = new();

    public static void Stamp(int userId) {
        stamps[userId] = Server.TickCount;
    }

    // Was there a team-switch stamp for this userid within the window around deathTick,
    // on either side of it?
    public static bool IsTeamSwitch(int userId, int deathTick) {
        if (!stamps.TryGetValue(userId, out int tick)) {
            return false;
        }

        if (System.Math.Abs(deathTick - tick) > GraceTicks) {
            stamps.Remove(userId);
            return false;
        }

        return true;
    }
}
