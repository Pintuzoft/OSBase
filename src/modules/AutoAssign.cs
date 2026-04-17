using System;
using System.Collections.Generic;
using System.Linq;
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

    private const float AssignDelay = 0.20f;
    private const float CorrectionDelay = 0.25f;
    private const float WarmupRespawnDelay = 0.35f;
    private const float GuardSeconds = 1.0f;

    private static readonly object TeamAssignLock = new();

    private bool warmupActive = true;
    private int stateGeneration = 0;

    // Guard recent team to fight engine SPEC fallback
    private readonly Dictionary<ulong, (CsTeam team, DateTime until)> teamGuards = new();

    // Track which team we intentionally assigned.
    // Correction pass may only restore this exact team, never recalculate.
    private readonly Dictionary<ulong, CsTeam> justAutoAssigned = new();

    // Avoid spamming multiple respawn timers for the same player.
    private readonly HashSet<ulong> pendingWarmupRespawns = new();

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

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded (assign={AssignDelay:0.00}s correction={CorrectionDelay:0.00}s).");
    }

    public void Unload() {
        isActive = false;
        ResetState();

        if (osbase != null && handlersLoaded) {
            osbase.DeregisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
            osbase.DeregisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
            osbase.DeregisterEventHandler<EventRoundAnnounceWarmup>(OnRoundAnnounceWarmup);
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
        osbase.RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        osbase.RegisterEventHandler<EventRoundAnnounceWarmup>(OnRoundAnnounceWarmup);
        osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        osbase.RegisterEventHandler<EventMapTransition>(OnMapTransition);
        osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);

        handlersLoaded = true;
    }

    private void OnMapStart(string mapName) {
        ResetState();
    }

    private HookResult OnRoundAnnounceWarmup(EventRoundAnnounceWarmup ev, GameEventInfo info) {
        warmupActive = true;
        stateGeneration++;
        teamGuards.Clear();
        justAutoAssigned.Clear();
        pendingWarmupRespawns.Clear();
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info) {
        warmupActive = false;
        stateGeneration++;
        teamGuards.Clear();
        justAutoAssigned.Clear();
        pendingWarmupRespawns.Clear();
        return HookResult.Continue;
    }

    private HookResult OnMapTransition(EventMapTransition ev, GameEventInfo info) {
        ResetState();
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull ev, GameEventInfo info) {
        if (!isActive || osbase == null) {
            return HookResult.Continue;
        }

        var player = ev.Userid;
        if (!IsEligiblePlayer(player)) {
            return HookResult.Continue;
        }

        ulong steamId = player!.SteamID;
        int generation = stateGeneration;

        osbase.AddTimer(AssignDelay, () => {
            if (!isActive || osbase == null) {
                return;
            }

            if (generation != stateGeneration) {
                return;
            }

            try {
                var livePlayer = FindHumanBySteamId(steamId);
                if (!IsEligiblePlayer(livePlayer)) {
                    return;
                }

                var safePlayer = livePlayer!;

                // Engine already placed the player on a real team. Leave it alone.
                if (IsPlayable(safePlayer.TeamNum)) {
                    var teamNow = (CsTeam)safePlayer.TeamNum;
                    teamGuards[steamId] = (teamNow, DateTime.UtcNow.AddSeconds(GuardSeconds));

                    if (warmupActive && !safePlayer.PawnIsAlive) {
                        ScheduleWarmupRespawn(steamId, generation, WarmupRespawnDelay);
                    }

                    return;
                }

                CsTeam intendedTeam;
                lock (TeamAssignLock) {
                    var all = Utilities.GetPlayers().Where(x => x != null && x.IsValid && !x.IsHLTV && !x.IsBot);
                    int ct = all.Count(x => x.TeamNum == (int)CsTeam.CounterTerrorist);
                    int tt = all.Count(x => x.TeamNum == (int)CsTeam.Terrorist);

                    intendedTeam = DecideTeamForJoin(ct, tt);
                    safePlayer.ChangeTeam(intendedTeam);
                }

                justAutoAssigned[steamId] = intendedTeam;
                teamGuards[steamId] = (intendedTeam, DateTime.UtcNow.AddSeconds(GuardSeconds));

                ScheduleCorrection(steamId, generation);
                if (warmupActive) {
                    ScheduleWarmupRespawn(steamId, generation, WarmupRespawnDelay);
                }
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] assign failed for {steamId}: {ex.Message}");
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    private void ScheduleCorrection(ulong steamId, int generation) {
        if (osbase == null) {
            return;
        }

        osbase.AddTimer(CorrectionDelay, () => {
            if (!isActive) {
                return;
            }

            if (generation != stateGeneration) {
                return;
            }

            try {
                var livePlayer = FindHumanBySteamId(steamId);
                if (!IsEligiblePlayer(livePlayer)) {
                    return;
                }

                var safePlayer = livePlayer!;

                if (!justAutoAssigned.TryGetValue(steamId, out var storedTeam)) {
                    return;
                }

                // Only restore if engine bounced the player back to a non-playable team.
                if (!IsPlayable(safePlayer.TeamNum) && !safePlayer.PawnIsAlive) {
                    safePlayer.ChangeTeam(storedTeam);
                }

                CsTeam finalTeam = IsPlayable(safePlayer.TeamNum) ? (CsTeam)safePlayer.TeamNum : storedTeam;
                string color = finalTeam == CsTeam.CounterTerrorist ? "\x0B" : "\x02";

                safePlayer.PrintToChat($" \x04[AutoAssign]\x01 You were assigned to the {color}{finalTeam}\x01 team.");
                teamGuards[steamId] = (finalTeam, DateTime.UtcNow.AddSeconds(GuardSeconds));

                if (warmupActive && !safePlayer.PawnIsAlive) {
                    ScheduleWarmupRespawn(steamId, generation, 0.15f);
                }
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] correction failed for {steamId}: {ex.Message}");
            } finally {
                justAutoAssigned.Remove(steamId);
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private HookResult OnPlayerTeam(EventPlayerTeam ev, GameEventInfo info) {
        if (!isActive || osbase == null) {
            return HookResult.Continue;
        }

        try {
            var player = ev.Userid;
            if (!IsEligiblePlayer(player)) {
                return HookResult.Continue;
            }

            var safePlayer = player!;
            ulong steamId = safePlayer.SteamID;

            if (!warmupActive) {
                return HookResult.Continue;
            }

            if (!teamGuards.TryGetValue(steamId, out var guard)) {
                return HookResult.Continue;
            }

            if (DateTime.UtcNow > guard.until) {
                teamGuards.Remove(steamId);
                return HookResult.Continue;
            }

            if (IsPlayable(safePlayer.TeamNum)) {
                return HookResult.Continue;
            }

            int generation = stateGeneration;

            // Delay the restore slightly instead of switching inside the event callback.
            osbase.AddTimer(0.05f, () => {
                if (!isActive) {
                    return;
                }

                if (generation != stateGeneration) {
                    return;
                }

                try {
                    var livePlayer = FindHumanBySteamId(steamId);
                    if (!IsEligiblePlayer(livePlayer)) {
                        return;
                    }

                    var p = livePlayer!;
                    if (IsPlayable(p.TeamNum)) {
                        return;
                    }

                    if (!p.PawnIsAlive) {
                        p.ChangeTeam(guard.team);
                        ScheduleWarmupRespawn(steamId, generation, 0.20f);
                    }
                } catch (Exception ex) {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] delayed restore failed for {steamId}: {ex.Message}");
                }
            }, TimerFlags.STOP_ON_MAPCHANGE);
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] OnPlayerTeam failed: {ex.Message}");
        }

        return HookResult.Continue;
    }

    private void ScheduleWarmupRespawn(ulong steamId, int generation, float delay) {
        if (!isActive || !warmupActive || osbase == null) {
            return;
        }

        if (!pendingWarmupRespawns.Add(steamId)) {
            return;
        }

        osbase.AddTimer(delay, () => {
            try {
                if (!isActive || !warmupActive) {
                    return;
                }

                if (generation != stateGeneration) {
                    return;
                }

                var livePlayer = FindHumanBySteamId(steamId);
                if (!IsEligiblePlayer(livePlayer)) {
                    return;
                }

                var player = livePlayer!;
                if (!IsPlayable(player.TeamNum)) {
                    return;
                }

                if (!player.PawnIsAlive) {
                    try {
                        player.Respawn();
                    } catch (Exception ex) {
                        Console.WriteLine($"[ERROR] OSBase[{ModuleName}] warmup respawn failed for {steamId}: {ex.Message}");
                    }
                }
            } finally {
                pendingWarmupRespawns.Remove(steamId);
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static CsTeam DecideTeamForJoin(int ct, int tt) {
        int dCT = Math.Abs((ct + 1) - tt);
        int dT = Math.Abs(ct - (tt + 1));

        if (dCT < dT) {
            return CsTeam.CounterTerrorist;
        }

        if (dT < dCT) {
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
        warmupActive = true;
        stateGeneration++;
        teamGuards.Clear();
        justAutoAssigned.Clear();
        pendingWarmupRespawns.Clear();
    }

    private static bool IsPlayable(int teamNum) {
        return teamNum == (int)CsTeam.Terrorist || teamNum == (int)CsTeam.CounterTerrorist;
    }

    private static bool IsEligiblePlayer(CCSPlayerController? player) {
        return player != null &&
               player.IsValid &&
               !player.IsHLTV &&
               !player.IsBot &&
               player.Connected == PlayerConnectedState.PlayerConnected;
    }
}