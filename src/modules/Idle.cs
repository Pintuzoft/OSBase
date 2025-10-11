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

    // cfg (idle.cfg)
    private float checkInterval = 10f; // seconds
    private float moveThreshold = 5f;  // units; hash quantization
    private int   warnAfter     = 3;   // unchanged checks before warn
    private int   moveAfter     = 6;   // unchanged checks before action

    // state
    private readonly Dictionary<uint, Tracked> tracked = new();
    private bool roundActive = false;
    private CounterStrikeSharp.API.Modules.Timers.Timer? loopTimer;

    private class Tracked {
        public int BaselineHash;
        public int StillCount;
    }

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        // toggle via OSBase.cfg (default OFF)
        config.RegisterGlobalConfigValue(ModuleName, "0");
        if (config.GetGlobalConfigValue(ModuleName, "0") != "1") return;

        // per-module config
        config.CreateCustomConfig("idle.cfg",
            "// Idle module\n" +
            "check_interval=10\n" +
            "move_threshold=5\n" +
            "warn_after=3\n" +
            "move_after=6\n"
        );
        foreach (var line in config.FetchCustomConfig("idle.cfg") ?? new List<string>()) {
            var s = line.Trim();
            if (s.Length == 0 || s.StartsWith("//")) continue;
            var kv = s.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;
            switch (kv[0].ToLowerInvariant()) {
                case "check_interval": if (float.TryParse(kv[1], out var ci)) checkInterval = MathF.Max(1f, ci); break;
                case "move_threshold": if (float.TryParse(kv[1], out var mt)) moveThreshold = MathF.Max(0.1f, mt); break;
                case "warn_after":     if (int.TryParse(kv[1], out var wa)) warnAfter = Math.Max(1, wa); break;
                case "move_after":     if (int.TryParse(kv[1], out var ma)) moveAfter = Math.Max(warnAfter, ma); break;
            }
        }

        // events
        osbase.RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        osbase.RegisterEventHandler<EventMapTransition>((_, __) => {
            StopLoop();
            tracked.Clear();
            roundActive = false;
            return HookResult.Continue;
        });
    }

    // === events ===

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd _, GameEventInfo __) {
        roundActive = true;
        tracked.Clear();

        // snapshot alive humans at freeze end
        foreach (var p in Utilities.GetPlayers()) {
            if (!IsAliveHuman(p)) continue;
            if (!TryGetPos(p!, out var pos)) continue;
            tracked[p!.Index] = new Tracked { BaselineHash = Hash(pos), StillCount = 0 };
        }

        StartLoop(); // first tick after checkInterval
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd _, GameEventInfo __) {
        roundActive = false;
        StopLoop();
        tracked.Clear();
        return HookResult.Continue;
    }

    // === loop ===

    private void StartLoop() {
        StopLoop();
        loopTimer = osbase!.AddTimer(
            checkInterval,
            Tick,
            CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE
        );
    }

    private void StopLoop() {
        loopTimer?.Kill();
        loopTimer = null;
    }

    private void Tick() {
        try {
            if (roundActive) CheckTracked();
        } catch (Exception ex) {
            Console.WriteLine($"[Idle] {ex}");
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

    // === core ===

    private void CheckTracked() {
        if (tracked.Count == 0) return;

        var players = Utilities.GetPlayers();
        if (players == null || players.Count == 0) return;

        var forget = new List<uint>();

        foreach (var (idx, data) in tracked) {
            CCSPlayerController? p = null;
            foreach (var c in players) {
                if (c != null && c.IsValid && c.Index == idx) { p = c; break; }
            }
            if (!IsAliveHuman(p)) { forget.Add(idx); continue; }
            if (!TryGetPos(p!, out var pos)) { forget.Add(idx); continue; }

            var cur = Hash(pos);
            if (cur == data.BaselineHash) {
                data.StillCount++;

                if (data.StillCount == warnAfter) {
                    // Yellow reads as "orange" in most HUDs
                    p!.PrintToChat($"{ChatColors.Yellow}[⚠ AFK Warning]{ChatColors.Default} You’re idle! Move now or you will be {ChatColors.Red}moved to spectators{ChatColors.Default}.");
                } else if (data.StillCount >= moveAfter) {
                    var name = p!.PlayerName ?? "Player";
                    // Last-alive check on the player's current team
                    var teamAlive = AliveHumansOnTeam(p.TeamNum);

                    if (teamAlive <= 1) {
                        // Last alive → slay AND move to spec (ensures round ends normally)
                        var pawn = p.PlayerPawn?.Value;
                        if (pawn != null && pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE) {
                            pawn.CommitSuicide(false, true);
                        }
                        p.ChangeTeam(CsTeam.Spectator);
                        Server.PrintToChatAll($"{ChatColors.Grey}[AFK]{ChatColors.Yellow} {name}{ChatColors.Default} was last alive, {ChatColors.Red}slain{ChatColors.Default} and moved to spectators for being idle.");
                    } else {
                        // Not last alive → move to spectators only
                        p.ChangeTeam(CsTeam.Spectator);
                        Server.PrintToChatAll($"{ChatColors.Grey}[AFK]{ChatColors.Red} {name} {ChatColors.Grey}was moved to spectators for being idle.");
                    }

                    forget.Add(idx);
                }
            } else {
                // player moved ⇒ stop tracking for the rest of the round
                forget.Add(idx);
            }
        }

        foreach (var i in forget) tracked.Remove(i);
    }

    // === helpers ===

    private static bool IsAliveHuman(CCSPlayerController? p) {
        if (p == null || !p.IsValid || p.IsHLTV || p.IsBot) return false;
        if (p.Connected != PlayerConnectedState.PlayerConnected) return false;
        if (p.TeamNum != (int)CsTeam.Terrorist && p.TeamNum != (int)CsTeam.CounterTerrorist) return false;

        var ph = p.PlayerPawn; if (ph == null || !ph.IsValid) return false;
        var pawn = ph.Value;   if (pawn == null) return false;
        if (pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return false;

        return pawn.AbsOrigin != null;
    }

    private static bool TryGetPos(CCSPlayerController p, out Vector pos) {
        pos = new Vector();
        var ph = p.PlayerPawn; if (ph == null || !ph.IsValid) return false;
        var pawn = ph.Value;   if (pawn == null || pawn.AbsOrigin == null) return false;
        var a = pawn.AbsOrigin; pos = new Vector(a.X, a.Y, a.Z);
        return true;
    }

    private int Hash(in Vector v) {
        var s = moveThreshold <= 0f ? 1f : moveThreshold;
        int qx = (int)MathF.Round(v.X / s);
        int qy = (int)MathF.Round(v.Y / s);
        int qz = (int)MathF.Round(v.Z / s);
        return (qx * 73856093) ^ (qy * 19349663) ^ (qz * 83492791);
    }

    private static int AliveHumansOnTeam(int teamNum) {
        int n = 0;
        foreach (var pl in Utilities.GetPlayers()) {
            if (pl == null || !pl.IsValid || pl.IsHLTV || pl.IsBot) continue;
            if (pl.Connected != PlayerConnectedState.PlayerConnected) continue;
            if (pl.TeamNum != teamNum) continue;

            var ph = pl.PlayerPawn; if (ph == null || !ph.IsValid) continue;
            var pawn = ph.Value;    if (pawn == null) continue;
            if (pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE) n++;
        }
        return n;
    }
}