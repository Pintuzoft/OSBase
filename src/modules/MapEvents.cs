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

public class MapEvents : IModule {
    public string ModuleName => "mapevents";   
     private OSBase? osbase;
    private Config? config;
    private bool isWarmup = true;

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        config.RegisterGlobalConfigValue($"{ModuleName}", "1");

        if (osbase == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] osbase is null. {ModuleName} failed to load.");
            return;
        } else if (config == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] config is null. {ModuleName} failed to load.");
            return;
        }

        if (config?.GetGlobalConfigValue($"{ModuleName}", "0") == "1") {
            createCustomConfigs();
            loadEventHandlers();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
        } else {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {ModuleName} is disabled in the global configuration.");
        }
    }

    private void createCustomConfigs() {
        if (config == null) return;
        config.CreateCustomConfig("mapstart.cfg", "// Commands for map start\n");
        config.CreateCustomConfig("mapend.cfg", "// Commands for map end\n");
        config.CreateCustomConfig("warmupstart.cfg", "// Commands for warmup start\nsv_gravity 200\n");
        config.CreateCustomConfig("warmupend.cfg", "// Commands for warmup end\nsv_gravity 800\n");
    }
    private void loadEventHandlers() {
        if(osbase == null) return; 
        osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        osbase.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        osbase.RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
        osbase.RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEndEvent);
    }

    private void OnMapStart(string mapName) {
        isWarmup = true;
        //if (osbase != null) {
        //    osbase.currentMap = mapName;
        //}
        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Map {mapName} started. Warmup begins.");
        config?.ExecuteCustomConfig("mapstart.cfg");
        config?.ExecuteCustomConfig("warmupstart.cfg");
    }

    private void OnMapEnd() {
        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Running end of map commands...");
        config?.ExecuteCustomConfig("mapend.cfg");
    }

    private HookResult OnWarmupEnd(EventWarmupEnd eventInfo, GameEventInfo gameEventInfo) {
        if (!isWarmup) return HookResult.Continue;
        isWarmup = false;
        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Warmup ended.");
        config?.ExecuteCustomConfig("warmupend.cfg");
        return HookResult.Continue;
    }

    private HookResult OnMatchEndEvent(EventCsWinPanelMatch eventInfo, GameEventInfo gameEventInfo) {
        // Add any additional logic for match end event here
        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Running end of map commands...");
        config?.ExecuteCustomConfig("mapend.cfg");
        return HookResult.Continue;
    }
}