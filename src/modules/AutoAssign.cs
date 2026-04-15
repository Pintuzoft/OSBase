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

    // Track who we actually auto-assigned from SPEC/UNASSIGNED.
    // Only these are eligible for the post-assign correction pass.
    private readonly HashSet<ulong> justAutoAssigned = new();

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
        warmupActive = true;
        teamGuards.Clear();
        justAutoAssigned.Clear();
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
        warmupActive = true;
        teamGuards.Clear();
        justAutoAssigned.Clear();
        return HookResult.Continue;
    }

    private static bool IsPlayable(byte teamNum) {
        return teamNum == (byte)CsTeam.Terrorist || teamNum == (byte)CsTeam.CounterTerrorist;
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull ev, GameEventInfo info) {
        if (!isActive || osbase == null) {
            return HookResult.Continue;
        }

        var player = ev.Userid;
        if (player == null || !player.IsValid || player.IsHLTV || player.IsBot) {
            return HookResult.Continue;
        }

        osbase.AddTimer(AssignDelay, () => {
            try {
                if (!isActive) {
                    return;
                }

                if (player == null || !player.IsValid || player.IsHLTV || player.IsBot) {
                    return;
                }

                // If the engine already placed them on a playable team, DO NOT MOVE THEM.
                // This avoids cross-team spawns caused by late swaps.
                if (IsPlayable(player.TeamNum)) {
                    var teamNow = (CsTeam)player.TeamNum;
                    teamGuards[player.SteamID] = (teamNow, DateTime.UtcNow.AddSeconds(1));

                    // Warmup-only respawn if they happen to be dead for some reason
                    if (warmupActive && !player.PawnIsAlive) {
                        TryForceSpawn(player);
                    }

                    return;
                }

                // Only assign if currently non-playable (Unassigned/Spectator)
                CsTeam finalTeam;
                lock (TeamAssignLock) {
                    var all = Utilities.GetPlayers().Where(x => x != null && x.IsValid && !x.IsHLTV);
                    int ct = all.Count(x => x.TeamNum == (byte)CsTeam.CounterTerrorist);
                    int tt = all.Count(x => x.TeamNum == (byte)CsTeam.Terrorist);

                    finalTeam = DecideTeamForJoin(ct, tt);
                    player.SwitchTeam(finalTeam);
                }

                // Mark that we auto-assigned this player from non-playable → eligible for correction
                justAutoAssigned.Add(player.SteamID);

                // Correction pass ONLY for those we just assigned
                osbase.AddTimer(CorrectionDelay, () => {
                    try {
                        if (!isActive) {
                            return;
                        }

                        if (player == null || !player.IsValid || player.IsHLTV || player.IsBot) {
                            return;
                        }

                        // If they already spawned, do NOT switch anymore.
                        if (!player.PawnIsAlive && justAutoAssigned.Contains(player.SteamID)) {
                            lock (TeamAssignLock) {
                                var all2 = Utilities.GetPlayers().Where(x => x != null && x.IsValid && !x.IsHLTV);
                                int ct2 = all2.Count(x => x.TeamNum == (byte)CsTeam.CounterTerrorist);
                                int tt2 = all2.Count(x => x.TeamNum == (byte)CsTeam.Terrorist);

                                var corrected = DecideTeamForJoin(ct2, tt2);
                                if ((byte)corrected != player.TeamNum) {
                                    player.SwitchTeam(corrected);
                                }
                            }
                        }

                        var teamNow = (CsTeam)player.TeamNum;
                        string color = teamNow == CsTeam.CounterTerrorist ? "\x0B" : "\x02";
                        player.PrintToChat($" \x04[AutoAssign]\x01 You were assigned to the {color}{teamNow}\x01 team.");

                        teamGuards[player.SteamID] = (teamNow, DateTime.UtcNow.AddSeconds(1));

                        if (warmupActive && !player.PawnIsAlive) {
                            TryForceSpawn(player);
                        }
                    } catch {
                    } finally {
                        justAutoAssigned.Remove(player.SteamID);
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
            if (player == null || !player.IsValid || player.IsHLTV || player.IsBot) {
                return HookResult.Continue;
            }

            if (!warmupActive) {
                return HookResult.Continue;
            }

            if (!teamGuards.TryGetValue(player.SteamID, out var guard)) {
                return HookResult.Continue;
            }

            if (DateTime.UtcNow > guard.until) {
                teamGuards.Remove(player.SteamID);
                return HookResult.Continue;
            }

            // If engine bounces them to SPEC right after join, push them back
            if (player.TeamNum == (byte)CsTeam.Spectator && !player.PawnIsAlive) {
                player.SwitchTeam(guard.team);
            }
        } catch {
        }

        return HookResult.Continue;
    }

    // New: joining decision ONLY
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

                if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) {
                    return;
                }

                if (!player.PawnIsAlive) {
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
}