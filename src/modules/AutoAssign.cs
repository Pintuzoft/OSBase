using System;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules
{
    public class AutoAssign : IModule
    {
        public string ModuleName => "autoassign";
        private OSBase? osbase;

        private const float AssignDelay = 0.25f; // short safety delay

        public void Load(OSBase inOsbase, Config inConfig)
        {
            osbase = inOsbase;

            // default disabled
            inConfig.RegisterGlobalConfigValue($"{ModuleName}", "0");

            if (inConfig.GetGlobalConfigValue($"{ModuleName}", "0") != "1")
            {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in global config.");
                return;
            }

            osbase.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded (delay={AssignDelay}s, ignores bots/HLTV).");
        }

        private HookResult OnPlayerConnectFull(EventPlayerConnectFull ev, GameEventInfo info)
        {
            var player = ev.Userid;
            if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
                return HookResult.Continue;

            osbase!.AddTimer(AssignDelay, () =>
            {
                try
                {
                    if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
                        return;

                    var humans = Utilities.GetPlayers()
                        .Where(p => p != null && p.IsValid && !p.IsBot && !p.IsHLTV);

                    int ctCount = humans.Count(p => p.TeamNum == (byte)CsTeam.CounterTerrorist);
                    int tCount  = humans.Count(p => p.TeamNum == (byte)CsTeam.Terrorist);

                    CsTeam target = (ctCount < tCount) ? CsTeam.CounterTerrorist
                                   : (tCount < ctCount) ? CsTeam.Terrorist
                                   : (Random.Shared.Next(2) == 0 ? CsTeam.CounterTerrorist : CsTeam.Terrorist);

                    if (player.TeamNum == (byte)target)
                        return;

                    player.SwitchTeam(target);

                    // Colorize per team
                    string teamColor = target == CsTeam.CounterTerrorist ? "\x0B" : "\x02"; // blue / red
                    player.PrintToChat($" \x04[AutoAssign]\x01 You were assigned to the {teamColor}{target}\x01 team.");

                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] assigned '{player.PlayerName ?? "Unknown"}' to {target} (CT={ctCount}, T={tCount}).");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] team switch failed: {ex.Message}");
                }
            });

            return HookResult.Continue;
        }
    }
}