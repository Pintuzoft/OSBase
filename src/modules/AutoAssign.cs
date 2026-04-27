using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules;

public class AutoAssign : IModule {
    public string ModuleName => "autoassign";

    private OSBase? osbase;
    private Config? config;

    private bool handlersLoaded = false;
    private bool isActive = false;

    private const float AssignDelay = 1.00f;
    private const float WarmupRespawnDelay = 0.20f;
    private const float GuardSeconds = 2.50f;

    private int stateGeneration = 0;
    private bool warmupActive = false;

    private readonly HashSet<ulong> pendingAssignments = new();
    private readonly HashSet<ulong> pendingWarmupRespawns = new();

    // Lets TeamBalancer skip freshly auto-assigned players if it wants to.
    private readonly Dictionary<ulong, DateTime> recentAutoAssign = new();

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;
        isActive = true;

        if (osbase == null || config == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] load failed (null deps).");
            isActive = false;
            return;
        }

        config.RegisterGlobalConfigValue(ModuleName, "0");

        if (config.GetGlobalConfigValue(ModuleName, "0") != "1") {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in global config.");
            isActive = false;
            return;
        }

        ResetState();
        LoadHandlers();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded (assign_delay={AssignDelay:0.00}s).");
    }

    public void Unload() {
        isActive = false;
        ResetState();

        if (osbase != null && handlersLoaded) {
            osbase.DeregisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
            osbase.DeregisterEventHandler<EventRoundAnnounceWarmup>(OnRoundAnnounceWarmup);
            osbase.DeregisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
            osbase.DeregisterEventHandler<EventMapTransition>(OnMapTransition);
            osbase.RemoveListener<Listeners.OnMapStart>(OnMapStart);

            handlersLoaded = false;
        }

        config = null;
        osbase = null;

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] unloaded.");
    }

    public void ReloadConfig(Config inConfig) {
        config = inConfig;
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] config reloaded.");
    }

    private void LoadHandlers() {
        if (osbase == null || handlersLoaded) {
            return;
        }

        osbase.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        osbase.RegisterEventHandler<EventRoundAnnounceWarmup>(OnRoundAnnounceWarmup);
        osbase.RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
        osbase.RegisterEventHandler<EventMapTransition>(OnMapTransition);
        osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);

        handlersLoaded = true;
    }

    private void OnMapStart(string mapName) {
        ResetState();
    }

    private HookResult OnMapTransition(EventMapTransition ev, GameEventInfo info) {
        ResetState();
        return HookResult.Continue;
    }

    private HookResult OnRoundAnnounceWarmup(EventRoundAnnounceWarmup ev, GameEventInfo info) {
        warmupActive = true;
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] warmup started.");
        return HookResult.Continue;
    }

    private HookResult OnWarmupEnd(EventWarmupEnd ev, GameEventInfo info) {
        warmupActive = false;
        pendingWarmupRespawns.Clear();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] warmup ended.");
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull ev, GameEventInfo info) {
        if (!isActive || osbase == null) {
            return HookResult.Continue;
        }

        var player = ev.Userid;
        if (!IsKnownHuman(player) || player!.SteamID == 0) {
            return HookResult.Continue;
        }

        ulong steamId = player.SteamID;

        if (!pendingAssignments.Add(steamId)) {
            return HookResult.Continue;
        }

        int generation = stateGeneration;

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] queued connected player steamid={steamId}.");

        osbase.AddTimer(AssignDelay, () => {
            TryAssignConnectedPlayer(steamId, generation);
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    private void TryAssignConnectedPlayer(ulong steamId, int generation) {
        if (!isActive || osbase == null || generation != stateGeneration) {
            pendingAssignments.Remove(steamId);
            return;
        }

        if (!pendingAssignments.Contains(steamId)) {
            return;
        }

        try {
            var player = FindHumanBySteamId(steamId);
            if (!IsEligiblePlayer(player)) {
                pendingAssignments.Remove(steamId);
                return;
            }

            var safePlayer = player!;

            // Already on a real team. AutoAssign is done and must not fight TeamBalancer.
            if (IsPlayable(safePlayer.TeamNum)) {
                pendingAssignments.Remove(steamId);

                if (warmupActive && !safePlayer.PawnIsAlive) {
                    ScheduleWarmupRespawn(steamId, generation);
                }

                return;
            }

            // Critical guard: only move fresh connects that are still Unassigned/Spectator.
            if (!IsAutoAssignableTeam(safePlayer.TeamNum)) {
                pendingAssignments.Remove(steamId);
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] skipped steamid={steamId}; current_team={safePlayer.TeamNum}.");
                return;
            }

            CountTeams(out int ct, out int tt);
            var intendedTeam = DecideTeamForJoin(ct, tt);

            Console.WriteLine(
                $"[DEBUG] OSBase[{ModuleName}] autoassign steamid={steamId} current_team={safePlayer.TeamNum} ct={ct} t={tt} -> {intendedTeam}");

            // Release before moving so AutoAssign cannot fight any later module logic.
            pendingAssignments.Remove(steamId);
            recentAutoAssign[steamId] = DateTime.UtcNow.AddSeconds(GuardSeconds);

            safePlayer.ChangeTeam(intendedTeam);

            if (warmupActive) {
                ScheduleWarmupRespawn(steamId, generation);
            }
        } catch (Exception ex) {
            pendingAssignments.Remove(steamId);
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] TryAssignConnectedPlayer failed for {steamId}: {ex.Message}");
        }
    }

    private void ScheduleWarmupRespawn(ulong steamId, int generation, float delay = WarmupRespawnDelay) {
        if (!isActive || osbase == null || !warmupActive) {
            return;
        }

        if (!pendingWarmupRespawns.Add(steamId)) {
            return;
        }

        osbase.AddTimer(delay, () => {
            try {
                if (!isActive || osbase == null || generation != stateGeneration || !warmupActive) {
                    return;
                }

                var player = FindHumanBySteamId(steamId);
                if (!IsEligiblePlayer(player)) {
                    return;
                }

                var safePlayer = player!;
                if (!IsPlayable(safePlayer.TeamNum)) {
                    return;
                }

                if (!safePlayer.PawnIsAlive) {
                    safePlayer.Respawn();
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] respawned {steamId} after autoassign.");
                }
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] warmup respawn failed for {steamId}: {ex.Message}");
            } finally {
                pendingWarmupRespawns.Remove(steamId);
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    public bool WasRecentlyAutoAssigned(ulong steamId) {
        if (!recentAutoAssign.TryGetValue(steamId, out var until)) {
            return false;
        }

        if (DateTime.UtcNow > until) {
            recentAutoAssign.Remove(steamId);
            return false;
        }

        return true;
    }

    private void CountTeams(out int ct, out int tt) {
        ct = 0;
        tt = 0;

        foreach (var player in Utilities.GetPlayers()) {
            if (!IsEligiblePlayer(player)) {
                continue;
            }

            if (player!.TeamNum == (int)CsTeam.CounterTerrorist) {
                ct++;
            } else if (player.TeamNum == (int)CsTeam.Terrorist) {
                tt++;
            }
        }
    }

    private static CsTeam DecideTeamForJoin(int ct, int tt) {
        int diffIfCt = Math.Abs((ct + 1) - tt);
        int diffIfT = Math.Abs(ct - (tt + 1));

        if (diffIfCt < diffIfT) {
            return CsTeam.CounterTerrorist;
        }

        if (diffIfT < diffIfCt) {
            return CsTeam.Terrorist;
        }

        return Random.Shared.Next(2) == 0 ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
    }

    private CCSPlayerController? FindHumanBySteamId(ulong steamId) {
        foreach (var player in Utilities.GetPlayers()) {
            if (!IsEligiblePlayer(player)) {
                continue;
            }

            if (player!.SteamID == steamId) {
                return player;
            }
        }

        return null;
    }

    private void ResetState() {
        stateGeneration++;
        warmupActive = false;
        pendingAssignments.Clear();
        pendingWarmupRespawns.Clear();
        recentAutoAssign.Clear();
    }

    private static bool IsPlayable(int teamNum) {
        return teamNum == (int)CsTeam.Terrorist || teamNum == (int)CsTeam.CounterTerrorist;
    }

    private static bool IsAutoAssignableTeam(int teamNum) {
        return teamNum == 0 || teamNum == (int)CsTeam.Spectator;
    }

    private static bool IsKnownHuman(CCSPlayerController? player) {
        return player != null &&
               player.IsValid &&
               !player.IsHLTV &&
               !player.IsBot;
    }

    private static bool IsEligiblePlayer(CCSPlayerController? player) {
        return IsKnownHuman(player) &&
               player!.Connected == PlayerConnectedState.Connected &&
               player.SteamID != 0;
    }
}