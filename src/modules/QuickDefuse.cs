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
using CounterStrikeSharp.API.Modules.Menu;

namespace OSBase.Modules;

using System.IO;
using CounterStrikeSharp.API.Modules.Entities;

public class QuickDefuse : IModule {
    public string ModuleName => "quickdefuse";
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
        osbase.RegisterEventHandler<EventBombBegindefuse>(OnBombBeginDefuse);
    }

    private HookResult OnBombBeginDefuse(EventBombBegindefuse eventInfo, GameEventInfo gameEventInfo) {
        // Add your event handling logic here
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Bomb defuse started.");

        CCSPlayerController? player = eventInfo.Userid;
        if (player == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] player is null.");
            return HookResult.Continue;
        }
 
//        OSMenuAPI osmenu = new OSMenuAPI();
//        osmenu.GetMenu("QuickDefuse", ).Title = "Quick Defuse";



        return HookResult.Continue;
    }

}