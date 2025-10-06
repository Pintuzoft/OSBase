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
    private bool enabled = false;
    private readonly Dictionary<uint, PlayerData> tracked = new();

    // cfg (defaults)
    private float checkInterval = 10f;
    private float moveThreshold = 5f;
    private int   warnAfter     = 3;
    private int   moveAfter     = 6;
    private bool  debug         = true; // flip in idle.cfg

    private class PlayerData { public Vector? Origin = null; public int StillCount = 0; }

    // logging helpers
    private void D(string msg) { if (debug) Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {msg}"); }
    private void E(string msg) { Console.WriteLine($"[ERROR] OSBase[{ModuleName}] {msg}"); }

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        // Default OFF
        config.RegisterGlobalConfigValue(ModuleName, "0");
        var toggle = config.GetGlobalConfigValue(ModuleName, "0");
        D($"Load(): toggle='{toggle}' (needs '1' to enable)");

        if (toggle != "1") { D("Disabled via OSBase.cfg → return (no timers, no handlers)."); return; }
        enabled = true;

        CreateAndParseModuleConfig();
        StartLoop();
        D($"Loaded. interval={checkInterval}s threshold={moveThreshold} warnAfter={warnAfter} moveAfter={moveAfter} debug={(debug?1:0)}");
    }

    private void CreateAndParseModuleConfig() {
        if (config == null) { E("config null in CreateAndParseModuleConfig"); return; }

        config.CreateCustomConfig("idle.cfg",
            "// Idle module configuration\n" +
            "// check_interval: seconds between checks\n" +
            "// move_threshold: distance to count as movement\n" +
            "// warn_after    : consecutive idle checks before warning\n" +
            "// move_after    : consecutive idle checks before move to spec\n" +
            "// debug         : 1=verbose logs, 0=silent\n" +
            "check_interval=10\n" +
            "move_threshold=5\n" +
            "warn_after=3\n" +
            "move_after=6\n" +
            "debug=1\n"
        );

        D("Parsing idle.cfg...");
        foreach (var raw in config.FetchCustomConfig("idle.cfg") ?? new List<string>()) {
            var line = raw?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) continue;

            var kv = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) { D($" skip unparsable: '{line}'"); continue; }

            var k = kv[0].ToLowerInvariant();
            var v = kv[1];

            switch (k) {
                case "check_interval": if (float.TryParse(v, out var ci)) { checkInterval = ci; D($" cfg check_interval={checkInterval}"); } break;
                case "move_threshold": if (float.TryParse(v, out var mt)) { moveThreshold = mt; D($" cfg move_threshold={moveThreshold}"); } break;
                case "warn_after":     if (int.TryParse(v, out var wa))   { warnAfter     = wa; D($" cfg warn_after={warnAfter}"); } break;
                case "move_after":     if (int.TryParse(v, out var ma))   { moveAfter     = ma; D($" cfg move_after={moveAfter}"); } break;
                case "debug":          debug = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); D($" cfg debug={(debug?1:0)}"); break;
                default: D($" cfg unknown key '{k}' (value '{v}')"); break;
            }
        }

        if (moveAfter < warnAfter) { D($" cfg adjust: move_after({moveAfter}) < warn_after({warnAfter}) → move_after=warn_after"); moveAfter = warnAfter; }
    }

    private void StartLoop() {
        if (osbase == null) { E("osbase null in StartLoop"); return; }
        D("StartLoop(): scheduling first Tick via osbase.AddTimer (after NextFrame).");
        Server.NextFrame(() => osbase.AddTimer(checkInterval, Tick));
    }

    // self-rescheduling heartbeat
    private void Tick() {
        Console.WriteLine("[DEBUG] OSBase[idle] Tick(): ENTER");
        try {
            if (!enabled) { D("Tick(): not enabled; return"); return; }
            CheckPlayers();
        } catch (Exception ex) {
            E($"Tick(): exception: {ex}");
        } finally {
            try {
                osbase?.AddTimer(checkInterval, Tick); // reschedule
                D($"Tick(): rescheduled in {checkInterval}s");
            } catch (Exception ex) {
                E($"Tick(): reschedule failed: {ex}");
            }
        }
    }

    private void CheckPlayers() {
        var toForget = new List<uint>();
        var players = Utilities.GetPlayers();
        D($"CheckPlayers(): players={players.Count}, tracked={tracked.Count}");

        foreach (var p in players) {
            try {
                string name = p?.PlayerName ?? "<null>";
                uint idx = p?.Index ?? 0;
                var state = p?.Connected ?? PlayerConnectedState.PlayerDisconnecting;

                D($"  [{idx}] '{name}': valid={p?.IsValid}, isHLTV={p?.IsHLTV}, isBot={p?.IsBot}, state={state}, team={p?.TeamNum}, life={p?.LifeState}");

                if (p == null || !p.IsValid) continue;
                if (p.IsHLTV) continue;
                if (state != PlayerConnectedState.PlayerConnected) continue;
                if (p.IsBot) continue;                 // skip real bots
                if (p.TeamNum < 2) continue;

                // lazy-init baseline
                if (!tracked.TryGetValue(p.Index, out var data)) {
                    var o0 = p.PlayerPawn?.Value?.CBodyComponent?.SceneNode?.AbsOrigin;
                    var alive = p.LifeState == (byte)LifeState_t.LIFE_ALIVE;
                    D($"   not tracked: origin={(o0==null?"null":"ok")}, alive={alive}");
                    if (o0 != null && alive) {
                        tracked[p.Index] = new PlayerData { Origin = o0, StillCount = 0 };
                        D($"    → tracked: ({o0.X:F1},{o0.Y:F1},{o0.Z:F1})");
                    }
                    continue;
                }

                // compare
                var o = p.PlayerPawn?.Value?.CBodyComponent?.SceneNode?.AbsOrigin;
                if (o == null || data.Origin == null) { D("    origin null → forget"); toForget.Add(p.Index); continue; }

                var dx = data.Origin.X - o.X; var dy = data.Origin.Y - o.Y; var dz = data.Origin.Z - o.Z;
                var dist = (float)Math.Sqrt(dx*dx + dy*dy + dz*dz);
                D($"   dist={dist:F2} thr={moveThreshold} still={data.StillCount}");

                if (dist < moveThreshold) {
                    data.StillCount++;
                    D($"    still → count={data.StillCount}");
                    if (data.StillCount == warnAfter) {
                        D("    WARN");
                        p.PrintToChat($"{ChatColors.Red}[AFK] Move now or you'll be moved soon!");
                    } else if (data.StillCount >= moveAfter) {
                        D("    MOVE to spec");
                        Server.PrintToChatAll($"{ChatColors.Grey}[AFK] {ChatColors.Red}{p.PlayerName}{ChatColors.Grey} moved to spectators.");
                        p.ChangeTeam(CsTeam.Spectator);
                        toForget.Add(p.Index);
                    }
                } else {
                    D("    moved → forget");
                    toForget.Add(p.Index);
                }
            } catch (Exception ex) {
                E($"CheckPlayers(): per-player exception: {ex}");
            }
        }

        if (toForget.Count > 0) {
            D($"CheckPlayers(): forgetting {toForget.Count}");
            foreach (var id in toForget) tracked.Remove(id);
        }
    }
}