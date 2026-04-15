using System;
using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace OSBase.Modules;

public class DemosMatch : IModule {
    public string ModuleName => "demosmatch";

    private OSBase? osbase;
    private Config? config;

    private bool handlersLoaded = false;
    private bool isActive = false;
    private bool mapEndHandled = false;

    private float demoQuitDelay = 10.0f;
    private Timer? quitTimer;

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
        config.RegisterGlobalConfigValue($"{ModuleName}_quit_delay", "10");

        if (config.GetGlobalConfigValue(ModuleName, "0") != "1") {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in config.");
            isActive = false;
            return;
        }

        LoadConfig();
        LoadHandlers();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    public void Unload() {
        isActive = false;

        quitTimer?.Kill();
        quitTimer = null;
        mapEndHandled = false;

        if (osbase != null && handlersLoaded) {
            osbase.RemoveListener<Listeners.OnMapStart>(OnMapStart);
            osbase.DeregisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);
            osbase.DeregisterEventHandler<EventMapTransition>(OnMapTransition);
            osbase.DeregisterEventHandler<EventMapShutdown>(OnMapShutdown);

            osbase.RemoveCommandListener("map", OnCommandMap, HookMode.Pre);
            osbase.RemoveCommandListener("changelevel", OnCommandMap, HookMode.Pre);
            osbase.RemoveCommandListener("ds_workshop_changelevel", OnCommandMap, HookMode.Pre);

            handlersLoaded = false;
        }

        config = null;
        osbase = null;

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] unloaded.");
    }

    public void ReloadConfig(Config inConfig) {
        config = inConfig;
        LoadConfig();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] config reloaded. demoQuitDelay={demoQuitDelay:0.0}");
    }

    private void LoadConfig() {
        demoQuitDelay = 10.0f;

        if (config == null) {
            return;
        }

        string raw = config.GetGlobalConfigValue($"{ModuleName}_quit_delay", "10");

        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out demoQuitDelay) &&
            !float.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out demoQuitDelay)) {
            demoQuitDelay = 10.0f;
        }

        demoQuitDelay = MathF.Max(0.0f, demoQuitDelay);
    }

    private void LoadHandlers() {
        if (osbase == null || handlersLoaded) {
            return;
        }

        osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        osbase.RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);
        osbase.RegisterEventHandler<EventMapTransition>(OnMapTransition);
        osbase.RegisterEventHandler<EventMapShutdown>(OnMapShutdown);

        osbase.AddCommandListener("map", OnCommandMap, HookMode.Pre);
        osbase.AddCommandListener("changelevel", OnCommandMap, HookMode.Pre);
        osbase.AddCommandListener("ds_workshop_changelevel", OnCommandMap, HookMode.Pre);

        handlersLoaded = true;
    }

    private void OnMapStart(string mapName) {
        if (!isActive) {
            return;
        }

        quitTimer?.Kill();
        quitTimer = null;
        mapEndHandled = false;

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Map has started: {mapName}");
    }

    private HookResult OnMatchEnd(EventCsWinPanelMatch eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Match has ended.");

        RunMapEnd("match_end");
        ScheduleQuit();

        return HookResult.Continue;
    }

    public HookResult OnCommandMap(CCSPlayerController? player, CommandInfo command) {
        if (!isActive) {
            return HookResult.Continue;
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Changelevel detected: {command.GetCommandString}");
        RunMapEnd("command_map");

        return HookResult.Continue;
    }

    private HookResult OnMapTransition(EventMapTransition eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Map has transitioned.");
        RunMapEnd("map_transition");

        return HookResult.Continue;
    }

    private HookResult OnMapShutdown(EventMapShutdown eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Map has shutdown.");
        RunMapEnd("map_shutdown");

        return HookResult.Continue;
    }

    private void RunMapEnd(string source) {
        if (mapEndHandled) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] map end already handled, skipping ({source}).");
            return;
        }

        mapEndHandled = true;

        osbase?.SendCommand("tv_stoprecord");
        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Stopped recording demo ({source}).");
    }

    private void ScheduleQuit() {
        if (osbase == null) {
            return;
        }

        quitTimer?.Kill();
        quitTimer = null;

        quitTimer = osbase.AddTimer(demoQuitDelay, () => {
            if (!isActive) {
                return;
            }

            Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Quitting server now.");
            osbase?.SendCommand("quit");
        });
    }
}