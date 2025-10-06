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

    // cfg
    private float checkInterval = 10f;
    private float moveThreshold = 5f;
    private int   warnAfter     = 3;
    private int   moveAfter     = 6;
    private bool  debug         = false;

    // state
    private readonly Dictionary<uint, PlayerData> tracked = new();
    private bool roundActive = false;

    private class PlayerData {
        public Vector Origin { get; set; } = new Vector(); // initialized
        public int StillCount { get; set; }
        public bool HasBaseline { get; set; }
    }

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        // default OFF
        config.RegisterGlobalConfigValue(ModuleName, "0");
        if (config.GetGlobalConfigValue(ModuleName, "0") != "1") {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in OSBase.cfg.");
            return;
        }

        CreateCustomConfigs();
        LoadEventHandlers();

        // start loop
        Server.NextFrame(() => osbase!.AddTimer(checkInterval, Tick));

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
    }

    private void LoadEventHandlers() {
        if (osbase == null) return;

        osbase.RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        osbase.RegisterEventHandler<EventMapTransition>((_, __) => {
            tracked.Clear();
            roundActive = false;
            if (debug) Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] map transition → cleared state");
            return HookResult.Continue;
        });
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd _, GameEventInfo __) {
        roundActive = true;
        tracked.Clear();

        var players = Utilities.GetPlayers();
        foreach (var p in players) {
            if (p == null || !p.IsValid || p.IsHLTV || p.IsBot) continue;
            if (p.Connected != PlayerConnectedState.PlayerConnected) continue;
            if (p.TeamNum < 2) continue;

            var pawnHandle = p.PlayerPawn;
            if (pawnHandle == null || !pawnHandle.IsValid) continue;

            var pawn = pawnHandle.Value;
            if (pawn == null) continue;
            if (pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;

            // AbsOrigin is valid for an alive pawn; use null-forgiving to silence analyzer.
            var pos = pawn.AbsOrigin!;
            tracked[p.Index] = new PlayerData {
                Origin = new Vector(pos.X, pos.Y, pos.Z),
                StillCount = 0,
                HasBaseline = true
            };
        }

        if (debug) Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] baselines snapshotted: {tracked.Count}");
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd _, GameEventInfo __) {
        roundActive = false;
        tracked.Clear();
        if (debug) Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] round end → cleared baselines");
        return HookResult.Continue;
    }

    private void Tick() {
        try {
            if (roundActive) CheckPlayers();
        } catch (Exception ex) {
            if (debug) Console.WriteLine($"[ERROR] OSBase[{ModuleName}] {ex}");
        } finally {
            osbase?.AddTimer(checkInterval, Tick); // reschedule
        }
    }

    private void CheckPlayers() {
        if (tracked.Count == 0) return;

        var forget = new List<uint>();
        var players = Utilities.GetPlayers();

        foreach (var p in players) {
            if (p == null || !p.IsValid || p.IsHLTV || p.IsBot) continue;
            if (p.Connected != PlayerConnectedState.PlayerConnected) continue;
            if (p.TeamNum < 2) continue;

            var pawnHandle = p.PlayerPawn;
            if (pawnHandle == null || !pawnHandle.IsValid) { forget.Add(p.Index); continue; }

            var pawn = pawnHandle.Value;
            if (pawn == null) { forget.Add(p.Index); continue; }
            if (pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) { forget.Add(p.Index); continue; }

            if (!tracked.TryGetValue(p.Index, out var data)) continue;
            if (!data.HasBaseline) continue;

            var basePos = data.Origin;           // initialized
            var curPos  = pawn.AbsOrigin!;       // assert non-null for alive pawn

            var dx = basePos.X - curPos.X;
            var dy = basePos.Y - curPos.Y;
            var dz = basePos.Z - curPos.Z;
            var dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);

            if (dist < moveThreshold) {
                data.StillCount++;

                if (data.StillCount == warnAfter) {
                    p.PrintToChat($"{ChatColors.Red}[AFK]{ChatColors.Default} Move now or you'll be moved to spectators!");
                } else if (data.StillCount >= moveAfter) {
                    var name = p.PlayerName ?? "Player";
                    Server.PrintToChatAll($"{ChatColors.Grey}[AFK] {ChatColors.Red}{name}{ChatColors.Grey} moved to spectators.");
                    p.ChangeTeam(CsTeam.Spectator);
                    forget.Add(p.Index);
                }
            } else {
                // moved → stop tracking this round
                forget.Add(p.Index);
            }
        }

        if (forget.Count > 0) {
            foreach (var id in forget)
                tracked.Remove(id);
        }
    }
}