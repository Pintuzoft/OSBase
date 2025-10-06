using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules;

public class Idle : IModule {
    public string ModuleName => "idle";

    private const int TEAM_T  = (int)CsTeam.Terrorist;
    private const int TEAM_CT = (int)CsTeam.CounterTerrorist;

    private OSBase? osbase;
    private Config? config;

    // cfg
    private float checkInterval = 10f; // seconds between checks
    private float moveThreshold = 5f;  // units; hashing is quantized by this
    private int   warnAfter     = 3;   // equal-hash checks before warn
    private int   moveAfter     = 6;   // equal-hash checks before move
    private bool  debug         = false;

    // state
    private readonly Dictionary<uint, PlayerData> tracked = new();
    private bool roundActive = false;

    private class PlayerData {
        public int LastHash { get; set; }
        public int StillCount { get; set; }
        public bool HasBaseline { get; set; }
    }

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        // default OFF
        config.RegisterGlobalConfigValue(ModuleName, "0");
        if (config.GetGlobalConfigValue(ModuleName, "0") != "1") {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in OSBase.cfg.");
            return;
        }

        CreateCustomConfigs();
        LoadEventHandlers();
        osbase!.AddTimer(checkInterval, Tick);

        if (debug)
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded (interval={checkInterval}s thr={moveThreshold} warn={warnAfter} move={moveAfter})");
    }

    private void CreateCustomConfigs() {
        if (config == null) return;

        config.CreateCustomConfig("idle.cfg",
            "// Idle module configuration\n" +
            "check_interval=10\n" +
            "move_threshold=5\n" +
            "warn_after=3\n" +
            "move_after=6\n" +
            "debug=0\n"
        );

        foreach (var raw in config.FetchCustomConfig("idle.cfg") ?? new List<string>()) {
            var line = raw?.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) continue;
            var kv = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;

            switch (kv[0].ToLowerInvariant()) {
                case "check_interval": if (float.TryParse(kv[1], out var ci)) checkInterval = ci; break;
                case "move_threshold": if (float.TryParse(kv[1], out var mt)) moveThreshold = mt; break;
                case "warn_after":     if (int.TryParse(kv[1],   out var wa)) warnAfter = wa;     break;
                case "move_after":     if (int.TryParse(kv[1],   out var ma)) moveAfter = ma;     break;
                case "debug":          debug = kv[1] == "1" || kv[1].Equals("true", StringComparison.OrdinalIgnoreCase); break;
            }
        }
        if (moveAfter < warnAfter) moveAfter = warnAfter;
    }

    private void LoadEventHandlers() {
        if (osbase == null) return;
        osbase.RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd _, GameEventInfo __) {
        roundActive = true;
        tracked.Clear();
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd _, GameEventInfo __) {
        roundActive = false;
        tracked.Clear();
        return HookResult.Continue;
    }

    private void Tick() {
        try {
            if (roundActive) CheckPlayers();
        } catch (Exception ex) {
            if (debug) Console.WriteLine($"[ERROR] OSBase[{ModuleName}] {ex}");
        } finally {
            osbase?.AddTimer(checkInterval, Tick); // reschedule
        }
    }

    // Safe position getter with no nullable returns.
    // Returns true + pos when player is a valid alive human on T/CT, else false.
    private static bool TryGetPosition(CCSPlayerController player, out Vector pos) {
        pos = new Vector();

        if (player == null || !player.IsValid) return false;
        if (player.IsHLTV || player.IsBot) return false;
        if (player.Connected != PlayerConnectedState.PlayerConnected) return false;
        if (player.TeamNum < 2) return false;

        var pawnHandle = player.PlayerPawn;
        if (pawnHandle == null || !pawnHandle.IsValid) return false;

        var pawn = pawnHandle.Value;
        if (pawn == null) return false;
        if (pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return false;

        // AbsOrigin is valid for an alive pawn; null-forgiving keeps the compiler happy.
        var abs = pawn.AbsOrigin!;
        pos = new Vector(abs.X, abs.Y, abs.Z);
        return true;
    }

    // Integer spatial hash with quantization by moveThreshold
    private int HashPosition(Vector v) {
        float scale = moveThreshold <= 0f ? 1f : moveThreshold; // avoid div by zero
        int qx = (int)MathF.Round(v.X / scale);
        int qy = (int)MathF.Round(v.Y / scale);
        int qz = (int)MathF.Round(v.Z / scale);

        // mix with big primes
        return (qx * 73856093) ^ (qy * 19349663) ^ (qz * 83492791);
    }

    private void CheckPlayers() {
        var playersList = Utilities.GetPlayers();
        if (playersList == null) return;

        foreach (var player in playersList) {
            if (player == null || !player.IsValid) continue;
            if (player.IsHLTV || player.IsBot) continue;
            if (player.Connected != PlayerConnectedState.PlayerConnected) continue;
            if (player.TeamNum != TEAM_T && player.TeamNum != TEAM_CT) continue;

            if (!TryGetPosition(player, out var pos)) {
                // not alive/ready; stop tracking if we had them
                tracked.Remove(player.Index);
                continue;
            }

            int newHash = HashPosition(pos);

            if (!tracked.TryGetValue(player.Index, out var data) || !data.HasBaseline) {
                tracked[player.Index] = new PlayerData {
                    LastHash = newHash,
                    StillCount = 0,
                    HasBaseline = true
                };
                if (debug) Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] baseline set for {player.PlayerName ?? "Unknown"} h={newHash}");
                continue;
            }

            if (data.LastHash == newHash) {
                data.StillCount++;
                if (debug) Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {player.PlayerName ?? "Unknown"} still={data.StillCount} hash={newHash}");

                if (data.StillCount == warnAfter) {
                    player.PrintToChat($"{ChatColors.Red}[AFK]{ChatColors.Default} Move now or you'll be moved!");
                } else if (data.StillCount >= moveAfter) {
                    var name = player.PlayerName ?? "Player";
                    Server.PrintToChatAll($"{ChatColors.Grey}[AFK] {ChatColors.Red}{name}{ChatColors.Grey} moved to spectators.");
                    player.ChangeTeam(CsTeam.Spectator);
                    tracked.Remove(player.Index); // forget after action
                }
            } else {
                // moved → reset baseline + counter
                data.LastHash = newHash;
                data.StillCount = 0;
                if (debug) Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {player.PlayerName ?? "Unknown"} moved -> hash={newHash}");
            }
        }
    }
}