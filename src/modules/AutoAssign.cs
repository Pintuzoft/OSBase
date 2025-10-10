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

        private const float AssignDelay = 0.05f;       // small delay for stability
        private const float CorrectionDelay = 0.12f;   // short correction window
        private static readonly object TeamAssignLock = new();

        // Tracks if warmup is active
        private bool _warmupActive = true;

        public void Load(OSBase inOsbase, Config inConfig) {
            osbase = inOsbase;

            // Default OFF in global config
            inConfig.RegisterGlobalConfigValue($"{ModuleName}", "0");
            if (inConfig.GetGlobalConfigValue($"{ModuleName}", "0") != "1") {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in global config.");
                return;
            }

            // Main handler
            osbase.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);

            // Track warmup state
            osbase.RegisterEventHandler<EventRoundAnnounceWarmup>((ev, info) => {
                _warmupActive = true;
                return HookResult.Continue;
            });

            osbase.RegisterEventHandler<EventRoundStart>((ev, info) => {
                _warmupActive = false;
                return HookResult.Continue;
            });

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded (delay={AssignDelay}s).");
        }

        private static bool Playable(byte t) {
            return t == (byte)CsTeam.Terrorist || t == (byte)CsTeam.CounterTerrorist;
        }

        private HookResult OnPlayerConnectFull(EventPlayerConnectFull ev, GameEventInfo info) {
            var p = ev.Userid;
            if (p == null || !p.IsValid || p.IsHLTV || p.IsBot)
                return HookResult.Continue;

            osbase!.AddTimer(AssignDelay, () => {
                try {
                    if (p == null || !p.IsValid || p.IsHLTV || p.IsBot)
                        return;

                    CsTeam finalTeam;

                    lock (TeamAssignLock) {
                        if (p == null || !p.IsValid || p.IsHLTV || p.IsBot)
                            return;

                        // Count all players (include bots for balance)
                        var all = Utilities.GetPlayers().Where(x => x != null && x.IsValid && !x.IsHLTV);
                        int ct = all.Count(x => x.TeamNum == (byte)CsTeam.CounterTerrorist);
                        int tt = all.Count(x => x.TeamNum == (byte)CsTeam.Terrorist);

                        finalTeam = DecideTeam(p.TeamNum, ct, tt);

                        if ((byte)finalTeam != p.TeamNum)
                            p.SwitchTeam(finalTeam);
                    }

                    // Small correction pass (pre-spawn only)
                    osbase!.AddTimer(CorrectionDelay, () => {
                        try {
                            if (p == null || !p.IsValid || p.IsHLTV || p.IsBot)
                                return;

                            if (!p.PawnIsAlive) {
                                lock (TeamAssignLock) {
                                    if (p == null || !p.IsValid || p.IsHLTV || p.IsBot)
                                        return;

                                    var all2 = Utilities.GetPlayers().Where(x => x != null && x.IsValid && !x.IsHLTV);
                                    int ct2 = all2.Count(x => x.TeamNum == (byte)CsTeam.CounterTerrorist);
                                    int tt2 = all2.Count(x => x.TeamNum == (byte)CsTeam.Terrorist);

                                    var corrected = DecideTeam(p.TeamNum, ct2, tt2);
                                    if ((byte)corrected != p.TeamNum)
                                        p.SwitchTeam(corrected);
                                }
                            }

                            // Print only once after correction window
                            var teamNow = (CsTeam)p.TeamNum;
                            string color = teamNow == CsTeam.CounterTerrorist ? "\x0B" : "\x02";
                            p.PrintToChat($" \x04[AutoAssign]\x01 You were assigned to the {color}{teamNow}\x01 team.");

                            // Only force-spawn during warmup
                            if (_warmupActive && !p.PawnIsAlive)
                                TryForceSpawn(p);
                        } catch { /* ignore */ }
                    });
                } catch (Exception ex) {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] assign failed: {ex.Message}");
                }
            });

            return HookResult.Continue;
        }

        private static CsTeam DecideTeam(byte current, int ct, int tt) {
            bool onTeam = Playable(current);

            if (!onTeam) {
                int dCT = Math.Abs((ct + 1) - tt);
                int dT = Math.Abs(ct - (tt + 1));
                if (dCT < dT) return CsTeam.CounterTerrorist;
                if (dT < dCT) return CsTeam.Terrorist;
                return (Random.Shared.Next(2) == 0)
                    ? CsTeam.CounterTerrorist
                    : CsTeam.Terrorist;
            } else {
                bool isCT = current == (byte)CsTeam.CounterTerrorist;
                int stay = Math.Abs(ct - tt);
                int swch = isCT
                    ? Math.Abs((ct - 1) - (tt + 1))
                    : Math.Abs((ct + 1) - (tt - 1));

                if (swch < stay)
                    return isCT ? CsTeam.Terrorist : CsTeam.CounterTerrorist;

                return (CsTeam)current;
            }
        }

        private void TryForceSpawn(CCSPlayerController player) {
            if (!_warmupActive)
                return;

            const int tries = 8;
            const float step = 0.20f;

            for (int i = 1; i <= tries; i++) {
                float d = i * step;
                osbase!.AddTimer(d, () => {
                    if (!_warmupActive)
                        return;

                    if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
                        return;

                    if (!player.PawnIsAlive) {
                        try { player.Respawn(); } catch { /* ignore */ }
                    }
                });
            }
        }
    }
}