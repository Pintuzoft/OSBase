using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules;

public class TeamDamage : IModule {
    public string ModuleName => "teamdamage";

    private OSBase? osbase;
    private Config? config;

    private bool handlersLoaded = false;
    private bool isActive = false;

    private const int TEAM_T = (int)CsTeam.Terrorist;
    private const int TEAM_CT = (int)CsTeam.CounterTerrorist;

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;
        isActive = true;

        if (osbase == null || config == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] load failed (null deps).");
            isActive = false;
            return;
        }

        config.RegisterGlobalConfigValue(ModuleName, "1");

        if (config.GetGlobalConfigValue(ModuleName, "0") != "1") {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in config.");
            isActive = false;
            return;
        }

        LoadHandlers();
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    public void Unload() {
        isActive = false;

        if (osbase != null && handlersLoaded) {
            osbase.DeregisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
            osbase.DeregisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            handlersLoaded = false;
        }

        config = null;
        osbase = null;

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] unloaded.");
    }

    public void ReloadConfig(Config inConfig) {
        config = inConfig;
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] config reloaded.");
    }

    private void LoadHandlers() {
        if (osbase == null || handlersLoaded) {
            return;
        }

        osbase.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);

        handlersLoaded = true;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt eventInfo, GameEventInfo gameEventInfo) {
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

    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
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