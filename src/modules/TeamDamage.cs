using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules;

public class TeamDamage : ModuleBase {
    public override string ModuleName => "teamdamage";

    private const int TEAM_T = (int)CsTeam.Terrorist;
    private const int TEAM_CT = (int)CsTeam.CounterTerrorist;

    protected override void RegisterHandlers() {
        osbase?.SubscribeToEvent<EventPlayerHurt>(OnPlayerHurt);
        osbase?.SubscribeToEvent<EventPlayerDeath>(OnPlayerDeath);
    }

    protected override void UnregisterHandlers() {
        osbase?.UnsubscribeFromEvent<EventPlayerHurt>(OnPlayerHurt);
        osbase?.UnsubscribeFromEvent<EventPlayerDeath>(OnPlayerDeath);
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

        return HookResult.Continue;
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