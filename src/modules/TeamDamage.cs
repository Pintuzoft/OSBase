using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules;

public class TeamDamage : ModuleBase {
    public override string ModuleName => "teamdamage";

    private const int TEAM_T = (int)CsTeam.Terrorist;
    private const int TEAM_CT = (int)CsTeam.CounterTerrorist;

    // Switching team while alive makes the engine kill you, and that death arrives as
    // player_death with no attacker -- indistinguishable from a fall or a drowning. Added
    // 2026-10-24 after a player got the suicide penalty for swapping sides mid-round; owner's
    // rule is that only a real suicide or a teamkill costs -1, never a team change, whoever
    // caused it. Two sources stamp the player's userid (the key player_death gives us):
    //   - the jointeam command, seen Pre so it lands BEFORE the death it causes (self-switch)
    //   - player_team with a real old->new team change (admin swap, ChangeTeam from another
    //     plugin, anything that never went through jointeam) -- which the engine fires AFTER
    //     the death, so the penalty itself is deferred by a few ticks to give that stamp a
    //     chance to arrive first.
    // A stamp within the window on either side of the death means no penalty and no chat
    // line. The window is generous relative to the real gap (same tick) but far too short
    // for a genuine suicide to follow a team switch -- you're dead by then. A refused switch
    // (same team, team full) leaves a stale stamp that the window ages out on its own.
    private const int TeamChangeGraceTicks = 64;
    private const float SuicidePenaltyDelay = 0.2f;
    private readonly Dictionary<int, int> teamChangeTick = new();

    protected override void RegisterHandlers() {
        osbase?.SubscribeToEvent<EventPlayerHurt>(OnPlayerHurt);
        osbase?.SubscribeToEvent<EventPlayerDeath>(OnPlayerDeath);
        osbase?.SubscribeToEvent<EventPlayerTeam>(OnPlayerTeam);
        osbase?.AddCommandListener("jointeam", OnCommandJoinTeam, HookMode.Pre);
    }

    protected override void UnregisterHandlers() {
        osbase?.UnsubscribeFromEvent<EventPlayerHurt>(OnPlayerHurt);
        osbase?.UnsubscribeFromEvent<EventPlayerDeath>(OnPlayerDeath);
        osbase?.UnsubscribeFromEvent<EventPlayerTeam>(OnPlayerTeam);
        osbase?.RemoveCommandListener("jointeam", OnCommandJoinTeam, HookMode.Pre);
    }

