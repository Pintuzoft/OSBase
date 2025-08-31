using System.IO;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Events;
using System.Diagnostics.Tracing;
using System.Reflection;

namespace OSBase.Modules;

using System.IO;

public class TeamDamage : IModule {
    public string ModuleName => "teamdamage";    
    private OSBase? osbase;
    private Config? config;

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        // Register required global config values
        config.RegisterGlobalConfigValue($"{ModuleName}", "1");

        if (osbase == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] osbase is null. {ModuleName} failed to load.");
            return;
        } else if (config == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] config is null. {ModuleName} failed to load.");
            return;
        }

        if (config?.GetGlobalConfigValue($"{ModuleName}", "0") == "1") {
            loadEventHandlers();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
        } else {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {ModuleName} is disabled in the global configuration.");
        }
    }

    private void loadEventHandlers() {
        if(osbase == null) return;
        osbase.RegisterEventHandler<EventPlayerHurt>(onPlayerHurt);
        osbase.RegisterEventHandler<EventPlayerDeath>(onPlayerDeath);
    }

    /* PLAYER HURT */
    private HookResult onPlayerHurt(EventPlayerHurt eventInfo, GameEventInfo gameEventInfo) {
        if (eventInfo.DmgHealth == 0) 
            return HookResult.Continue;
        

        var attacker = eventInfo.Attacker;
        var victim = eventInfo.Userid;

        if (attacker != null && 
            victim != null &&
            attacker.Team == victim.Team) {
                if ( attacker == victim ) {
                    return HookResult.Continue;
                }
                osbase?.SendCommand($"css_slap \"#{attacker.UserId}\" {eventInfo.DmgHealth}");
                Server.PrintToChatAll($"[TeamDamage] {attacker.PlayerName} hurt {victim.PlayerName}");
        }
        
        return HookResult.Continue;
    }

    /* PLAYER DEATH */  
    private HookResult onPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
        if (eventInfo.DmgHealth == 0) 
            return HookResult.Continue;
        

        var attacker = eventInfo.Attacker;
        var victim = eventInfo.Userid;

        if (attacker != null && 
            victim != null &&
            attacker.Team == victim.Team) {
                if ( attacker == victim ) {
                    return HookResult.Continue;
                }
                osbase?.SendCommand($"css_slap \"#{attacker.UserId}\" {eventInfo.DmgHealth}");
                Server.PrintToChatAll($"[TeamKill] {attacker.PlayerName} killed {victim.PlayerName}");
                attacker.PrintToCenterAlert($"!![TeamKill] You killed {victim.PlayerName}!!");
        }
        
        return HookResult.Continue;
    }

}