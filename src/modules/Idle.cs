using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules;

public class Idle : IModule {
    public string ModuleName => "idle";

    private OSBase? osbase;
    private Config? config;

    // state
    private readonly Dictionary<uint, PlayerData> tracked = new();

    // config (defaults)
    private float checkInterval = 10f; // seconds between checks
    private float moveThreshold = 5f;  // distance to count as movement
    private int   warnAfter     = 3;   // consecutive idle checks before warn
    private int   moveAfter     = 6;   // consecutive idle checks before spec
    private bool  debug         = false;

    private class PlayerData {
        public Vector? Origin = null;
        public int StillCount = 0;
    }

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        // Default OFF; do nothing unless explicitly enabled
        config.RegisterGlobalConfigValue(ModuleName, "0");
        if (config.GetGlobalConfigValue(ModuleName, "0") != "1")
            return;

        // Create & parse idle.cfg
        config.CreateCustomConfig("idle.cfg",
            "// Idle module configuration\n" +
            "check_interval=10\n" +
            "move_threshold=5\n" +
            "warn_after=3\n" +
            "move_after=6\n" +
            "debug=0\n"
        );
        foreach (var raw in config.FetchCustomConfig("idle.cfg") ?? new List<string>()) {
            var line = raw?.Trim(); if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) continue;
            var kv = line.Split('=', 2, StringSplitOptions.TrimEntries); if (kv.Length != 2) continue;
            switch (kv[0].ToLowerInvariant()) {
                case "check_interval": float.TryParse(kv[1], out checkInterval); break;
                case "move_threshold": float.TryParse(kv[1], out moveThreshold); break;
                case "warn_after":     int.TryParse(kv[1],   out warnAfter);     break;
                case "move_after":     int.TryParse(kv[1],   out moveAfter);     break;
                case "debug":          debug = kv[1] == "1" || kv[1].Equals("true", StringComparison.OrdinalIgnoreCase); break;
            }
        }
        if (moveAfter < warnAfter) moveAfter = warnAfter;

        // Start recurring loop after server is ready
        Server.NextFrame(() => osbase!.AddTimer(checkInterval, Tick));
        if (debug) Console.WriteLine($"[DEBUG] OSBase[idle] enabled interval={checkInterval}s");
    }

    // recurring heartbeat
    private void Tick() {
        try { CheckPlayers(); }
        catch (Exception ex) { if (debug) Console.WriteLine($"[ERROR] OSBase[idle] {ex}"); }
        finally { osbase?.AddTimer(checkInterval, Tick); } // re-schedule
    }

    private void CheckPlayers() {
        if (tracked.Count == 0 && !AnyEligiblePlayers()) return;

        var forget = new List<uint>();

        foreach (var p in Utilities.GetPlayers()) {
            if (p == null || !p.IsValid) continue;
            if (p.IsHLTV || p.IsBot) continue;
            if (p.Connected != PlayerConnectedState.PlayerConnected) continue;
            if (p.TeamNum < 2) continue;
            if (p.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;

            var pawn = p.PlayerPawn?.Value;
            var node = pawn?.CBodyComponent?.SceneNode;
            var pos  = node?.AbsOrigin;
            if (pos == null) continue;

            if (!tracked.TryGetValue(p.Index, out var data)) {
                tracked[p.Index] = new PlayerData { Origin = pos, StillCount = 0 };
                continue;
            }

            // manual distance (Vector.Distance doesn't exist)
            var dx = data.Origin!.X - pos.X;
            var dy = data.Origin!.Y - pos.Y;
            var dz = data.Origin!.Z - pos.Z;
            var dist = (float)Math.Sqrt(dx*dx + dy*dy + dz*dz);

            if (dist < moveThreshold) {
                data.StillCount++;
                if (data.StillCount == warnAfter)
                    p.PrintToChat($"{ChatColors.Red}[AFK] Move now or you'll be moved soon!");
                else if (data.StillCount >= moveAfter) {
                    Server.PrintToChatAll($"{ChatColors.Grey}[AFK] {ChatColors.Red}{p.PlayerName}{ChatColors.Grey} moved to spectators.");
                    p.ChangeTeam(CsTeam.Spectator);
                    forget.Add(p.Index);
                }
            } else {
                // moved → stop tracking
                forget.Add(p.Index);
            }
        }

        foreach (var id in forget)
            tracked.Remove(id);
    }

    private static bool AnyEligiblePlayers() {
        foreach (var p in Utilities.GetPlayers()) {
            if (p != null && p.IsValid && !p.IsHLTV && !p.IsBot &&
                p.Connected == PlayerConnectedState.PlayerConnected &&
                p.TeamNum >= 2 &&
                p.LifeState == (byte)LifeState_t.LIFE_ALIVE)
                return true;
        }
        return false;
    }
}