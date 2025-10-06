using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules;

public class Idle : IModule {
    public string ModuleName => "idle";

    private const int TEAM_T  = (int)CsTeam.Terrorist;
    private const int TEAM_CT = (int)CsTeam.CounterTerrorist;

    private OSBase? osbase;
    private Config? config;

    // cfg
    private float checkInterval = 10f; // seconds between checks
    private float moveThreshold = 5f;  // units; hashing quantizes by this
    private int   warnAfter     = 3;   // equal-hash checks before warn
    private int   moveAfter     = 6;   // equal-hash checks before move
    private bool  debug         = false;

    // state (only players baseline-captured at FreezeEnd are tracked)
    private readonly Dictionary<uint, PlayerData> tracked = new();
    private bool roundActive = false;
    private CounterStrikeSharp.API.Modules.Timers.Timer? loopTimer;

    private class PlayerData {
        public int BaselineHash { get; set; }
        public int StillCount   { get; set; }
    }

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        // default OFF in global cfg
        config.RegisterGlobalConfigValue(ModuleName, "0");
        if (config.GetGlobalConfigValue(ModuleName, "0") != "1") {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in OSBase.cfg.");
            return;
        }

        CreateCustomConfigs();
        LoadEventHandlers();

        if (debug)
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded (interval={checkInterval}s thr={moveThreshold} warn={warnAfter} move={moveAfter})");
    }

    private void CreateCustomConfigs() {
        if (config == null) return;

        config.CreateCustomConfig("idle.cfg",
            "// Idle module configuration\n" +
            "check_interval=10\n" +
            "move_threshold=5\n" +
            "warn_after=3\n" +
            "move_after=6\n" +
            "debug=0\n"
        );

        foreach (var raw in config.FetchCustomConfig("idle.cfg") ?? new List<string>()) {
            var line = raw?.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) continue;
            var kv = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;

            switch (kv[0].ToLowerInvariant()) {
                case "check_interval": if (float.TryParse(kv[1], out var ci)) checkInterval = ci; break;
                case "move_threshold": if (float.TryParse(kv[1], out var mt)) moveThreshold = mt; break;
                case "warn_after":     if (int.TryParse(kv[1],   out var wa)) warnAfter = wa;     break;
                case "move_after":     if (int.TryParse(kv[1],   out var ma)) moveAfter = ma;     break;
                case "debug":          debug = kv[1] == "1" || kv[1].Equals("true", StringComparison.OrdinalIgnoreCase); break;
            }
        }

        if (moveAfter < warnAfter) moveAfter = warnAfter;
        if (checkInterval < 1f)    checkInterval = 1f;
        if (moveThreshold < 0.1f)  moveThreshold = 0.1f;
    }

    private void LoadEventHandlers() {
        if (osbase == null) return;

        osbase.RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        osbase.RegisterEventHandler<EventMapTransition>((_, __) => {
            StopLoop();
            tracked.Clear();
            roundActive = false;
            if (debug) Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] map transition -> cleared");
            return HookResult.Continue;
        });
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd _, GameEventInfo __) {
        roundActive = true;
        tracked.Clear();

        // capture baseline of all alive humans exactly at freeze end
        int captured = 0;
        foreach (var p in Utilities.GetPlayers()) {
            if (!IsAliveHuman(p)) continue;
            if (!TryGetPosition(p!, out var pos)) continue;

            int h = HashPosition(pos);
            tracked[p!.Index] = new PlayerData { BaselineHash = h, StillCount = 0 };
            captured++;
        }

        if (debug) Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] FreezeEnd: captured {captured} baselines");

        // start loop (first tick after checkInterval)
        StartLoop(0f);
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd _, GameEventInfo __) {
        roundActive = false;
        StopLoop();
        tracked.Clear();
        if (debug) Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] RoundEnd: stopped & cleared");
        return HookResult.Continue;
    }

    private void StartLoop(float delay) {
        StopLoop(); // ensure no dupes
        float first = delay <= 0f ? checkInterval : delay;
        loopTimer = osbase!.AddTimer(
            first,
            Tick,
            CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE
        );
        if (debug) Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loop armed (first in {first:0.#}s, interval {checkInterval:0.#}s)");
    }

    private void StopLoop() {
        if (loopTimer == null) return;
        loopTimer.Kill();
        loopTimer = null;
    }

    private void Tick() {
        try {
            if (roundActive) {
                CheckTrackedPlayers();
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Tick: {ex}");
        } finally {
            if (roundActive) {
                loopTimer = osbase?.AddTimer(
                    checkInterval,
                    Tick,
                    CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE
                );
            }
        }
    }

    // ——— core logic ———

    private void CheckTrackedPlayers() {
        if (tracked.Count == 0) return;

        // build an index->controller map for quick lookups
        var players = Utilities.GetPlayers();
        if (players == null || players.Count == 0) return;

        var toForget = new List<uint>();

        foreach (var kvp in tracked) {
            uint idx = kvp.Key;
            var data = kvp.Value;

            CCSPlayerController? p = null;
            foreach (var candidate in players) {
                if (candidate != null && candidate.IsValid && candidate.Index == idx) { p = candidate; break; }
            }
            if (!IsAliveHuman(p)) { toForget.Add(idx); continue; }
            if (!TryGetPosition(p!, out var pos)) { toForget.Add(idx); continue; }

            int cur = HashPosition(pos);
            if (cur == data.BaselineHash) {
                data.StillCount++;

                if (debug) Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {p!.PlayerName ?? "Player"} still={data.StillCount}");

                if (data.StillCount == warnAfter) {
                    p!.PrintToChat($"{ChatColors.Red}[AFK]{ChatColors.Default} Move now or you'll be moved!");
                }
                if (data.StillCount >= moveAfter) {
                    var name = p!.PlayerName ?? "Player";
                    Server.PrintToChatAll($"{ChatColors.Grey}[AFK] {ChatColors.Red}{name}{ChatColors.Grey} moved to spectators.");
                    p!.ChangeTeam(CsTeam.Spectator);
                    toForget.Add(idx);
                }
            } else {
                // moved -> stop tracking this player for the rest of the round
                toForget.Add(idx);
                if (debug) Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {p!.PlayerName ?? "Player"} moved -> untracked");
            }
        }

        // cleanup
        foreach (var idx in toForget)
            tracked.Remove(idx);
    }

    // ——— helpers ———

    private static bool IsAliveHuman(CCSPlayerController? p) {
        if (p == null || !p.IsValid) return false;
        if (p.IsHLTV || p.IsBot) return false;
        if (p.Connected != PlayerConnectedState.PlayerConnected) return false;
        if (p.TeamNum != (int)CsTeam.Terrorist && p.TeamNum != (int)CsTeam.CounterTerrorist) return false;

        var pawnHandle = p.PlayerPawn;
        if (pawnHandle == null || !pawnHandle.IsValid) return false;
        var pawn = pawnHandle.Value;
        if (pawn == null) return false;
        if (pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return false;

        // AbsOrigin must exist for alive pawn
        return pawn.AbsOrigin != null;
    }

    private static bool TryGetPosition(CCSPlayerController player, out Vector pos) {
        pos = new Vector();

        var pawnHandle = player.PlayerPawn;
        if (pawnHandle == null || !pawnHandle.IsValid) return false;

        var pawn = pawnHandle.Value;
        if (pawn == null || pawn.AbsOrigin == null) return false;

        var a = pawn.AbsOrigin;
        pos = new Vector(a.X, a.Y, a.Z);
        return true;
    }

    // integer spatial hash with quantization by moveThreshold
    private int HashPosition(in Vector v) {
        float scale = moveThreshold <= 0f ? 1f : moveThreshold;
        int qx = (int)MathF.Round(v.X / scale);
        int qy = (int)MathF.Round(v.Y / scale);
        int qz = (int)MathF.Round(v.Z / scale);
        return (qx * 73856093) ^ (qy * 19349663) ^ (qz * 83492791);
    }
}