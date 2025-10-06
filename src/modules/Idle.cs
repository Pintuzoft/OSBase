using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules;

public class Idle : IModule {
    public string ModuleName => "idle";
    private OSBase? osbase;
    private Config? config;
    private readonly Dictionary<uint, PlayerData> tracked = new();

    private float checkInterval = 10f;
    private float moveThreshold = 5f;
    private int warnAfter = 3;
    private int moveAfter = 6;
    private CCSGameRules? rules;

    private class PlayerData { public Vector? Origin; public int StillCount; }

    public void Load(OSBase os, Config cfg) {
        osbase = os; config = cfg;
        cfg.RegisterGlobalConfigValue(ModuleName, "1");

        if (cfg.GetGlobalConfigValue(ModuleName, "0") != "1") {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled."); return;
        }

        // create config file if missing
        cfg.CreateCustomConfig($"{ModuleName}.cfg",
            "// Idle configuration\n" +
            "check_interval=10\nmove_threshold=5\nwarn_after=3\nmove_after=6\n");

        // parse key/value pairs manually
        foreach (var line in cfg.FetchCustomConfig($"{ModuleName}.cfg")) {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) continue;
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;

            switch (parts[0].ToLower()) {
                case "check_interval": float.TryParse(parts[1], out checkInterval); break;
                case "move_threshold": float.TryParse(parts[1], out moveThreshold); break;
                case "warn_after": int.TryParse(parts[1], out warnAfter); break;
                case "move_after": int.TryParse(parts[1], out moveAfter); break;
            }
        }

        os.RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        os.RegisterEventHandler<EventMapTransition>((_, __) => { tracked.Clear(); return HookResult.Continue; });
        os.RegisterEventHandler<EventRoundFreezeEnd>((_, __) => { rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules; return HookResult.Continue; });
        os.AddTimer(checkInterval, CheckPlayers); // removed bool flag

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded (interval={checkInterval}s).");
    }

    private HookResult OnSpawn(EventPlayerSpawn e, GameEventInfo _) {
        var p = e.Userid;
        if (p == null || !p.IsValid || p.IsBot) return HookResult.Continue;
        tracked[p.Index] = new PlayerData { Origin = p.PlayerPawn?.Value?.CBodyComponent?.SceneNode?.AbsOrigin };
        return HookResult.Continue;
    }

    private void CheckPlayers() {
        if (osbase == null || (rules != null && (rules.FreezePeriod || rules.WarmupPeriod))) return;
        if (tracked.Count == 0) return;

        var forget = new List<uint>();
        foreach (var p in Utilities.GetPlayers()) {
            if (p == null || !p.IsValid || p.IsBot || p.TeamNum < 2) continue;
            if (!tracked.TryGetValue(p.Index, out var d)) continue;

            var o = p.PlayerPawn?.Value?.CBodyComponent?.SceneNode?.AbsOrigin;
            if (o == null) continue;

            if (d.Origin != null && Distance(d.Origin, o) < moveThreshold) {
                d.StillCount++;
                if (d.StillCount == warnAfter)
                    p.PrintToChat($"{ChatColors.Red}[AFK] Move now or you'll be moved soon!");
                else if (d.StillCount >= moveAfter) {
                    Server.PrintToChatAll($"{ChatColors.Grey}[AFK] {ChatColors.Red}{p.PlayerName}{ChatColors.Grey} moved to spectators.");
                    p.ChangeTeam(CsTeam.Spectator);
                    forget.Add(p.Index);
                }
            } else forget.Add(p.Index); // moved
        }
        foreach (var id in forget) tracked.Remove(id);
    }

    private static float Distance(Vector a, Vector b) {
        var dx = a.X - b.X; var dy = a.Y - b.Y; var dz = a.Z - b.Z;
        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}