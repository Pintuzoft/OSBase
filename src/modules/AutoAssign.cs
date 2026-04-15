using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
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
    private static readonly object TeamAssignLock = new();

    private bool warmupActive = true;

    // Guard recent team to fight engine SPEC fallback
    private readonly Dictionary<ulong, (CsTeam team, DateTime until)> teamGuards = new();

    // Track which team we intentionally assigned.
    // Correction pass may only restore this exact team, never recalculate.
    private readonly Dictionary<ulong, CsTeam> justAutoAssigned = new();

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

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded (delay={AssignDelay}s).");
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
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info) {
        warmupActive = false;
        teamGuards.Clear();
        justAutoAssigned.Clear();
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

        osbase.AddTimer(AssignDelay, () => {
            try {
                if (!isActive) {
                    return;
                }

                if (!IsEligiblePlayer(player)) {
                    return;
                }

                var safePlayer = player!;

                // Engine already placed the player on a real team. Leave it alone.
                if (IsPlayable(safePlayer.TeamNum)) {
                    var teamNow = (CsTeam)safePlayer.TeamNum;
                    teamGuards[safePlayer.SteamID] = (teamNow, DateTime.UtcNow.AddSeconds(1));

                    if (warmupActive && !safePlayer.PawnIsAlive) {
                        TryForceSpawn(safePlayer);
                    }

                    return;
                }

                CsTeam intendedTeam;
                lock (TeamAssignLock) {
                    var all = Utilities.GetPlayers().Where(x => x != null && x.IsValid && !x.IsHLTV && !x.IsBot);
                    int ct = all.Count(x => x.TeamNum == (byte)CsTeam.CounterTerrorist);
                    int tt = all.Count(x => x.TeamNum == (byte)CsTeam.Terrorist);

                    intendedTeam = DecideTeamForJoin(ct, tt);
                    safePlayer.SwitchTeam(intendedTeam);
                }

                justAutoAssigned[safePlayer.SteamID] = intendedTeam;

                osbase.AddTimer(CorrectionDelay, () => {
                    try {
                        if (!isActive) {
                            return;
                        }

                        if (!IsEligiblePlayer(safePlayer)) {
                            return;
                        }

                        if (!justAutoAssigned.TryGetValue(safePlayer.SteamID, out var storedTeam)) {
                            return;
                        }

                        // Only restore if engine bounced the player back to a non-playable team.
                        if (!safePlayer.PawnIsAlive && !IsPlayable(safePlayer.TeamNum)) {
                            safePlayer.SwitchTeam(storedTeam);
                        }

                        CsTeam finalTeam = IsPlayable(safePlayer.TeamNum) ? (CsTeam)safePlayer.TeamNum : storedTeam;
                        string color = finalTeam == CsTeam.CounterTerrorist ? "\x0B" : "\x02";

                        safePlayer.PrintToChat($" \x04[AutoAssign]\x01 You were assigned to the {color}{finalTeam}\x01 team.");
                        teamGuards[safePlayer.SteamID] = (finalTeam, DateTime.UtcNow.AddSeconds(1));

                        if (warmupActive && !safePlayer.PawnIsAlive) {
                            TryForceSpawn(safePlayer);
                        }
                    } catch (Exception ex) {
                        Console.WriteLine($"[ERROR] OSBase[{ModuleName}] correction failed: {ex.Message}");
                    } finally {
                        justAutoAssigned.Remove(safePlayer.SteamID);
                    }
                });
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] assign failed: {ex.Message}");
            }
        });

        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam ev, GameEventInfo info) {
        if (!isActive) {
            return HookResult.Continue;
        }

        try {
            var player = ev.Userid;
            if (!IsEligiblePlayer(player)) {
                return HookResult.Continue;
            }

            var safePlayer = player!;

            if (!warmupActive) {
                return HookResult.Continue;
            }

            if (!teamGuards.TryGetValue(safePlayer.SteamID, out var guard)) {
                return HookResult.Continue;
            }

            if (DateTime.UtcNow > guard.until) {
                teamGuards.Remove(safePlayer.SteamID);
                return HookResult.Continue;
            }

            // If engine bounces them right back to SPEC during warmup, restore intended team.
            if (!IsPlayable(safePlayer.TeamNum) && !safePlayer.PawnIsAlive) {
                safePlayer.SwitchTeam(guard.team);
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] OnPlayerTeam failed: {ex.Message}");
        }

        return HookResult.Continue;
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

    private void TryForceSpawn(CCSPlayerController player) {
        if (!isActive || !warmupActive || osbase == null) {
            return;
        }

        const int tries = 8;
        const float step = 0.20f;

        for (int i = 1; i <= tries; i++) {
            float delay = i * step;

            osbase.AddTimer(delay, () => {
                if (!isActive || !warmupActive) {
                    return;
                }

                if (!IsEligiblePlayer(player)) {
                    return;
                }

                if (!player!.PawnIsAlive) {
                    try {
                        player.Respawn();
                    } catch {
                    }
                }
            });
        }
    }

    private void ResetState() {
        warmupActive = true;
        teamGuards.Clear();
        justAutoAssigned.Clear();
    }

    private static bool IsPlayable(byte teamNum) {
        return teamNum == (byte)CsTeam.Terrorist || teamNum == (byte)CsTeam.CounterTerrorist;
    }

    private static bool IsEligiblePlayer(CCSPlayerController? player) {
        return player != null && player.IsValid && !player.IsHLTV && !player.IsBot;
    }
}