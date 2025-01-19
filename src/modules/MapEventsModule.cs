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

public class MapEventsModule : IModule {
    public string ModuleName => "MapEventsModule";   
     private OSBase? osbase;
    private ConfigModule? config;
    private bool isWarmup = true;

    public void Load(OSBase inOsbase, ConfigModule inConfig) {
        osbase = inOsbase;
        config = inConfig;

        // Create custom config files
        config.CreateCustomConfig("mapstart.cfg", "// Commands for map start\n");
        config.CreateCustomConfig("mapend.cfg", "// Commands for map end\n");
        config.CreateCustomConfig("warmupstart.cfg", "// Commands for warmup start\nsv_gravity 200\n");
        config.CreateCustomConfig("warmupend.cfg", "// Commands for warmup end\nsv_gravity 800\n");

        // Register event handlers and listeners
        osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        osbase.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        osbase.RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
        osbase.RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEndEvent);

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    private void OnMapStart(string mapName) {
        isWarmup = true;
        if (osbase != null) {
            osbase.currentMap = mapName;
        }
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