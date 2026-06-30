using System;
using System.Collections.Generic;
using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace OSBase.Modules;

public class Idle : ModuleBase {
    public override string ModuleName => "idle";
    protected override string DefaultEnabled => "0";

    private float spawnEpsilon = 0.5f;
    private float deathSpecAfterSeconds = 25f;
    private float warnAfterSeconds = 30f;
    private float moveAfterSeconds = 45f;

    private bool roundActive = false;

    private readonly Dictionary<uint, Tracked> tracked = new();

    private sealed class Tracked {
        public Vector SpawnPos = new();
        public bool SpawnDeathSpecArmed;
        public Timer? ArmTimer;
        public Timer? WarnTimer;
        public Timer? MoveTimer;
    }

    protected override void OnLoad() {
        CreateCustomConfigs();
        LoadConfig();
    }

    protected override void OnUnload() {
        roundActive = false;
        ClearTracked();
    }

    protected override void OnReloadConfig() {
        CreateCustomConfigs();
        LoadConfig();
    }

    protected override void RegisterHandlers() {
        // Use new EventBus system
        osbase?.SubscribeToEvent<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        osbase?.SubscribeToEvent<EventRoundEnd>(OnRoundEnd);
        osbase?.SubscribeToEvent<EventPlayerDeath>(OnPlayerDeath);
        osbase?.SubscribeToEvent<EventMapTransition>(OnMapTransition);
        osbase?.RegisterListener<Listeners.OnPlayerButtonsChanged>(OnPlayerButtonsChanged);
    }

    protected override void UnregisterHandlers() {
        // Use new EventBus system
        osbase?.UnsubscribeFromEvent<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        osbase?.UnsubscribeFromEvent<EventRoundEnd>(OnRoundEnd);
        osbase?.UnsubscribeFromEvent<EventPlayerDeath>(OnPlayerDeath);
        osbase?.UnsubscribeFromEvent<EventMapTransition>(OnMapTransition);
        osbase?.RemoveListener<Listeners.OnPlayerButtonsChanged>(OnPlayerButtonsChanged);
    }

    private void CreateCustomConfigs() {
        config?.CreateCustomConfig(
            "idle.cfg",
            "// Idle module\n" +
            "spawn_epsilon=0.5\n" +
            "death_spec_after_seconds=25\n" +
            "warn_after_seconds=30\n" +
            "move_after_seconds=45\n"
        );
    }

    private void LoadConfig() {
        spawnEpsilon = 0.5f;
        deathSpecAfterSeconds = 25f;
        warnAfterSeconds = 30f;
        moveAfterSeconds = 45f;

        foreach (var line in config?.FetchCustomConfig("idle.cfg") ?? new List<string>()) {
            var s = line.Trim();
            if (s.Length == 0 || s.StartsWith("//")) {
                continue;
            }

            var kv = s.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) {
                continue;
            }

            switch (kv[0].ToLowerInvariant()) {
                case "spawn_epsilon":
                    if (TryParseFloat(kv[1], out var se)) {
                        spawnEpsilon = MathF.Max(0.01f, se);
                    }
                    break;

                case "death_spec_after_seconds":
                    if (TryParseFloat(kv[1], out var ds)) {
                        deathSpecAfterSeconds = MathF.Max(1f, ds);
                    }
                    break;

                case "warn_after_seconds":
                    if (TryParseFloat(kv[1], out var wa)) {
                        warnAfterSeconds = MathF.Max(0f, wa);
                    }
                    break;

                case "move_after_seconds":
                    if (TryParseFloat(kv[1], out var ma)) {
                        moveAfterSeconds = MathF.Max(1f, ma);
                    }
                    break;
            }
        }

        if (warnAfterSeconds > moveAfterSeconds) {
            warnAfterSeconds = moveAfterSeconds;
        }

        if (deathSpecAfterSeconds > moveAfterSeconds) {
            deathSpecAfterSeconds = moveAfterSeconds;
        }

