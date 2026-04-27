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

    private const float AssignDelay = 0.15f;
    private const float RetryDelay = 0.35f;
    private const int AssignAttempts = 8;

    private const float WarmupRespawnDelay = 0.20f;
    private const float SweepDelay = 0.75f;
    private const float GuardSeconds = 2.50f;
    private const float AutoAssignCloseAt = 55.0f;

    private int stateGeneration = 0;
    private bool warmupActive = false;
    private bool autoAssignOpen = false;

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

        Console.WriteLine(
            $"[DEBUG] OSBase[{ModuleName}] loaded (assign={AssignDelay:0.00}s retry={RetryDelay:0.00}s attempts={AssignAttempts} respawn={WarmupRespawnDelay:0.00}s close={AutoAssignCloseAt:0.00}s).");
    }

    public void Unload() {
        isActive = false;
        ResetState();

        if (osbase != null && handlersLoaded) {
            osbase.DeregisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
            osbase.DeregisterEventHandler<EventRoundAnnounceWarmup>(OnRoundAnnounceWarmup);
            osbase.DeregisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
            osbase.DeregisterEventHandler<EventRoundStart>(OnRoundStart);
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
        osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        osbase.RegisterEventHandler<EventMapTransition>(OnMapTransition);
        osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);

        handlersLoaded = true;
    }

    private void OnMapStart(string mapName) {
        ResetState();
    }

    private HookResult OnRoundAnnounceWarmup(EventRoundAnnounceWarmup ev, GameEventInfo info) {
        stateGeneration++;
        warmupActive = true;
        autoAssignOpen = true;

        pendingAssignments.Clear();
        pendingWarmupRespawns.Clear();
        recentAutoAssign.Clear();

        int generation = stateGeneration;

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] warmup started, autoassign open.");

        osbase?.AddTimer(0.10f, () => {
            QueueUnassignedWarmupPlayers(generation);
        }, TimerFlags.STOP_ON_MAPCHANGE);

        ScheduleWarmupSweep(generation);

        osbase?.AddTimer(AutoAssignCloseAt, () => {
            if (!isActive || generation != stateGeneration) {
                return;
            }

            autoAssignOpen = false;
            pendingAssignments.Clear();

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] autoassign closed for warmup.");
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    private HookResult OnWarmupEnd(EventWarmupEnd ev, GameEventInfo info) {
        ResetState();
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] warmup ended.");
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info) {
        if (warmupActive && autoAssignOpen) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] ignored round_start while warmup autoassign window is open.");
            return HookResult.Continue;
        }

        ResetState();
        return HookResult.Continue;
    }

    private HookResult OnMapTransition(EventMapTransition ev, GameEventInfo info) {
        ResetState();
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull ev, GameEventInfo info) {
        if (!isActive || osbase == null || !warmupActive || !autoAssignOpen) {
            return HookResult.Continue;
        }

        var player = ev.Userid;
        if (!IsKnownHuman(player)) {
            return HookResult.Continue;
        }

        int generation = stateGeneration;
        TryQueuePlayer(player, generation, "connect_full");

        return HookResult.Continue;
    }

    private void ScheduleWarmupSweep(int generation) {
        osbase?.AddTimer(SweepDelay, () => {
            if (!isActive || osbase == null || generation != stateGeneration || !warmupActive || !autoAssignOpen) {
                return;
            }

            QueueUnassignedWarmupPlayers(generation);
            ScheduleWarmupSweep(generation);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void QueueUnassignedWarmupPlayers(int generation) {
        if (!isActive || osbase == null || generation != stateGeneration || !warmupActive || !autoAssignOpen) {
            return;
        }

        foreach (var player in Utilities.GetPlayers()) {
            if (!IsEligiblePlayer(player)) {
                continue;
            }

            if (IsPlayable(player.TeamNum)) {
                continue;
            }

            TryQueuePlayer(player, generation, "sweep");
        }
    }

    private bool TryQueuePlayer(CCSPlayerController? player, int generation, string source) {
        if (!isActive || osbase == null || generation != stateGeneration || !warmupActive || !autoAssignOpen) {
            return false;
        }

        if (!IsKnownHuman(player)) {
            return false;
        }

        ulong steamId = player!.SteamID;
        if (steamId == 0) {
            return false;
        }

        if (IsPlayable(player.TeamNum)) {
            recentAutoAssign[steamId] = DateTime.UtcNow.AddSeconds(GuardSeconds);

            if (!player.PawnIsAlive) {
                ScheduleWarmupRespawn(steamId, generation);
            }

            return false;
        }

        if (!pendingAssignments.Add(steamId)) {
            return false;
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] queued steamid={steamId} source={source}.");

        for (int i = 0; i < AssignAttempts; i++) {
            int attempt = i + 1;
            float delay = AssignDelay + (RetryDelay * i);

            osbase.AddTimer(delay, () => {
                TryAssignPlayer(steamId, generation, attempt, source);
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }

        osbase.AddTimer(AssignDelay + (RetryDelay * AssignAttempts) + 0.25f, () => {
            FinishAssignment(steamId, generation);
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return true;
    }

    private void TryAssignPlayer(ulong steamId, int generation, int attempt, string source) {
        if (!isActive || osbase == null || generation != stateGeneration || !warmupActive || !autoAssignOpen) {
            return;
        }

        try {
            var player = FindHumanBySteamId(steamId);
            if (!IsEligiblePlayer(player)) {
                return;
            }

            var safePlayer = player!;

            if (IsPlayable(safePlayer.TeamNum)) {
                recentAutoAssign[steamId] = DateTime.UtcNow.AddSeconds(GuardSeconds);

                if (!safePlayer.PawnIsAlive) {
                    ScheduleWarmupRespawn(steamId, generation);
                }

                return;
            }

            CountTeams(out int ct, out int tt);

            var intendedTeam = DecideTeamForJoin(ct, tt);
            recentAutoAssign[steamId] = DateTime.UtcNow.AddSeconds(GuardSeconds);

            Console.WriteLine(
                $"[DEBUG] OSBase[{ModuleName}] assign steamid={steamId} source={source} attempt={attempt}/{AssignAttempts} ct={ct} t={tt} -> {intendedTeam}");

            safePlayer.ChangeTeam(intendedTeam);
            ScheduleWarmupRespawn(steamId, generation);
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] TryAssignPlayer failed for {steamId}: {ex.Message}");
        }
    }

    private void FinishAssignment(ulong steamId, int generation) {
        if (!isActive || generation != stateGeneration) {
            return;
        }

        pendingAssignments.Remove(steamId);
        CleanupExpiredGuard(steamId);

        try {
            var player = FindHumanBySteamId(steamId);
            if (!IsEligiblePlayer(player)) {
                return;
            }

            var safePlayer = player!;
            if (!IsPlayable(safePlayer.TeamNum)) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] failed to autoassign steamid={steamId}; player still has team={safePlayer.TeamNum}.");
                return;
            }

            AnnounceFinalTeam(safePlayer);
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] FinishAssignment failed for {steamId}: {ex.Message}");
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
                if (!isActive || osbase == null || !warmupActive || generation != stateGeneration) {
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

    private void AnnounceFinalTeam(CCSPlayerController player) {
        if (!IsEligiblePlayer(player) || !IsPlayable(player!.TeamNum)) {
            return;
        }

        var finalTeam = (CsTeam)player.TeamNum;
        string color = finalTeam == CsTeam.CounterTerrorist ? "\x0B" : "\x02";

        player.PrintToChat($" \x04[AutoAssign]\x01 You were assigned to the {color}{finalTeam}\x01 team.");
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

    private void CleanupExpiredGuard(ulong steamId) {
        if (!recentAutoAssign.TryGetValue(steamId, out var until)) {
            return;
        }

        if (DateTime.UtcNow > until) {
            recentAutoAssign.Remove(steamId);
        }
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
        autoAssignOpen = false;
        pendingAssignments.Clear();
        pendingWarmupRespawns.Clear();
        recentAutoAssign.Clear();
    }

    private static bool IsPlayable(int teamNum) {
        return teamNum == (int)CsTeam.Terrorist || teamNum == (int)CsTeam.CounterTerrorist;
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