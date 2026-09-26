using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules;

// Team moves use SwitchTeam, not ChangeTeam. ChangeTeam's own join/spawn ceremony can drop a
// brand-new connect under the map during warmup (v0.0.481-0.0.554; MatchZy hit the same thing).
// SwitchTeam avoids that but the engine sometimes bounces the player straight back to
// spectator/unassigned, so the guard + correction pass below (restored from v0.0.480) re-applies
// the intended team during warmup.
public class AutoAssign : ModuleBase {
    public override string ModuleName => "autoassign";
    protected override string DefaultEnabled => "0";

    private const float AssignDelay = 1.00f;
    private const float CorrectionDelay = 0.25f;
    private const float BounceRestoreDelay = 0.05f;
    private const float GuardSeconds = 1.0f;

    private int stateGeneration = 0;
    private bool warmupActive = true;

    private readonly HashSet<ulong> pendingAssignments = new();

    // Intended team per player, re-applied if the engine bounces them off it within GuardSeconds.
    private readonly Dictionary<ulong, (CsTeam team, DateTime until)> teamGuards = new();

    // Team we assigned; the correction pass may only restore this exact team, never recalculate.
    private readonly Dictionary<ulong, CsTeam> justAutoAssigned = new();

    // Lets TeamBalancer skip freshly auto-assigned players if it wants to.
    private readonly Dictionary<ulong, DateTime> recentAutoAssign = new();

    protected override void OnLoad() {
        ResetState();
    }

    protected override void OnUnload() {
        ResetState();
    }

    protected override void RegisterHandlers() {
        // Use new EventBus system
        osbase?.SubscribeToEvent<EventPlayerConnectFull>(OnPlayerConnectFull);
        osbase?.SubscribeToEvent<EventPlayerTeam>(OnPlayerTeam);
        osbase?.SubscribeToEvent<EventRoundAnnounceWarmup>(OnRoundAnnounceWarmup);
        osbase?.SubscribeToEvent<EventWarmupEnd>(OnWarmupEnd);
        osbase?.SubscribeToEvent<EventMapTransition>(OnMapTransition);
        osbase?.RegisterListener<Listeners.OnMapStart>(OnMapStart);
    }

    protected override void UnregisterHandlers() {
        // Use new EventBus system
        osbase?.UnsubscribeFromEvent<EventPlayerConnectFull>(OnPlayerConnectFull);
        osbase?.UnsubscribeFromEvent<EventPlayerTeam>(OnPlayerTeam);
        osbase?.UnsubscribeFromEvent<EventRoundAnnounceWarmup>(OnRoundAnnounceWarmup);
        osbase?.UnsubscribeFromEvent<EventWarmupEnd>(OnWarmupEnd);
        osbase?.UnsubscribeFromEvent<EventMapTransition>(OnMapTransition);
        osbase?.RemoveListener<Listeners.OnMapStart>(OnMapStart);
    }

    private void OnMapStart(string mapName) {
        ResetState();
    }

    private HookResult OnMapTransition(EventMapTransition ev) {
        ResetState();
        return HookResult.Continue;
    }

    private HookResult OnRoundAnnounceWarmup(EventRoundAnnounceWarmup ev) {
        warmupActive = true;
        stateGeneration++;
        teamGuards.Clear();
        justAutoAssigned.Clear();
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] warmup started.");
        return HookResult.Continue;
    }

    private HookResult OnWarmupEnd(EventWarmupEnd ev) {
        warmupActive = false;
        stateGeneration++;
        teamGuards.Clear();
        justAutoAssigned.Clear();
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] warmup ended.");
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull ev) {
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

            // Engine already placed the player on a real team. Leave it alone, just guard it.
            if (IsPlayable(safePlayer.TeamNum)) {
                pendingAssignments.Remove(steamId);
                teamGuards[steamId] = ((CsTeam)safePlayer.TeamNum, DateTime.UtcNow.AddSeconds(GuardSeconds));
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

            safePlayer.SwitchTeam(intendedTeam);

            justAutoAssigned[steamId] = intendedTeam;
            teamGuards[steamId] = (intendedTeam, DateTime.UtcNow.AddSeconds(GuardSeconds));

            ScheduleCorrection(steamId, generation);
        } catch (Exception ex) {
            pendingAssignments.Remove(steamId);
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] TryAssignConnectedPlayer failed for {steamId}: {ex.Message}");
        }
    }

    private void ScheduleCorrection(ulong steamId, int generation) {
        if (osbase == null) {
            return;
        }

        osbase.AddTimer(CorrectionDelay, () => {
            if (!isActive || generation != stateGeneration) {
                justAutoAssigned.Remove(steamId);
                return;
            }

            try {
                var player = FindHumanBySteamId(steamId);
                if (!IsEligiblePlayer(player)) {
                    return;
                }

                var safePlayer = player!;

                if (!justAutoAssigned.TryGetValue(steamId, out var storedTeam)) {
                    return;
                }

                // Only restore if the engine bounced the player back to a non-playable team.
                if (!IsPlayable(safePlayer.TeamNum) && !safePlayer.PawnIsAlive) {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] correction: steamid={steamId} bounced to team={safePlayer.TeamNum}, restoring {storedTeam}.");
                    safePlayer.SwitchTeam(storedTeam);
                }

                CsTeam finalTeam = IsPlayable(safePlayer.TeamNum) ? (CsTeam)safePlayer.TeamNum : storedTeam;
                string color = finalTeam == CsTeam.CounterTerrorist ? "\x0B" : "\x02";

                safePlayer.PrintToChat($" \x04[AutoAssign]\x01 You were assigned to the {color}{finalTeam}\x01 team.");
                teamGuards[steamId] = (finalTeam, DateTime.UtcNow.AddSeconds(GuardSeconds));
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] correction failed for {steamId}: {ex.Message}");
            } finally {
                justAutoAssigned.Remove(steamId);
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private HookResult OnPlayerTeam(EventPlayerTeam ev) {
        if (!isActive || osbase == null || !warmupActive) {
            return HookResult.Continue;
        }

        try {
            var player = ev.Userid;
            if (!IsEligiblePlayer(player)) {
                return HookResult.Continue;
            }

            var safePlayer = player!;
            ulong steamId = safePlayer.SteamID;

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

            // Restore on a short delay instead of switching inside the event callback.
            osbase.AddTimer(BounceRestoreDelay, () => {
                if (!isActive || generation != stateGeneration) {
                    return;
                }

                try {
                    var livePlayer = FindHumanBySteamId(steamId);
                    if (!IsEligiblePlayer(livePlayer)) {
                        return;
                    }

                    var p = livePlayer!;
                    if (IsPlayable(p.TeamNum) || p.PawnIsAlive) {
                        return;
                    }

                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] bounce restore: steamid={steamId} team={p.TeamNum} -> {guard.team}.");
                    p.SwitchTeam(guard.team);
                } catch (Exception ex) {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] bounce restore failed for {steamId}: {ex.Message}");
                }
            }, TimerFlags.STOP_ON_MAPCHANGE);
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] OnPlayerTeam failed: {ex.Message}");
        }

        return HookResult.Continue;
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
        warmupActive = true;
        pendingAssignments.Clear();
        teamGuards.Clear();
        justAutoAssigned.Clear();
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