        Console.WriteLine(
            $"[DEBUG] OSBase[{ModuleName}] config loaded. " +
            $"spawnEpsilon={spawnEpsilon:0.00} deathSpecAfter={deathSpecAfterSeconds:0.0} " +
            $"warnAfter={warnAfterSeconds:0.0} moveAfter={moveAfterSeconds:0.0}"
        );
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd _) {
        if (!isActive) {
            return HookResult.Continue;
        }

        roundActive = true;
        ClearTracked();

        foreach (var p in Utilities.GetPlayers()) {
            if (!IsAliveHuman(p)) {
                continue;
            }

            if (!TryGetPos(p!, out var pos)) {
                continue;
            }

            StartTracking(p!.Index, pos);
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd _) {
        roundActive = false;
        ClearTracked();
        return HookResult.Continue;
    }

    private HookResult OnMapTransition(EventMapTransition _) {
        roundActive = false;
        ClearTracked();
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event) {
        if (!isActive || !roundActive) {
            return HookResult.Continue;
        }

        var victim = @event.Userid;
        var attacker = @event.Attacker;

        if (victim == null || !victim.IsValid || victim.IsHLTV || victim.IsBot) {
            return HookResult.Continue;
        }

        if (!tracked.TryGetValue(victim.Index, out var data)) {
            return HookResult.Continue;
        }

        var victimIndex = victim.Index;
        var victimName = victim.PlayerName ?? "Player";
        var victimTeam = victim.TeamNum;

        var shouldMoveToSpec =
            data.SpawnDeathSpecArmed &&
            attacker != null &&
            attacker.IsValid &&
            !attacker.IsHLTV &&
            !attacker.IsBot &&
            attacker.Index != victimIndex &&
            attacker.TeamNum != victimTeam;

        RemoveTracked(victimIndex);

        if (!shouldMoveToSpec) {
            return HookResult.Continue;
        }

        Server.NextFrame(() => {
            if (!isActive) {
                return;
            }

            var p = Utilities.GetPlayerFromIndex((int)victimIndex);
            if (p == null || !p.IsValid) {
                return;
            }

            if (p.Connected != PlayerConnectedState.Connected) {
                return;
            }

            if (p.TeamNum != (int)CsTeam.Terrorist && p.TeamNum != (int)CsTeam.CounterTerrorist) {
                return;
            }

            p.ChangeTeam(CsTeam.Spectator);
            Server.PrintToChatAll($"{ChatColors.Grey}[AFK]{ChatColors.Red} {victimName} {ChatColors.Grey}was killed while idle on spawn and moved to spectators.");
        });

        return HookResult.Continue;
    }

    private void OnPlayerButtonsChanged(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released) {
        if (!isActive || !roundActive) {
            return;
        }

        if (player == null || !player.IsValid || player.IsHLTV || player.IsBot) {
            return;
        }

        if (!tracked.ContainsKey(player.Index)) {
            return;
        }

        var moveMask =
            PlayerButtons.Forward |
            PlayerButtons.Back |
            PlayerButtons.Moveleft |
            PlayerButtons.Moveright;

        if ((pressed & moveMask) == 0) {
            return;
        }

        RemoveTracked(player.Index);
    }

    private void StartTracking(uint index, Vector spawnPos) {
        if (!isActive || osbase == null) {
            return;
        }

        RemoveTracked(index);

        var data = new Tracked {
            SpawnPos = CloneVector(spawnPos),
            SpawnDeathSpecArmed = false
        };

        tracked[index] = data;

        data.ArmTimer = osbase.AddTimer(
            deathSpecAfterSeconds,
            () => ArmIfStillOnSpawn(index),
            TimerFlags.STOP_ON_MAPCHANGE
        );

        if (warnAfterSeconds > 0f) {
            data.WarnTimer = osbase.AddTimer(
                warnAfterSeconds,
                () => WarnIfStillOnSpawn(index),
                TimerFlags.STOP_ON_MAPCHANGE
            );
        }

        data.MoveTimer = osbase.AddTimer(
            moveAfterSeconds,
            () => MoveIfStillOnSpawn(index),
            TimerFlags.STOP_ON_MAPCHANGE
        );
    }

    private void ArmIfStillOnSpawn(uint index) {
        if (!isActive || !roundActive) {
            return;
        }

        if (!tracked.TryGetValue(index, out var data)) {
            return;
        }

        var p = Utilities.GetPlayerFromIndex((int)index);
        if (!IsAliveHuman(p)) {
            RemoveTracked(index);
            return;
        }

        if (!TryGetPos(p!, out var pos)) {
            RemoveTracked(index);
            return;
        }

        if (!SamePos(pos, data.SpawnPos, spawnEpsilon)) {
            RemoveTracked(index);
            return;
        }

        data.SpawnDeathSpecArmed = true;
    }

    private void WarnIfStillOnSpawn(uint index) {
        if (!isActive || !roundActive) {
            return;
        }

        if (!tracked.TryGetValue(index, out var data)) {
            return;
        }

        var p = Utilities.GetPlayerFromIndex((int)index);
        if (!IsAliveHuman(p)) {
            RemoveTracked(index);
            return;
        }

        if (!TryGetPos(p!, out var pos)) {
            RemoveTracked(index);
            return;
        }

        if (!SamePos(pos, data.SpawnPos, spawnEpsilon)) {
            RemoveTracked(index);
            return;
        }

        p!.PrintToChat($"{ChatColors.Yellow}[⚠ AFK Warning]{ChatColors.Default} You’re idle! Move now or you will be {ChatColors.Red}moved to spectators{ChatColors.Default}.");
    }

    private void MoveIfStillOnSpawn(uint index) {
        if (!isActive || !roundActive) {
            return;
        }

        if (!tracked.TryGetValue(index, out var data)) {
            return;
        }

        var p = Utilities.GetPlayerFromIndex((int)index);
        if (!IsAliveHuman(p)) {
            RemoveTracked(index);
            return;
        }

        if (!TryGetPos(p!, out var pos)) {
            RemoveTracked(index);
            return;
        }

        if (!SamePos(pos, data.SpawnPos, spawnEpsilon)) {
            RemoveTracked(index);
            return;
        }

        var name = p!.PlayerName ?? "Player";
        var teamAlive = AliveHumansOnTeam(p.TeamNum);

        if (teamAlive <= 1) {
            var pawn = p.PlayerPawn?.Value;
            if (pawn != null && pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE) {
                pawn.CommitSuicide(false, true);
            }

            p.ChangeTeam(CsTeam.Spectator);
            Server.PrintToChatAll($"{ChatColors.Grey}[AFK]{ChatColors.Yellow} {name}{ChatColors.Default} was last alive, {ChatColors.Red}slain{ChatColors.Default} and moved to spectators for being idle.");
        } else {
            p.ChangeTeam(CsTeam.Spectator);
            Server.PrintToChatAll($"{ChatColors.Grey}[AFK]{ChatColors.Red} {name} {ChatColors.Grey}was moved to spectators for being idle.");
        }

        RemoveTracked(index);
    }

    private void RemoveTracked(uint index) {
        if (!tracked.TryGetValue(index, out var data)) {
            return;
        }

        data.ArmTimer?.Kill();
        data.WarnTimer?.Kill();
        data.MoveTimer?.Kill();

        tracked.Remove(index);
    }

    private void ClearTracked() {
        foreach (var data in tracked.Values) {
            data.ArmTimer?.Kill();
            data.WarnTimer?.Kill();
            data.MoveTimer?.Kill();
        }

        tracked.Clear();
    }

    private static bool TryParseFloat(string input, out float value) {
        return float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
               float.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    private static Vector CloneVector(Vector v) {
        return new Vector(v.X, v.Y, v.Z);
    }

    private static bool SamePos(Vector a, Vector b, float epsilon) {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz) <= (epsilon * epsilon);
    }

    private static bool IsAliveHuman(CCSPlayerController? p) {
        if (p == null || !p.IsValid || p.IsHLTV || p.IsBot) {
            return false;
        }

        if (p.Connected != PlayerConnectedState.Connected) {
            return false;
        }

        if (p.TeamNum != (int)CsTeam.Terrorist && p.TeamNum != (int)CsTeam.CounterTerrorist) {
            return false;
        }

        var ph = p.PlayerPawn;
        if (ph == null || !ph.IsValid) {
            return false;
        }

        var pawn = ph.Value;
        if (pawn == null) {
            return false;
        }

        if (pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) {
            return false;
        }

        return pawn.AbsOrigin != null;
    }

    private static bool TryGetPos(CCSPlayerController p, out Vector pos) {
        pos = new Vector();

        var ph = p.PlayerPawn;
        if (ph == null || !ph.IsValid) {
            return false;
        }

        var pawn = ph.Value;
        if (pawn == null || pawn.AbsOrigin == null) {
            return false;
        }

        var a = pawn.AbsOrigin;
        pos = new Vector(a.X, a.Y, a.Z);
        return true;
    }

    private static int AliveHumansOnTeam(int teamNum) {
        int n = 0;

        foreach (var pl in Utilities.GetPlayers()) {
            if (pl == null || !pl.IsValid || pl.IsHLTV || pl.IsBot) {
                continue;
            }

            if (pl.Connected != PlayerConnectedState.Connected) {
                continue;
            }

            if (pl.TeamNum != teamNum) {
                continue;
            }

            var ph = pl.PlayerPawn;
            if (ph == null || !ph.IsValid) {
                continue;
            }

            var pawn = ph.Value;
            if (pawn == null) {
                continue;
            }

            if (pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE) {
                n++;
            }
        }

        return n;
    }
}