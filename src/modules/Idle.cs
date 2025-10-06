using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules;

public class Idle : IModule {
    public string ModuleName => "idle";
    private OSBase? osbase;
    private Config? config;

    private bool roundActive = false;
    private CounterStrikeSharp.API.Modules.Timers.Timer? loopTimer;

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Load() called.");

        // register default enable flag
        config.RegisterGlobalConfigValue(ModuleName, "0");

        var enabled = config.GetGlobalConfigValue(ModuleName, "0") == "1";
        if (!enabled) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in OSBase.cfg.");
            return;
        }

        CreateCustomConfigs();
        LoadEventHandlers();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] successfully loaded!");
    }

    private void CreateCustomConfigs() {
        if (config == null) return;
        config.CreateCustomConfig("idle.cfg",
            "// debug idle skeleton\n" +
            "interval=10\n" +
            "debug=1\n"
        );
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Custom config ensured.");
    }

    private void LoadEventHandlers() {
        if (osbase == null) return;

        osbase.RegisterEventHandler<EventMapTransition>(OnMapTransition);
        osbase.RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        osbase.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        osbase.RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] event handlers registered.");
    }

    private HookResult OnMapTransition(EventMapTransition _, GameEventInfo __) {
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] OnMapTransition()");
        StopLoop();
        roundActive = false;
        return HookResult.Continue;
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd _, GameEventInfo __) {
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] OnRoundFreezeEnd()");
        roundActive = true;
        StartLoop(1.0f);
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd _, GameEventInfo __) {
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] OnRoundEnd()");
        roundActive = false;
        StopLoop();
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull e, GameEventInfo _) {
        var p = e.Userid;
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] OnPlayerConnectFull() → {p?.PlayerName ?? "null"}");
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn e, GameEventInfo _) {
        var p = e.Userid;
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] OnPlayerSpawn() → {p?.PlayerName ?? "null"}");
        return HookResult.Continue;
    }

    private void StartLoop(float delay = 0f) {
        StopLoop();
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] StartLoop(delay={delay})");

        loopTimer = osbase?.AddTimer(delay <= 0f ? 10f : delay, Tick,
            CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void StopLoop() {
        if (loopTimer != null) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] StopLoop() – killing active timer.");
            loopTimer.Kill();
            loopTimer = null;
        }
    }

    private void Tick() {
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Tick() fired – roundActive={roundActive}");
        if (!roundActive) return;

        try {
            foreach (var p in Utilities.GetPlayers()) {
                if (p == null || !p.IsValid) continue;
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Player → {p.PlayerName ?? "Unknown"}");
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Tick() Exception: {ex}");
        } finally {
            loopTimer = osbase?.AddTimer(10f, Tick,
                CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
        }
    }
}