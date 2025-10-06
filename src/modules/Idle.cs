using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace OSBase.Modules;
public class Idle : IModule {
    public string ModuleName => "idle";
    private OSBase? os;
    public void Load(OSBase o, Config cfg) {
        os = o; cfg.RegisterGlobalConfigValue(ModuleName, "0");
        if (cfg.GetGlobalConfigValue(ModuleName, "0") != "1") return;
        Server.NextFrame(() => os!.AddTimer(5f, Tick));
        Console.WriteLine("[DEBUG] OSBase[idle] STEP3 timer scheduled");
    }
    private void Tick() {
        try {
            var list = Utilities.GetPlayers();
            foreach (var p in list) {
                if (p == null || !p.IsValid || p.IsBot || p.IsHLTV) continue;
                if (p.Connected != PlayerConnectedState.PlayerConnected || p.TeamNum < 2) continue;

                var pawn = p.PlayerPawn?.Value;
                var node = pawn?.CBodyComponent?.SceneNode;
                var pos  = node?.AbsOrigin;
                if (pos != null)
                    Console.WriteLine($"[DEBUG] OSBase[idle] STEP3 {p.PlayerName} pos=({pos.X:F1},{pos.Y:F1},{pos.Z:F1})");
                else
                    Console.WriteLine($"[DEBUG] OSBase[idle] STEP3 {p.PlayerName} pos=null (skip)");
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[idle] STEP3: {ex}");
        } finally {
            os?.AddTimer(5f, Tick);
        }
    }
}