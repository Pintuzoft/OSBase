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

public class MugModule : IModule {
    public string ModuleName => "MugModule";

    private OSBase? osbase;
    private ConfigModule? config;

    public void Load(OSBase inOsbase, ConfigModule inConfig) {
        osbase = inOsbase;
        config = inConfig;

        // Register required global config values
        config.RegisterGlobalConfigValue("mug_enabled", "1");

        // Register the player hurt event handler
        osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);

        Console.WriteLine("[INFO] MugModule loaded successfully!");
    }

    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
        if (config == null || osbase == null) 
            return HookResult.Continue;

        // Check if the mugging functionality is enabled
        if (config.GetGlobalConfigValue("mug_enabled", "0") != "1") 
            return HookResult.Continue;

        var attacker = eventInfo.Attacker;
        var victim = eventInfo.Userid;

        // Ensure both attacker and victim are valid players
        if (attacker == null || victim == null || !attacker.IsValid || !victim.IsValid) 
            return HookResult.Continue;

        // Check if the attacker used a knife
        if (eventInfo.Weapon != "knife") 
            return HookResult.Continue;

        int victimMoney = victim.InGameMoneyServices?.Account ?? 0;

        if (attacker.Team == victim.Team) {
            // Reverse punishment for team knifing
            if (victimMoney > 0) {
                attacker.RemoveMoney(victimMoney); // Deduct all money from the attacker
                victim.AddMoney(victimMoney);     // Give it back to the victim

                Console.WriteLine($"[INFO] MugModule: {attacker.PlayerName} was punished for knifing their teammate {victim.PlayerName} and lost ${victimMoney}.");
                osbase.SendCommand($"say \"{attacker.PlayerName} tried to mug their teammate {victim.PlayerName} and lost ${victimMoney} as punishment!\"");
            } else {
                Console.WriteLine("[DEBUG] MugModule: Attacker has no money to punish.");
            }
        } else {
            // Normal mugging behavior
            if (victimMoney > 0) {
                victim.RemoveMoney(victimMoney); // Deduct all money from the victim
                attacker.AddMoney(victimMoney);  // Give it to the attacker

                Console.WriteLine($"[INFO] MugModule: {attacker.PlayerName} stole ${victimMoney} from {victim.PlayerName} with a knife.");
                osbase.SendCommand($"say \"{attacker.PlayerName} mugged {victim.PlayerName} for ${victimMoney}!\"");
            } else {
                Console.WriteLine("[DEBUG] MugModule: Victim has no money to steal.");
            }
        }

        return HookResult.Continue;
    }
}