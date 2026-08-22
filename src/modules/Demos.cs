using System;
using System.Linq;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;

namespace OSBase.Modules;

public class Demos : ModuleBase {
    public override string ModuleName => "demos";

    // ban-highlights-contract.md §9b, 2026-08-21: SourceTV can get kicked mid-match by things
    // outside this module (e.g. bot_quota rebalancing). Root-cause-agnostic fix: don't try to
    // enumerate everything that could kill the recorder, just notice it's gone and start it
    // again. RestartDelaySeconds gives whatever kicked it a moment to finish before tv_record
    // is reissued into the same churn.
    private const float RestartDelaySeconds = 1.0f;

    private bool recordingStartedForMap = false;
    private bool mapEndHandled = false;
    private bool restartPending = false;
    private string currentMap = string.Empty;

    protected override void OnLoad() {
        currentMap = osbase?.currentMap ?? Server.MapName ?? string.Empty;
    }

    protected override void OnUnload() {
        recordingStartedForMap = false;
        mapEndHandled = false;
        restartPending = false;
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
        osbase?.SubscribeToEvent<EventPlayerDisconnect>(OnPlayerDisconnect);

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
        osbase?.UnsubscribeFromEvent<EventPlayerDisconnect>(OnPlayerDisconnect);

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

    // ban-highlights-contract.md §9b: the recorder disappearing on its own (kicked mid-match
    // by something outside this module) looks identical to a deliberate stop unless we track
    // our OWN intent. mapEndHandled is true only in the tick after WE called tv_stoprecord
    // (RunMapEnd) -- any other HLTV disconnect is unrequested, regardless of what the engine's
    // own reason code says, so there's no need to decode NETWORK_DISCONNECT_* here at all.
    private HookResult OnPlayerDisconnect(EventPlayerDisconnect eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        var player = eventInfo.Userid;
        if (player == null || !player.IsHLTV) {
            return HookResult.Continue;
        }

        if (mapEndHandled || !recordingStartedForMap) {
            // Expected (we asked for the stop) or nothing was recording for this map yet
            // (e.g. still warmup) -- nothing to restart either way.
            return HookResult.Continue;
        }

        Console.WriteLine($"[WARN] OSBase[{ModuleName}]: SourceTV disconnected unexpectedly mid-recording (reason={eventInfo.Reason}) -- restarting.");
        ScheduleRestart("recorder_lost");
        return HookResult.Continue;
    }

    private void ScheduleRestart(string source) {
        if (restartPending) {
            return;
        }

        restartPending = true;
        recordingStartedForMap = false;

        osbase?.AddTimer(RestartDelaySeconds, () => {
            restartPending = false;

            if (!isActive || mapEndHandled) {
                // Map ended (or the plugin unloaded) in the delay window -- a fresh recording
                // would immediately be wrong for a map that's no longer live.
                return;
            }

            RunWarmupEnd(source);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    // Same fix, second half: OnWarmupEnd and OnMatchStart both call RunWarmupEnd (belt and
    // suspenders for whichever event a given game mode actually fires), so the guard below has
    // always had to handle being called twice for one map. It used to trust recordingStartedForMap
    // as a memory of "we already did this" -- if the recorder was lost and OnPlayerDisconnect's
    // restart (above) hasn't landed yet when the second call comes in, that memory is stale.
    // Checking presence directly turns the second call into a second chance to notice the
    // recorder is actually gone, instead of skipping on faith.
    private static bool IsRecorderConnected() {
        return Utilities.GetPlayers().Any(p => p != null && p.IsValid && p.IsHLTV);
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
            // ban-highlights-contract.md §9b: this used to be a memory ("we already started
            // it") instead of a check. Confirming the recorder is actually there catches the
            // recorder having been killed silently during warmup, before it ever produced a
            // disconnect event for OnPlayerDisconnect above to react to.
            if (IsRecorderConnected()) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] demo already started for this map, skipping ({source}).");
                return;
            }

            Console.WriteLine($"[WARN] OSBase[{ModuleName}]: recording was marked started but SourceTV isn't connected -- restarting ({source}).");
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