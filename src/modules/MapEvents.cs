using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;

namespace OSBase.Modules;

public class MapEvents : IModule {
    public string ModuleName => "mapevents";

    private OSBase? osbase;
    private Config? config;

    private bool handlersLoaded = false;
    private bool isActive = false;
    private bool isWarmup = true;
    private bool mapEndExecuted = false;

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

        CreateCustomConfigs();
        LoadHandlers();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    public void Unload() {
        isActive = false;

        if (osbase != null && handlersLoaded) {
            osbase.RemoveListener<Listeners.OnMapStart>(OnMapStart);
            osbase.RemoveListener<Listeners.OnMapEnd>(OnMapEnd);
            osbase.DeregisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
            osbase.DeregisterEventHandler<EventCsWinPanelMatch>(OnMatchEndEvent);

            handlersLoaded = false;
        }

        isWarmup = true;
        mapEndExecuted = false;

        config = null;
        osbase = null;

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] unloaded.");
    }

    public void ReloadConfig(Config inConfig) {
        config = inConfig;
        CreateCustomConfigs();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] config reloaded.");
    }

    private void LoadHandlers() {
        if (osbase == null || handlersLoaded) {
            return;
        }

        osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        osbase.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        osbase.RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
        osbase.RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEndEvent);

        handlersLoaded = true;
    }

    private void CreateCustomConfigs() {
        if (config == null) {
            return;
        }

        config.CreateCustomConfig("mapstart.cfg", "// Commands for map start\n");
        config.CreateCustomConfig("mapend.cfg", "// Commands for map end\n");
        config.CreateCustomConfig("warmupstart.cfg", "// Commands for warmup start\nsv_gravity 200\n");
        config.CreateCustomConfig("warmupend.cfg", "// Commands for warmup end\nsv_gravity 800\n");
    }

    private void OnMapStart(string mapName) {
        if (!isActive) {
            return;
        }

        isWarmup = true;
        mapEndExecuted = false;

        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Map {mapName} started. Warmup begins.");
        config?.ExecuteCustomConfig("mapstart.cfg");
        config?.ExecuteCustomConfig("warmupstart.cfg");
    }

    private void OnMapEnd() {
        if (!isActive) {
            return;
        }

        RunMapEndCommands("OnMapEnd");
    }

    private HookResult OnWarmupEnd(EventWarmupEnd eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        if (!isWarmup) {
            return HookResult.Continue;
        }

        isWarmup = false;

        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Warmup ended.");
        config?.ExecuteCustomConfig("warmupend.cfg");

        return HookResult.Continue;
    }

    private HookResult OnMatchEndEvent(EventCsWinPanelMatch eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        RunMapEndCommands("OnMatchEndEvent");
        return HookResult.Continue;
    }

    private void RunMapEndCommands(string source) {
        if (mapEndExecuted) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: mapend.cfg already executed, skipping ({source}).");
            return;
        }

        mapEndExecuted = true;

        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Running end of map commands ({source})...");
        config?.ExecuteCustomConfig("mapend.cfg");
    }
}