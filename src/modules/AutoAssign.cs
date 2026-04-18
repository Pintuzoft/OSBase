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

    private const float AssignDelay = 0.50f;
    private const float RetryDelay = 0.40f;
    private const float GuardSeconds = 2.50f;
    private const float AutoAssignCloseAt = 55.0f;

    private int stateGeneration = 0;
    private bool warmupActive = false;
    private bool autoAssignOpen = false;

    // Prevent duplicate join flows for the same player.
    private readonly HashSet<ulong> pendingAssignments = new();

    // Lets TeamBalancer skip freshly auto-assigned players if you want.
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

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded (assign={AssignDelay:0.00}s retry={RetryDelay:0.00}s close={AutoAssignCloseAt:0.00}s).");
    }

    public void Unload() {
        isActive = false;
        ResetState();

        if (osbase != null && handlersLoaded) {
            osbase.DeregisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
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
        stateGeneration++;
        warmupActive = true;
        autoAssignOpen = true;
        pendingAssignments.Clear();
        recentAutoAssign.Clear();

        int generation = stateGeneration;

        osbase?.AddTimer(AutoAssignCloseAt, () => {
            if (!isActive || generation != stateGeneration) {
                return;
            }

            autoAssignOpen = false;
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] autoassign closed for warmup.");
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info) {
        stateGeneration++;
        warmupActive = false;
        autoAssignOpen = false;
        pendingAssignments.Clear();
        recentAutoAssign.Clear();
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
        if (!IsEligiblePlayer(player)) {
            return HookResult.Continue;
        }

        ulong steamId = player!.SteamID;
        int generation = stateGeneration;

        if (!pendingAssignments.Add(steamId)) {
            return HookResult.Continue;
        }

        osbase.AddTimer(AssignDelay, () => {
            TryAssignPlayer(steamId, generation, false);
        }, TimerFlags.STOP_ON_MAPCHANGE);

        osbase.AddTimer(AssignDelay + RetryDelay, () => {
            TryAssignPlayer(steamId, generation, true);
        }, TimerFlags.STOP_ON_MAPCHANGE);

        osbase.AddTimer(AssignDelay + RetryDelay + 0.25f, () => {
            if (generation != stateGeneration) {
                return;
            }

            pendingAssignments.Remove(steamId);
            CleanupExpiredGuard(steamId);
            AnnounceFinalTeam(steamId, generation);
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    private void TryAssignPlayer(ulong steamId, int generation, bool retry) {
        if (!isActive || osbase == null || !warmupActive || !autoAssignOpen) {
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

            var player = livePlayer!;

            // If engine or another module already put them on a real team, stop.
            if (IsPlayable(player.TeamNum)) {
                return;
            }

            int ct = 0;
            int tt = 0;

            foreach (var p in Utilities.GetPlayers()) {
                if (!IsEligiblePlayer(p)) {
                    continue;
                }

                if (p!.TeamNum == (int)CsTeam.CounterTerrorist) {
                    ct++;
                } else if (p.TeamNum == (int)CsTeam.Terrorist) {
                    tt++;
                }
            }

            var intendedTeam = DecideTeamForJoin(ct, tt);
            recentAutoAssign[steamId] = DateTime.UtcNow.AddSeconds(GuardSeconds);

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {(retry ? "retry" : "assign")} steamid={steamId} ct={ct} t={tt} -> {intendedTeam}");
            player.ChangeTeam(intendedTeam);
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] TryAssignPlayer failed for {steamId}: {ex.Message}");
        }
    }

    private void AnnounceFinalTeam(ulong steamId, int generation) {
        if (!isActive) {
            return;
        }

        if (generation != stateGeneration) {
            return;
        }

        try {
            var player = FindHumanBySteamId(steamId);
            if (!IsEligiblePlayer(player)) {
                return;
            }

            var safePlayer = player!;
            if (!IsPlayable(safePlayer.TeamNum)) {
                return;
            }

            var finalTeam = (CsTeam)safePlayer.TeamNum;
            string color = finalTeam == CsTeam.CounterTerrorist ? "\x0B" : "\x02";

            safePlayer.PrintToChat($" \x04[AutoAssign]\x01 You were assigned to the {color}{finalTeam}\x01 team.");
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] AnnounceFinalTeam failed for {steamId}: {ex.Message}");
        }
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
        recentAutoAssign.Clear();
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