    private HookResult OnCommandJoinTeam(CCSPlayerController? player, CommandInfo command) {
        if (!isActive || player == null || !player.IsValid || !player.UserId.HasValue) {
            return HookResult.Continue;
        }

        // Only an alive player can be killed by the switch; a dead one swapping sides has no
        // death coming that needs sparing.
        if (player.PawnIsAlive) {
            teamChangeTick[player.UserId.Value] = Server.TickCount;
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        var player = eventInfo.Userid;
        if (player == null || !player.IsValid || !player.UserId.HasValue) {
            return HookResult.Continue;
        }

        // A disconnect also arrives as player_team; a disconnecting player's death (if any)
        // is nobody's team switch, and the stamp would just be noise on a userid about to be
        // recycled.
        if (eventInfo.Disconnect || eventInfo.Team == eventInfo.Oldteam) {
            return HookResult.Continue;
        }

        teamChangeTick[player.UserId.Value] = Server.TickCount;
        return HookResult.Continue;
    }

    private bool WasTeamChange(int userId, int deathTick) {
        if (!teamChangeTick.TryGetValue(userId, out int tick)) {
            return false;
        }

        if (Math.Abs(deathTick - tick) > TeamChangeGraceTicks) {
            teamChangeTick.Remove(userId);
            return false;
        }

        teamChangeTick.Remove(userId);
        return true;
    }

    // Deferred so a player_team fired after the death can still cancel it. Re-resolves the
    // controller by userid when the timer fires rather than holding the reference across
    // ticks; a player gone by then simply gets no penalty, which is the right outcome for a
    // disconnect-death too.
    private void ScheduleSuicidePenalty(int userId, int deathTick) {
        osbase?.AddTimer(SuicidePenaltyDelay, () => {
            if (!isActive || WasTeamChange(userId, deathTick)) {
                return;
            }

            var victim = Utilities.GetPlayerFromUserid(userId);
            if (victim == null || !victim.IsValid) {
                return;
            }

            AdjustScoreboardKills(victim, -1);
            Server.PrintToChatAll($"[Suicide] {victim.PlayerName ?? "Unknown"} took themselves out.");
        });
    }

    private HookResult OnPlayerHurt(EventPlayerHurt eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        if (eventInfo.DmgHealth <= 0) {
            return HookResult.Continue;
        }

        var attacker = eventInfo.Attacker;
        var victim = eventInfo.Userid;

        if (!IsValidFriendlyFire(attacker, victim)) {
            return HookResult.Continue;
        }

        int attackerUserId = attacker!.UserId!.Value;
        string attackerName = attacker.PlayerName ?? "Unknown";
        string victimName = victim!.PlayerName ?? "Unknown";

        osbase?.SendCommand($"css_slap \"#{attackerUserId}\" {eventInfo.DmgHealth}");
        Server.PrintToChatAll($"[TeamDamage] {attackerName} hurt {victimName}");

        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        var attacker = eventInfo.Attacker;
        var victim = eventInfo.Userid;

        // Old CS:Source scoreboard behavior: a teamkill or a suicide takes the offender's
        // own kill count down by one, cosmetic only. Checked first and returns -- a suicide
        // is never also "friendly fire" (IsValidFriendlyFire already excludes attacker==
        // victim), so there's no double-handling risk here.
        if (IsSuicide(attacker, victim, eventInfo.Weapon)) {
            ScheduleSuicidePenalty(victim!.UserId!.Value, Server.TickCount);
            return HookResult.Continue;
        }

        if (!IsValidFriendlyFire(attacker, victim)) {
            return HookResult.Continue;
        }

        int slapDamage = eventInfo.DmgHealth > 0 ? eventInfo.DmgHealth : 1;
        int attackerUserId = attacker!.UserId!.Value;
        string attackerName = attacker.PlayerName ?? "Unknown";
        string victimName = victim!.PlayerName ?? "Unknown";

        osbase?.SendCommand($"css_slap \"#{attackerUserId}\" {slapDamage}");
        Server.PrintToChatAll($"[TeamKill] {attackerName} killed {victimName}");
        attacker.PrintToCenterAlert($"!![TeamKill] You killed {victimName}!!");
        AdjustScoreboardKills(attacker, -1);

        return HookResult.Continue;
    }

    // World damage (fall, drowning, a lingering own molotov, etc.) reports no attacker at
    // all; a direct self-kill (own grenade, the "kill" console command) reports the victim
    // as their own attacker. Both read as the same thing here -- except the bomb, which also
    // reports no attacker but isn't the victim's own fault, so it's excluded explicitly.
    private static bool IsSuicide(CCSPlayerController? attacker, CCSPlayerController? victim, string? weapon) {
        if (victim == null || !victim.IsValid || !victim.UserId.HasValue) {
            return false;
        }

        if (attacker == null) {
            return !string.Equals(weapon, "planted_c4", StringComparison.OrdinalIgnoreCase);
        }

        return attacker.IsValid && attacker.UserId.HasValue && attacker.UserId.Value == victim.UserId.Value;
    }

    // Scoreboard-only, deliberately -- never touches any OSBase-tracked counter or table.
    // Those get their own teamkills/suicides fields instead (DamageReport.cs's
    // player_duel_total), kept separate on purpose so this stays a pure cosmetic match to
    // old CS:Source, not a second writer on a counter something else owns.
    // Deliberately unclamped, confirmed 2026-08-04: a player with 0 kills who teamkills goes
    // to -1, visibly, rather than being floored at 0 -- floor would hide the penalty from
    // exactly the player who most deserves to see it. NOT yet visually verified that CS2's
    // scoreboard renders a negative m_iKills sanely (the underlying int has no problem with
    // it, but the HUD widget is untested) -- if a real negative value looks broken on screen,
    // switch this to floor at 0 instead; that's a hard constraint at that point, not a
    // preference.
    private static void AdjustScoreboardKills(CCSPlayerController player, int delta) {
        var tracking = player.ActionTrackingServices;
        if (tracking == null) {
            return;
        }

        tracking.MatchStats.Kills += delta;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pActionTrackingServices");
    }

    private bool IsValidFriendlyFire(CCSPlayerController? attacker, CCSPlayerController? victim) {
        if (attacker == null || victim == null) {
            return false;
        }

        if (!attacker.IsValid || !victim.IsValid) {
            return false;
        }

        if (!attacker.UserId.HasValue || !victim.UserId.HasValue) {
            return false;
        }

        if (attacker.UserId.Value == victim.UserId.Value) {
            return false;
        }

        if (attacker.TeamNum != victim.TeamNum) {
            return false;
        }

        if (attacker.TeamNum != TEAM_T && attacker.TeamNum != TEAM_CT) {
            return false;
        }

        return true;
    }
}