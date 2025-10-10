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
                        if (player == null || !player.IsValid || player.IsHLTV || player.IsBot) return;

                        // If already on a team, allow one pre-spawn rebalance
                        bool alreadyAssigned = IsOnPlayableTeam(player.TeamNum);

                        Func<(int ct,int tt)> countNow = () => {
                            var all = Utilities.GetPlayers().Where(p => p != null && p.IsValid && !p.IsHLTV);
                            return (all.Count(p => p.TeamNum == (byte)CsTeam.CounterTerrorist),
                                    all.Count(p => p.TeamNum == (byte)CsTeam.Terrorist));
                        };

                        (int ct, int tt) = countNow();

                        CsTeam DecideTarget(byte currentTeam, int ct, int tt, bool treatAsOnTeam) {
                            // if engine already put them on a team, simulate moving them to the other side
                            if (treatAsOnTeam) {
                                bool isCT = currentTeam == (byte)CsTeam.CounterTerrorist;
                                int stay = Math.Abs(ct - tt);
                                int swch = isCT ? Math.Abs((ct - 1) - (tt + 1)) : Math.Abs((ct + 1) - (tt - 1));
                                if (swch < stay) return isCT ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
                                return isCT ? CsTeam.CounterTerrorist : CsTeam.Terrorist; // “no change” marker
                            } else {
                                int diffIfCT = Math.Abs((ct + 1) - tt);
                                int diffIfT  = Math.Abs(ct - (tt + 1));
                                if (diffIfCT < diffIfT) return CsTeam.CounterTerrorist;
                                if (diffIfT  < diffIfCT) return CsTeam.Terrorist;
                                return (Random.Shared.Next(2) == 0) ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
                            }
                        }

                        // Decide (pre-switch) with fresh counts
                        var target = DecideTarget(player.TeamNum, ct, tt, alreadyAssigned);
                        bool needSwitch =
                            !alreadyAssigned ||                     // unassigned -> need a team
                            (!player.PawnIsAlive &&                 // assigned but pre-spawn -> may rebalance
                            ((player.TeamNum == (byte)CsTeam.CounterTerrorist && target == CsTeam.Terrorist) ||
                            (player.TeamNum == (byte)CsTeam.Terrorist && target == CsTeam.CounterTerrorist)));

                        if (needSwitch) {
                            player.SwitchTeam(target);
                            string color = (target == CsTeam.CounterTerrorist) ? "\x0B" : "\x02";
                            player.PrintToChat($" \x04[AutoAssign]\x01 You were assigned to the {color}{target}\x01 team.");
                        }

                        // Post-switch verify once after 50 ms (only while pre-spawn)
                        osbase!.AddTimer(0.05f, () => {
                            if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return;
                            if (player.PawnIsAlive) return; // spawned -> leave it

                            (int ct2, int tt2) = countNow();
                            // simulate: if flipping again would strictly improve balance, do it once
                            bool isCT = player.TeamNum == (byte)CsTeam.CounterTerrorist;
                            int stay = Math.Abs(ct2 - tt2);
                            int flip = isCT ? Math.Abs((ct2 - 1) - (tt2 + 1)) : Math.Abs((ct2 + 1) - (tt2 - 1));
                            if (flip < stay) {
                                var other = isCT ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
                                player.SwitchTeam(other);
                                string color2 = (other == CsTeam.CounterTerrorist) ? "\x0B" : "\x02";
                                player.PrintToChat($" \x04[AutoAssign]\x01 You were assigned to the {color2}{other}\x01 team.");
                            } else if (!player.PawnIsAlive) {
                                // still not alive? nudge spawn (warmup edge)
                                TryForceSpawn(player);
                            }
                        });
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