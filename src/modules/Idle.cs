using System;
using System.Collections.Generic;
using System.IO;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using System.Reflection;

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

    private class PlayerData {
        public Vector? Origin;
        public int StillCount;
    }

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        // Global toggle in OSBase.cfg
        config.RegisterGlobalConfigValue($"{ModuleName}", "1");

        if (osbase == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] osbase is null. {ModuleName} failed to load.");
            return;
        } else if (config == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] config is null. {ModuleName} failed to load.");
            return;
        }

        var raw = config.GetGlobalConfigValue($"{ModuleName}", "0")?.Trim().Trim('=', ' ');
        if (!string.Equals(raw, "1", StringComparison.Ordinal)) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {ModuleName} is disabled in the global configuration.");
            return;
        }

        createCustomConfigs();
        loadEventHandlers();
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully! (interval={checkInterval}s)");
    }

    private void createCustomConfigs() {
        if (config == null) return;

        // Own config file: idle.cfg
        config.CreateCustomConfig("idle.cfg",
            "// Idle module configuration\n" +
            "// check_interval = seconds between checks\n" +
            "// move_threshold = distance in units to count as movement\n" +
            "// warn_after     = consecutive idle checks before warning\n" +
            "// move_after     = consecutive idle checks before move to spec\n" +
            "check_interval=10\n" +
            "move_threshold=5\n" +
            "warn_after=3\n" +
            "move_after=6\n"
        );

        // Parse key=value pairs from idle.cfg
        foreach (var line in config.FetchCustomConfig("idle.cfg") ?? new List<string>()) {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("//")) continue;
            var kv = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;

            switch (kv[0].ToLowerInvariant()) {
                case "check_interval": if (!float.TryParse(kv[1], out checkInterval)) checkInterval = 10f; break;
                case "move_threshold": if (!float.TryParse(kv[1], out moveThreshold)) moveThreshold = 5f; break;
                case "warn_after":     if (!int.TryParse(kv[1], out warnAfter))       warnAfter = 3; break;
                case "move_after":     if (!int.TryParse(kv[1], out moveAfter))       moveAfter = 6; break;
            }
        }
    }

    private void loadEventHandlers() {
        if (osbase == null) return;

        osbase.RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        osbase.RegisterEventHandler<EventMapTransition>(OnMapTransition);

        // Start timer on next frame (safer during init)
        Server.NextFrame(() => osbase.AddTimer(checkInterval, CheckPlayers));
    }

    private HookResult OnMapTransition(EventMapTransition _, GameEventInfo __) {
        tracked.Clear();
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn e, GameEventInfo __) {
        try {
            var p = e.Userid;
            if (p == null || !p.IsValid || p.IsBot || p.TeamNum < 2) return HookResult.Continue;

            var origin = p.PlayerPawn?.Value?.CBodyComponent?.SceneNode?.AbsOrigin;
            if (origin == null) return HookResult.Continue;

            tracked[p.Index] = new PlayerData { Origin = origin, StillCount = 0 };
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] OnPlayerSpawn: {ex.Message}");
        }
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
                        Server.PrintToChatAll($"{ChatColors.Grey}[AFK] {ChatColors.Red}{p.PlayerName}{ChatColors.Grey} was moved to spectators.");
                        p.ChangeTeam(CsTeam.Spectator);
                        forget.Add(p.Index); // stop tracking after move
                    }
                } else {
                    // Player moved → stop tracking; they’ll be retracked next spawn
                    forget.Add(p.Index);
                }
            }
            foreach (var id in forget) tracked.Remove(id);
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] CheckPlayers: {ex.Message}");
        }
    }
}