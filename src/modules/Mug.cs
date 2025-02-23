using System;
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

public class Mug : IModule {
    public string ModuleName => "mug";
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
        osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
    }

    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
        if ( config == null || osbase == null ) 
            return HookResult.Continue;

        var attacker = eventInfo.Attacker;
        var victim = eventInfo.Userid;

        // Ensure both attacker and victim are valid players
        if ( attacker == null || victim == null || !attacker.IsValid || !victim.IsValid ) 
            return HookResult.Continue;

        // Check if the attacker used a knife
        if ( ! eventInfo.Weapon.Contains("knife")) 
            return HookResult.Continue;

        int victimMoney = victim.InGameMoneyServices?.Account ?? 0;

        if ( attacker.Team == victim.Team ) {
            // Reverse punishment for team knifing
            if (victimMoney > 0) {
                attacker.RemoveMoney(victimMoney); // Deduct all money from the attacker
                victim.AddMoney(victimMoney);     // Give it back to the victim
                Server.PrintToChatAll($"{attacker.PlayerName} tried to mug their teammate {victim.PlayerName} and lost ${victimMoney} as punishment!");
            } else {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Attacker has no money to punish.");
            }
        } else {
            // Normal mugging behavior
            if (victimMoney > 0) {
                victim.RemoveMoney(victimMoney); // Deduct all money from the victim
                attacker.AddMoney(victimMoney);  // Give it to the attacker

                //Console.WriteLine($"[INFO] OSBase[{ModuleName}]  {attacker.PlayerName} stole ${victimMoney} from {victim.PlayerName} with a knife.");
                Server.PrintToChatAll($"{attacker.PlayerName} mugged {victim.PlayerName} for ${victimMoney}!");
            } else {
                Server.PrintToChatAll($"{attacker.PlayerName} mugged {victim.PlayerName} but they had no money!");
            }
        }

        return HookResult.Continue;
    }
}