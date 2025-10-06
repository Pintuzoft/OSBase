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
        Console.WriteLine("[DEBUG] OSBase[idle] STEP2 timer scheduled");
    }
    private void Tick() {
        try {
            var list = Utilities.GetPlayers();
            Console.WriteLine($"[DEBUG] OSBase[idle] STEP2 players={list.Count}");
            foreach (var p in list) {
                if (p == null) continue;
                Console.WriteLine($"  idx={p.Index} name='{p.PlayerName}' bot={p.IsBot} conn={p.Connected} team={p.TeamNum}");
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[idle] STEP2: {ex}");
        } finally {
            os?.AddTimer(5f, Tick);
        }
    }
}