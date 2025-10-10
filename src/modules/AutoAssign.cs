using System;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules {
    public class AutoAssign : IModule {
        public string ModuleName => "autoassign";
        private OSBase? osbase;

        private const float AssignDelay = 0.20f;
        private static readonly object TeamAssignLock = new();

        public void Load(OSBase inOsbase, Config inConfig) {
            osbase = inOsbase;

            // default OFF
            inConfig.RegisterGlobalConfigValue($"{ModuleName}", "0");
            if (inConfig.GetGlobalConfigValue($"{ModuleName}", "0") != "1") {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in global config.");
                return;
            }

            osbase.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded (delay={AssignDelay}s).");
        }

        private static bool IsOnPlayableTeam(byte teamNum) {
            return teamNum == (byte)CsTeam.Terrorist || teamNum == (byte)CsTeam.CounterTerrorist;
        }

        private HookResult OnPlayerConnectFull(EventPlayerConnectFull ev, GameEventInfo info) {
            var player = ev.Userid;
            if (player == null || !player.IsValid || player.IsHLTV || player.IsBot)
                return HookResult.Continue;

            osbase!.AddTimer(AssignDelay, () => {
                try {
                    if (player == null || !player.IsValid || player.IsHLTV || player.IsBot)
                        return;

                    // If engine already assigned, skip (avoid wrong spawn)
                    if (IsOnPlayableTeam(player.TeamNum)) {
                        if (!player.PawnIsAlive)
                            TryForceSpawn(player);
                        return;
                    }

                    lock (TeamAssignLock) {
                        if (player == null || !player.IsValid || player.IsHLTV || player.IsBot)
                            return;

                        if (IsOnPlayableTeam(player.TeamNum)) {
                            if (!player.PawnIsAlive)
                                TryForceSpawn(player);
                            return;
                        }

                        var all = Utilities.GetPlayers()
                            .Where(p => p != null && p.IsValid && !p.IsHLTV);

                        int ct = all.Count(p => p.TeamNum == (byte)CsTeam.CounterTerrorist);
                        int tt = all.Count(p => p.TeamNum == (byte)CsTeam.Terrorist);

                        int diffIfCT = Math.Abs((ct + 1) - tt);
                        int diffIfT = Math.Abs(ct - (tt + 1));
                        CsTeam target = diffIfCT < diffIfT
                            ? CsTeam.CounterTerrorist
                            : diffIfT < diffIfCT
                                ? CsTeam.Terrorist
                                : (Random.Shared.Next(2) == 0 ? CsTeam.CounterTerrorist : CsTeam.Terrorist);

                        player.SwitchTeam(target);

                        string teamColor = target == CsTeam.CounterTerrorist ? "\x0B" : "\x02";
                        player.PrintToChat($" \x04[AutoAssign]\x01 You were assigned to the {teamColor}{target}\x01 team.");

                        if (!player.PawnIsAlive)
                            TryForceSpawn(player);

                        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] assigned '{player.PlayerName ?? "Unknown"}' to {target} (CT={ct}, T={tt} -> {target}).");
                    }
                } catch (Exception ex) {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] team assign failed: {ex.Message}");
                }
            });

            return HookResult.Continue;
        }

        private void TryForceSpawn(CCSPlayerController player) {
            const int maxTries = 6;
            const float step = 0.20f;

            for (int i = 1; i <= maxTries; i++) {
                float delay = i * step;
                osbase!.AddTimer(delay, () => {
                    if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
                        return;

                    if (!player.PawnIsAlive) {
                        try {
                            player.Respawn();
                        } catch {
                            // ignore
                        }
                    }
                });
            }
        }
    }
}