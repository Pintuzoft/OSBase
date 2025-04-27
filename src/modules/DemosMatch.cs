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
using System.Xml;
using CounterStrikeSharp.API.Core.Commands;

public class DemosMatch : IModule {
    public string ModuleName => "demosmatch";   
    private OSBase? osbase;
    private Config? config;
    private float demoQuitDelay = 5.0f; // Default delay in seconds

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
        osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        osbase.RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);
    }

    /*
        EVENT HANDLERS
    */
 
    private void OnMapStart(string mapName) {
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Map has started.");
        if (osbase != null) {
            osbase.currentMap = mapName;
        }
    }

    private HookResult OnMatchEnd(EventCsWinPanelMatch eventInfo, GameEventInfo gameEventInfo) {
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Match has ended.");
        osbase?.SendCommand("tv_stoprecord");
        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Autorecord is enabled. Stopped recording demo.");
        osbase?.AddTimer(demoQuitDelay, () => {
            Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Quitting server now.");
            osbase?.SendCommand("quit");
        });
        return HookResult.Continue;
    }

}