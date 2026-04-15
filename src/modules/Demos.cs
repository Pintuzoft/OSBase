using System;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;

namespace OSBase.Modules;

public class Demos : IModule {
    public string ModuleName => "demos";

    private OSBase? osbase;
    private Config? config;

    private bool handlersLoaded = false;
    private bool isActive = false;

    private bool recordingStartedForMap = false;
    private bool mapEndHandled = false;
    private string currentMap = string.Empty;

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

        currentMap = osbase.currentMap ?? Server.MapName ?? string.Empty;
        LoadHandlers();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    public void Unload() {
        isActive = false;
        recordingStartedForMap = false;
        mapEndHandled = false;

        if (osbase != null && handlersLoaded) {
            osbase.RemoveListener<Listeners.OnMapStart>(OnMapStart);
            osbase.RemoveListener<Listeners.OnMapEnd>(OnMapEnd);

            osbase.DeregisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
            osbase.DeregisterEventHandler<EventBeginNewMatch>(OnMatchStart);
            osbase.DeregisterEventHandler<EventCsWinPanelMatch>(OnMatchEndEvent);
            osbase.DeregisterEventHandler<EventMapTransition>(OnMapTransition);
            osbase.DeregisterEventHandler<EventMapShutdown>(OnMapShutdown);

            osbase.RemoveCommandListener("map", OnCommandMap, HookMode.Pre);
            osbase.RemoveCommandListener("changelevel", OnCommandMap, HookMode.Pre);
            osbase.RemoveCommandListener("ds_workshop_changelevel", OnCommandMap, HookMode.Pre);

            handlersLoaded = false;
        }

        config = null;
        osbase = null;
        currentMap = string.Empty;

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] unloaded.");
    }

    public void ReloadConfig(Config inConfig) {
        config = inConfig;
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] config reloaded.");
    }

    private void LoadHandlers() {
        if (osbase == null || handlersLoaded) {
            return;
        }

        osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        osbase.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

        osbase.RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
        osbase.RegisterEventHandler<EventBeginNewMatch>(OnMatchStart);
        osbase.RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEndEvent);
        osbase.RegisterEventHandler<EventMapTransition>(OnMapTransition);
        osbase.RegisterEventHandler<EventMapShutdown>(OnMapShutdown);

        osbase.AddCommandListener("map", OnCommandMap, HookMode.Pre);
        osbase.AddCommandListener("changelevel", OnCommandMap, HookMode.Pre);
        osbase.AddCommandListener("ds_workshop_changelevel", OnCommandMap, HookMode.Pre);

        handlersLoaded = true;
    }

    /*
        EVENT HANDLERS
    */

    private void OnMapStart(string mapName) {
        if (!isActive) {
            return;
        }

        currentMap = mapName;
        recordingStartedForMap = false;
        mapEndHandled = false;

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Map has started: {mapName}");
    }

    public HookResult OnCommandMap(CCSPlayerController? player, CommandInfo command) {
        if (!isActive) {
            return HookResult.Continue;
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Changelevel detected.");
        RunMapEnd("command_map");
        return HookResult.Continue;
    }

    private void OnMapEnd() {
        if (!isActive) {
            return;
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Map has ended.");
        RunMapEnd("map_end");
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

    private HookResult OnWarmupEnd(EventWarmupEnd eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Warmup has ended.");
        RunWarmupEnd("warmup_end");
        return HookResult.Continue;
    }

    private HookResult OnMatchStart(EventBeginNewMatch eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Match start.");
        RunWarmupEnd("match_start");
        return HookResult.Continue;
    }

    private HookResult OnMatchEndEvent(EventCsWinPanelMatch eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Match has ended.");
        RunMapEnd("match_end");
        return HookResult.Continue;
    }

    /*
        METHODS
    */

    private void RunMapEnd(string source) {
        if (mapEndHandled) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] map end already handled, skipping ({source}).");
            return;
        }

        mapEndHandled = true;
        recordingStartedForMap = false;

        osbase?.SendCommand("tv_stoprecord");
        osbase?.SendCommand("tv_enable 0");

        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Stopped recording demo ({source}).");
    }

    private void RunWarmupEnd(string source) {
        if (recordingStartedForMap) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] demo already started for this map, skipping ({source}).");
            return;
        }

        string date = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string mapName = string.IsNullOrWhiteSpace(currentMap) ? (osbase?.currentMap ?? Server.MapName ?? "unknownmap") : currentMap;

        string tName = "Terrorists";
        string ctName = "CounterTerrorists";
        bool isMatch = false;

        Server.ExecuteCommand("tv_enable 1");

        try {
            isMatch = Teams.isMatchActive();

            if (isMatch) {
                TeamInfo tTeam = Teams.getTeams().getT();
                TeamInfo ctTeam = Teams.getTeams().getCT();

                tName = tTeam.getTeamName();
                ctName = ctTeam.getTeamName();

                Server.ExecuteCommand("tv_stoprecord");
            }
        } catch (Exception ex) {
            Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Failed to get teams, recording generic demo -> {ex.Message}");
            isMatch = false;
        }

        string safeMap = SanitizeDemoPart(mapName);
        string safeT = SanitizeDemoPart(tName);
        string safeCt = SanitizeDemoPart(ctName);

        if (isMatch) {
            Server.ExecuteCommand($"tv_record {date}-{safeMap}-{safeCt}_vs_{safeT}.dem");
        } else {
            Server.ExecuteCommand($"tv_record {date}-{safeMap}.dem");
        }

        recordingStartedForMap = true;
        mapEndHandled = false;

        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Demo recording started ({source}).");
    }

    private static string SanitizeDemoPart(string input) {
        if (string.IsNullOrWhiteSpace(input)) {
            return "unknown";
        }

        var sb = new StringBuilder(input.Length);

        foreach (char c in input) {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') {
                sb.Append(c);
            } else if (char.IsWhiteSpace(c)) {
                sb.Append('_');
            } else {
                sb.Append('_');
            }
        }

        string result = sb.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
    }
}