using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules;

public class AutoAssign : ModuleBase {
    public override string ModuleName => "autoassign";
    protected override string DefaultEnabled => "0";

    private const float AssignDelay = 1.00f;
    private const float GuardSeconds = 2.50f;

    private int stateGeneration = 0;

    private readonly HashSet<ulong> pendingAssignments = new();

    // Steam IDs awaiting a post-spawn position check (see OnPlayerSpawn). ChangeTeam's own
    // join/spawn ceremony can drop a brand-new connect under the map during warmup -- known
    // CS2/CounterStrikeSharp issue (MatchZy hit the same thing and just removed auto-join).
    // We keep ChangeTeam (it's the one that reliably sticks the player on the team -- the old
    // SwitchTeam-based version needed a whole guard system to fight the engine bouncing
    // players back to spectator) and instead correct the landing spot on first spawn.
    private readonly HashSet<ulong> pendingSpawnFix = new();

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
        osbase?.SubscribeToEvent<EventPlayerSpawn>(OnPlayerSpawn);
        osbase?.SubscribeToEvent<EventMapTransition>(OnMapTransition);
        osbase?.RegisterListener<Listeners.OnMapStart>(OnMapStart);
    }

    protected override void UnregisterHandlers() {
        // Use new EventBus system
        osbase?.UnsubscribeFromEvent<EventPlayerConnectFull>(OnPlayerConnectFull);
        osbase?.UnsubscribeFromEvent<EventPlayerSpawn>(OnPlayerSpawn);
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

            // Already on a real team. AutoAssign is done and must not fight TeamBalancer.
            if (IsPlayable(safePlayer.TeamNum)) {
                pendingAssignments.Remove(steamId);
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
            pendingSpawnFix.Add(steamId);

            safePlayer.ChangeTeam(intendedTeam);
        } catch (Exception ex) {
            pendingAssignments.Remove(steamId);
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] TryAssignConnectedPlayer failed for {steamId}: {ex.Message}");
        }
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn ev) {
        if (!isActive || osbase == null) {
            return HookResult.Continue;
        }

        var player = ev.Userid;
        if (!IsEligiblePlayer(player)) {
            return HookResult.Continue;
        }

        ulong steamId = player!.SteamID;
        if (!pendingSpawnFix.Remove(steamId)) {
            return HookResult.Continue;
        }

        try {
            if (!IsPlayable(player.TeamNum)) {
                return HookResult.Continue;
            }

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid) {
                return HookResult.Continue;
            }

            var spawnPoint = PickSpawnPointForTeam((CsTeam)player.TeamNum);
            if (spawnPoint == null) {
                return HookResult.Continue;
            }

            pawn.Teleport(spawnPoint.AbsOrigin, spawnPoint.AbsRotation, new Vector(0f, 0f, 0f));
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] corrected spawn position for steamid={steamId}.");
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] spawn fix failed for {steamId}: {ex.Message}");
        }

        return HookResult.Continue;
    }

    // ChangeTeam's own join ceremony can land a brand-new connect off any real spawn point
    // during warmup (see pendingSpawnFix comment). Correct it onto one of the map's actual
    // spawn entities instead of guessing a "too low" threshold, which wouldn't generalize
    // across maps.
    private SpawnPoint? PickSpawnPointForTeam(CsTeam team) {
        var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        if (gameRules == null) {
            return null;
        }

        var handles = team == CsTeam.CounterTerrorist ? gameRules.CTSpawnPoints : gameRules.TerroristSpawnPoints;
        var points = handles.Where(h => h.IsValid).Select(h => h.Value).Where(sp => sp != null && sp.IsValid).ToList();
        if (points.Count == 0) {
            return null;
        }

        return points[Random.Shared.Next(points.Count)];
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
        pendingAssignments.Clear();
        pendingSpawnFix.Clear();
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