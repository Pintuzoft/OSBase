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

public class TeamDamageModule : IModule {
    public string ModuleName => "TeamDamageModule";    
    private OSBase? osbase;
    private ConfigModule? config;

    public void Load(OSBase inOsbase, ConfigModule inConfig) {
        osbase = inOsbase;
        config = inConfig;

        // Register required global config values
        config.RegisterGlobalConfigValue("teamdamage_slaps", "1");

        // Register event handlers and listeners
        osbase.RegisterEventHandler<EventPlayerHurt>(onPlayerHurt);
        osbase.RegisterEventHandler<EventPlayerDeath>(onPlayerDeath);

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    /* PLAYER HURT */
    private HookResult onPlayerHurt(EventPlayerHurt eventInfo, GameEventInfo gameEventInfo) {
        if (config?.GetGlobalConfigValue("teamdamage_slaps", "0") != "1") {
            return HookResult.Continue;
        } else if (eventInfo.DmgHealth == 0) {
            return HookResult.Continue;
        }

        var attacker = eventInfo.Attacker;
        var victim = eventInfo.Userid;

        if (attacker != null && 
            victim != null &&
            attacker.Team == victim.Team) {
                if ( attacker == victim ) {
                    return HookResult.Continue;
                }
                osbase?.SendCommand($"css_slap #{attacker.UserId} {eventInfo.DmgHealth}");
                osbase?.SendCommand($"say [TeamDamage] {attacker.PlayerName} hurt {victim.PlayerName}");
        }
        
        return HookResult.Continue;
    }

    /* PLAYER DEATH */  
    private HookResult onPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
        if (config?.GetGlobalConfigValue("teamdamage_slaps", "0") != "1") {
            return HookResult.Continue;
        } else if (eventInfo.DmgHealth == 0) {
            return HookResult.Continue;
        }

        var attacker = eventInfo.Attacker;
        var victim = eventInfo.Userid;

        if (attacker != null && 
            victim != null &&
            attacker.Team == victim.Team) {
                if ( attacker == victim ) {
                    return HookResult.Continue;
                }
                osbase?.SendCommand($"css_slap #{attacker.UserId} {eventInfo.DmgHealth}");
                osbase?.SendCommand($"say [TeamKill] {attacker.PlayerName} killed {victim.PlayerName}");
        }
        
        return HookResult.Continue;
    }

}