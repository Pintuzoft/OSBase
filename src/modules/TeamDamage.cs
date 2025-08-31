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
    }

    /* PLAYER HURT */
    private HookResult onPlayerHurt(EventPlayerHurt eventInfo, GameEventInfo gameEventInfo) {
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] 0:");
        if (eventInfo.DmgHealth == 0) 
            return HookResult.Continue;
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] 1:");
        

        var attacker = eventInfo.Attacker;
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] 2:");
        var victim = eventInfo.Userid;
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] 3:");

        if (attacker != null && 
            victim != null &&
            attacker.Team == victim.Team) {
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] 4:");
                if ( attacker == victim ) {
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] 5:");
                    return HookResult.Continue;
                }
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] 6:");

                // ONLY CHANGE: cap slap damage so attacker can't go below 0 HP
                int attackerHp = System.Math.Max(0, attacker.Health);
                int slapDmg = System.Math.Min(eventInfo.DmgHealth, attackerHp);
                if (slapDmg <= 0)
                    return HookResult.Continue;

                osbase?.SendCommand($"css_slap \"#{attacker.UserId}\" {slapDmg}");

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] 7:");
                Server.PrintToChatAll($"[TeamDamage] {attacker.PlayerName} hurt {victim.PlayerName}");
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] 8:");
        }
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] 9:");
        
        return HookResult.Continue;
    }

}