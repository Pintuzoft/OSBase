using System;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;

namespace OSBase.Modules;

public class Demos : ModuleBase {
    public override string ModuleName => "demos";

    private bool recordingStartedForMap = false;
    private bool mapEndHandled = false;
    private string currentMap = string.Empty;

    protected override void OnLoad() {
        currentMap = osbase?.currentMap ?? Server.MapName ?? string.Empty;
    }

    protected override void OnUnload() {
        recordingStartedForMap = false;
        mapEndHandled = false;
        currentMap = string.Empty;
    }

    protected override void RegisterHandlers() {
        osbase?.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        osbase?.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

        // Use new EventBus system
        osbase?.SubscribeToEvent<EventWarmupEnd>(OnWarmupEnd);
        osbase?.SubscribeToEvent<EventBeginNewMatch>(OnMatchStart);
        osbase?.SubscribeToEvent<EventCsWinPanelMatch>(OnMatchEndEvent);
        osbase?.SubscribeToEvent<EventMapTransition>(OnMapTransition);
        osbase?.SubscribeToEvent<EventMapShutdown>(OnMapShutdown);

        osbase?.AddCommandListener("map", OnCommandMap, HookMode.Pre);
        osbase?.AddCommandListener("changelevel", OnCommandMap, HookMode.Pre);
        osbase?.AddCommandListener("ds_workshop_changelevel", OnCommandMap, HookMode.Pre);
    }

    protected override void UnregisterHandlers() {
        osbase?.RemoveListener<Listeners.OnMapStart>(OnMapStart);
        osbase?.RemoveListener<Listeners.OnMapEnd>(OnMapEnd);

        // Use new EventBus system
        osbase?.UnsubscribeFromEvent<EventWarmupEnd>(OnWarmupEnd);
        osbase?.UnsubscribeFromEvent<EventBeginNewMatch>(OnMatchStart);
        osbase?.UnsubscribeFromEvent<EventCsWinPanelMatch>(OnMatchEndEvent);
        osbase?.UnsubscribeFromEvent<EventMapTransition>(OnMapTransition);
        osbase?.UnsubscribeFromEvent<EventMapShutdown>(OnMapShutdown);

        osbase?.RemoveCommandListener("map", OnCommandMap, HookMode.Pre);
        osbase?.RemoveCommandListener("changelevel", OnCommandMap, HookMode.Pre);
        osbase?.RemoveCommandListener("ds_workshop_changelevel", OnCommandMap, HookMode.Pre);
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

    private HookResult OnMapTransition(EventMapTransition eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Map has transitioned.");
        RunMapEnd("map_transition");
        return HookResult.Continue;
    }

    private HookResult OnMapShutdown(EventMapShutdown eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Map has shutdown.");
        RunMapEnd("map_shutdown");
        return HookResult.Continue;
    }

    private HookResult OnWarmupEnd(EventWarmupEnd eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Warmup has ended.");
        RunWarmupEnd("warmup_end");
        return HookResult.Continue;
    }

    private HookResult OnMatchStart(EventBeginNewMatch eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Match start.");
        RunWarmupEnd("match_start");
        return HookResult.Continue;
    }

    private HookResult OnMatchEndEvent(EventCsWinPanelMatch eventInfo) {
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