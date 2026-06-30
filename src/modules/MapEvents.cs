using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;

namespace OSBase.Modules;

public class MapEvents : ModuleBase {
    public override string ModuleName => "mapevents";

    private bool isWarmup = true;
    private bool mapEndExecuted = false;

    protected override void OnLoad() {
        CreateCustomConfigs();
    }

    protected override void OnUnload() {
        isWarmup = true;
        mapEndExecuted = false;
    }

    protected override void OnReloadConfig() {
        CreateCustomConfigs();
    }

    protected override void RegisterHandlers() {
        osbase?.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        osbase?.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        // Use new EventBus system
        osbase?.SubscribeToEvent<EventWarmupEnd>(OnWarmupEnd);
        osbase?.SubscribeToEvent<EventCsWinPanelMatch>(OnMatchEndEvent);
    }

    protected override void UnregisterHandlers() {
        osbase?.RemoveListener<Listeners.OnMapStart>(OnMapStart);
        osbase?.RemoveListener<Listeners.OnMapEnd>(OnMapEnd);
        // Use new EventBus system
        osbase?.UnsubscribeFromEvent<EventWarmupEnd>(OnWarmupEnd);
        osbase?.UnsubscribeFromEvent<EventCsWinPanelMatch>(OnMatchEndEvent);
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

    private HookResult OnWarmupEnd(EventWarmupEnd eventInfo) {
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

    private HookResult OnMatchEndEvent(EventCsWinPanelMatch eventInfo) {
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