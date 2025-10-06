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

    public void Load(OSBase os, Config cfg) {
        osbase = os; config = cfg;
        cfg.RegisterGlobalConfigValue(ModuleName, "1");
        if (cfg.GetGlobalConfigValue(ModuleName, "0") != "1") { Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled."); return; }

        // ensure config exists, then parse it
        cfg.CreateCustomConfig($"{ModuleName}.cfg",
            "// Idle configuration\ncheck_interval=10\nmove_threshold=5\nwarn_after=3\nmove_after=6\n");
        var lines = cfg.FetchCustomConfig($"{ModuleName}.cfg") ?? new List<string>();
        foreach (var line in lines) {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) continue;
            var kv = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;
            switch (kv[0].ToLowerInvariant()) {
                case "check_interval": if (!float.TryParse(kv[1], out checkInterval)) checkInterval = 10f; break;
                case "move_threshold": if (!float.TryParse(kv[1], out moveThreshold)) moveThreshold = 5f; break;
                case "warn_after":     if (!int.TryParse(kv[1], out warnAfter))       warnAfter = 3; break;
                case "move_after":     if (!int.TryParse(kv[1], out moveAfter))       moveAfter = 6; break;
            }
        }

        os.RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        os.RegisterEventHandler<EventMapTransition>((_, __) => { tracked.Clear(); return HookResult.Continue; });

        // IMPORTANT: start timer on next frame so the server is fully up
        Server.NextFrame(() => os.AddTimer(checkInterval, CheckPlayers));

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded (interval={checkInterval}s).");
    }

    private HookResult OnSpawn(EventPlayerSpawn e, GameEventInfo _) {
        var p = e.Userid;
        if (p == null || !p.IsValid || p.IsBot) return HookResult.Continue;
        tracked[p.Index] = new PlayerData {
            Origin = p.PlayerPawn?.Value?.CBodyComponent?.SceneNode?.AbsOrigin,
            StillCount = 0
        };
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
                    if (d.StillCount == warnAfter)
                        p.PrintToChat($"{ChatColors.Red}[AFK] Move now or you'll be moved soon!");
                    else if (d.StillCount >= moveAfter) {
                        Server.PrintToChatAll($"{ChatColors.Grey}[AFK] {ChatColors.Red}{p.PlayerName}{ChatColors.Grey} moved to spectators.");
                        p.ChangeTeam(CsTeam.Spectator);
                        forget.Add(p.Index);
                    }
                } else {
                    // moved → stop tracking until next spawn
                    forget.Add(p.Index);
                }
            }
            foreach (var id in forget) tracked.Remove(id);
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] CheckPlayers exception: {ex.Message}");
            // swallow to avoid killing server
        }
    }
}