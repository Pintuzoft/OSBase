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

    private readonly Dictionary<uint, PlayerData> tracked = new();
    private float checkInterval = 10f;
    private float moveThreshold = 5f;
    private int warnAfter = 3;
    private int moveAfter = 6;

    private class PlayerData { public Vector? Origin; public int StillCount; }

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        config.RegisterGlobalConfigValue(ModuleName, "0");
        if (osbase == null || config == null) { Console.WriteLine($"[ERROR] OSBase[{ModuleName}] null refs."); return; }

        var raw = config.GetGlobalConfigValue(ModuleName, "0")?.Trim().Trim('=', ' ');
        if (!string.Equals(raw, "1", StringComparison.Ordinal)) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in OSBase.cfg."); return;
        }

        createCustomConfigs();
        loadEventHandlers();
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded (interval={checkInterval}s).");
    }

    private void createCustomConfigs() {
        if (config == null) return;
        config.CreateCustomConfig("idle.cfg",
            "// Idle\ncheck_interval=10\nmove_threshold=5\nwarn_after=3\nmove_after=6\n");
        foreach (var line in config.FetchCustomConfig("idle.cfg") ?? new List<string>()) {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("//")) continue;
            var kv = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;
            if (kv[0].Equals("check_interval", StringComparison.OrdinalIgnoreCase)) float.TryParse(kv[1], out checkInterval);
            else if (kv[0].Equals("move_threshold", StringComparison.OrdinalIgnoreCase)) float.TryParse(kv[1], out moveThreshold);
            else if (kv[0].Equals("warn_after", StringComparison.OrdinalIgnoreCase)) int.TryParse(kv[1], out warnAfter);
            else if (kv[0].Equals("move_after", StringComparison.OrdinalIgnoreCase)) int.TryParse(kv[1], out moveAfter);
        }
    }

    private void loadEventHandlers() {
        if (osbase == null) return;

        osbase.RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        osbase.RegisterEventHandler<EventMapTransition>(OnMapTransition);

        // start loop after the server is fully up
        Server.NextFrame(() => osbase.AddTimer(checkInterval, CheckPlayers));
    }

    private HookResult OnMapTransition(EventMapTransition _, GameEventInfo __) {
        tracked.Clear();
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn e, GameEventInfo __) {
        // Defer origin snapshot to avoid nulls during spawn init
        var p = e.Userid;
        if (p == null || !p.IsValid || p.IsBot) return HookResult.Continue;

        Server.NextFrame(() => {
            try {
                if (p == null || !p.IsValid || p.IsBot || p.TeamNum < 2) return;
                if (p.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;

                var pawn = p.PlayerPawn?.Value;
                var origin = pawn?.CBodyComponent?.SceneNode?.AbsOrigin;
                if (origin == null) return;

                tracked[p.Index] = new PlayerData { Origin = origin, StillCount = 0 };
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] deferred spawn grab: {ex.Message}");
            }
        });

        return HookResult.Continue;
    }

    private void CheckPlayers() {
        try {
            if (osbase == null || tracked.Count == 0) return;

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
                    if (d.StillCount == warnAfter) {
                        p.PrintToChat($"{ChatColors.Red}[AFK] Move now or you'll be moved soon!");
                    } else if (d.StillCount >= moveAfter) {
                        Server.PrintToChatAll($"{ChatColors.Grey}[AFK] {ChatColors.Red}{p.PlayerName}{ChatColors.Grey} moved to spectators.");
                        p.ChangeTeam(CsTeam.Spectator);
                        forget.Add(p.Index);
                    }
                } else {
                    // moved → stop tracking; they’ll be re-tracked next spawn
                    forget.Add(p.Index);
                }
            }
            foreach (var id in forget) tracked.Remove(id);
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] CheckPlayers: {ex.Message}");
        }
    }
}