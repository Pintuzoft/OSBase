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

    // state
    private readonly Dictionary<uint, PlayerData> tracked = new();
    private float checkInterval = 10f;
    private float moveThreshold = 5f;
    private int warnAfter = 3;
    private int moveAfter = 6;

    private class PlayerData { public Vector? Origin; public int StillCount; }

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        // Default OFF (mirrors your Welcome style)
        config.RegisterGlobalConfigValue($"{ModuleName}", "0");

        if (osbase == null) { Console.WriteLine($"[ERROR] OSBase[{ModuleName}] osbase is null."); return; }
        if (config == null) { Console.WriteLine($"[ERROR] OSBase[{ModuleName}] config is null."); return; }

        // Respect toggle exactly like Welcome does
        if (config.GetGlobalConfigValue($"{ModuleName}", "0") == "1") {
            createCustomConfigs();
            loadEventHandlers();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded (interval={checkInterval}s).");
        } else {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in OSBase.cfg.");
        }
    }

    private void createCustomConfigs() {
        if (config == null) return;
        config.CreateCustomConfig("idle.cfg",
            "// Idle module configuration\n" +
            "check_interval=10\n" +
            "move_threshold=5\n" +
            "warn_after=3\n" +
            "move_after=6\n"
        );
        foreach (var line in config.FetchCustomConfig("idle.cfg") ?? new List<string>()) {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("//")) continue;
            var kv = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;
            switch (kv[0].ToLowerInvariant()) {
                case "check_interval": float.TryParse(kv[1], out checkInterval); break;
                case "move_threshold": float.TryParse(kv[1], out moveThreshold); break;
                case "warn_after":     int.TryParse(kv[1], out warnAfter); break;
                case "move_after":     int.TryParse(kv[1], out moveAfter); break;
            }
        }
    }

    private void loadEventHandlers() {
        if (osbase == null) return;

        osbase.RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        osbase.RegisterEventHandler<EventMapTransition>(OnMapTransition);

        // Kick off the recurring loop AFTER server is ready
        Server.NextFrame(() => osbase!.AddTimer(checkInterval, Tick));
    }

    private HookResult OnMapTransition(EventMapTransition _, GameEventInfo __) {
        tracked.Clear();
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn e, GameEventInfo __) {
        var p = e.Userid;
        if (p == null || !p.IsValid || p.IsBot) return HookResult.Continue;

        // Defer to next frame so pawn/scene node exists
        Server.NextFrame(() => {
            try {
                if (p == null || !p.IsValid || p.IsBot || p.TeamNum < 2) return;
                if (p.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;

                var origin = p.PlayerPawn?.Value?.CBodyComponent?.SceneNode?.AbsOrigin;
                if (origin == null) return;

                tracked[p.Index] = new PlayerData { Origin = origin, StillCount = 0 };
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] deferred spawn: {ex.Message}");
            }
        });

        return HookResult.Continue;
    }

    // Self-rescheduling heartbeat
    private void Tick() {
        try { CheckPlayers(); }
        catch (Exception ex) { Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Tick: {ex.Message}"); }
        finally { osbase?.AddTimer(checkInterval, Tick); } // <- run again
    }

    private void CheckPlayers() {
        if (tracked.Count == 0) return;

        var forget = new List<uint>();
        foreach (var p in Utilities.GetPlayers()) {
            if (p == null || !p.IsValid || p.IsBot || p.TeamNum < 2) continue;
            if (!tracked.TryGetValue(p.Index, out var d)) continue;

            var o = p.PlayerPawn?.Value?.CBodyComponent?.SceneNode?.AbsOrigin;
            if (o == null || d.Origin == null) { forget.Add(p.Index); continue; }

            var dx = d.Origin.X - o.X; var dy = d.Origin.Y - o.Y; var dz = d.Origin.Z - o.Z;
            var dist = (float)Math.Sqrt(dx*dx + dy*dy + dz*dz);

            if (dist < moveThreshold) {
                d.StillCount++;
                if (d.StillCount == warnAfter)
                    p.PrintToChat($"{ChatColors.Red}[AFK] Move now or you'll be moved soon!");
                else if (d.StillCount >= moveAfter) {
                    Server.PrintToChatAll($"{ChatColors.Grey}[AFK] {ChatColors.Red}{p.PlayerName}{ChatColors.Grey} moved to spectators.");
                    p.ChangeTeam(CsTeam.Spectator);
                    forget.Add(p.Index);
                }
            } else {
                // player moved → stop tracking until next spawn
                forget.Add(p.Index);
            }
        }
        foreach (var id in forget) tracked.Remove(id);
    }
}