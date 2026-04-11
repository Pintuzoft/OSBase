using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using CoreListeners = CounterStrikeSharp.API.Core.Listeners;

namespace OSBase.Modules;

public class QuickDefuse : IModule {
    public string ModuleName => "quickdefuse";

    private const float SessionTimeoutSeconds = 10.0f;

    private OSBase? osbase;
    private Config? config;
    private readonly Random random = new();
    private readonly Dictionary<IntPtr, ActiveDefuseSession> activeSessions = new();

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        config.RegisterGlobalConfigValue(ModuleName, "1");
        config.RegisterGlobalConfigValue($"{ModuleName}_debug", "0");

        if (osbase == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] osbase is null. {ModuleName} failed to load.");
            return;
        }

        if (config == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] config is null. {ModuleName} failed to load.");
            return;
        }

        if (config.GetGlobalConfigValue(ModuleName, "0") != "1") {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {ModuleName} is disabled in the global configuration.");
            return;
        }

        LoadHandlers();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    private void LoadHandlers() {
        if (osbase == null) {
            return;
        }

        osbase.RegisterListener<CoreListeners.OnTick>(OnTick);
        osbase.RegisterListener<CoreListeners.OnMapEnd>(OnMapEnd);
        osbase.RegisterListener<CoreListeners.OnPlayerButtonsChanged>(OnPlayerButtonsChanged);

        osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        osbase.RegisterEventHandler<EventBombBegindefuse>(OnBombBeginDefuse);
        osbase.RegisterEventHandler<EventBombAbortdefuse>(OnBombAbortDefuse);
        osbase.RegisterEventHandler<EventBombDefused>(OnBombDefused);
        osbase.RegisterEventHandler<EventBombExploded>(OnBombExploded);
        osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        osbase.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
    }

    private HookResult OnRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {
        ClearAllSessions();
        return HookResult.Continue;
    }

    private void OnMapEnd() {
        ClearAllSessions();
    }

    private HookResult OnBombBeginDefuse(EventBombBegindefuse eventInfo, GameEventInfo gameEventInfo) {
        CCSPlayerController? player = eventInfo.Userid;
        if (player == null || !player.IsValid || !player.PawnIsAlive) {
            return HookResult.Continue;
        }

        StartSession(player, eventInfo.Haskit);
        return HookResult.Continue;
    }

    private HookResult OnBombAbortDefuse(EventBombAbortdefuse eventInfo, GameEventInfo gameEventInfo) {
        CCSPlayerController? player = eventInfo.Userid;
        if (player != null && player.IsValid) {
            EndSession(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnBombDefused(EventBombDefused eventInfo, GameEventInfo gameEventInfo) {
        ClearAllSessions();
        return HookResult.Continue;
    }

    private HookResult OnBombExploded(EventBombExploded eventInfo, GameEventInfo gameEventInfo) {
        ClearAllSessions();
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
        CCSPlayerController? player = eventInfo.Userid;
        if (player != null && player.IsValid) {
            EndSession(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect eventInfo, GameEventInfo gameEventInfo) {
        CCSPlayerController? player = eventInfo.Userid;
        if (player != null) {
            activeSessions.Remove(player.Handle);
        }

        return HookResult.Continue;
    }

    private void OnTick() {
        if (activeSessions.Count == 0) {
            return;
        }

        foreach (ActiveDefuseSession session in activeSessions.Values.ToList()) {
            CCSPlayerController player = session.Player;

            if (!player.IsValid || !player.PawnIsAlive) {
                EndSession(player);
                continue;
            }

            if (Server.CurrentTime >= session.ExpiresAt) {
                EndSession(player);
                continue;
            }

            CPlantedC4? bomb = FindPlantedBomb();
            if (bomb == null || bomb.HasExploded || bomb.BombDefused || !bomb.BombTicking || !bomb.BeingDefused || bomb.CannotBeDefused) {
                EndSession(player);
                continue;
            }

            player.PrintToCenterHtml(BuildMenuHtml());
        }
    }

    private void OnPlayerButtonsChanged(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released) {
        if (!player.IsValid || !player.PawnIsAlive) {
            return;
        }

        if (!activeSessions.TryGetValue(player.Handle, out ActiveDefuseSession? session)) {
            return;
        }

        if (session.SelectionLocked) {
            return;
        }

        PlayerButtons? selectedDirection = GetSelectedDirection(pressed);
        if (selectedDirection == null) {
            return;
        }

        session.SelectionLocked = true;
        HandleSelection(player, selectedDirection.Value, session.CorrectDirection, session.HasDefuseKit);
    }

    private void StartSession(CCSPlayerController player, bool hasDefuseKit) {
        activeSessions[player.Handle] = new ActiveDefuseSession {
            Player = player,
            HasDefuseKit = hasDefuseKit,
            CorrectDirection = GetRandomDirection(),
            ExpiresAt = Server.CurrentTime + SessionTimeoutSeconds,
            SelectionLocked = false
        };

        if (IsDebugEnabled()) {
            ActiveDefuseSession session = activeSessions[player.Handle];
            player.PrintToChat($"[OSBase] DEBUG correct cable: {GetColorNameText(session.CorrectDirection)}");
        }
    }

    private void HandleSelection(CCSPlayerController player, PlayerButtons selectedDirection, PlayerButtons correctDirection, bool hasDefuseKit) {
        bool correctCable = selectedDirection == correctDirection;

        PrintCableAttempt(player, selectedDirection, hasDefuseKit);
        EndSession(player);

        if (!correctCable) {
            PrintIncorrectResult(player, selectedDirection, correctDirection);
            ForceBombExplode();
            return;
        }

        if (hasDefuseKit) {
            PrintCorrectResult(player, selectedDirection);
            ForceInstantDefuse();
            return;
        }

        bool noKitSuccess = random.Next(2) == 0;
        if (!noKitSuccess) {
            PrintNoKitFailResult(player, selectedDirection);
            ForceBombExplode();
            return;
        }

        PrintCorrectResult(player, selectedDirection);
        ForceInstantDefuse();
    }

    private void ForceInstantDefuse() {
        Server.NextFrame(() => {
            CPlantedC4? bomb = FindPlantedBomb();
            if (bomb == null || bomb.CannotBeDefused || bomb.HasExploded || bomb.BombDefused) {
                return;
            }

            bomb.DefuseCountDown = 0.0f;
        });
    }

    private void ForceBombExplode() {
        Server.NextFrame(() => {
            CPlantedC4? bomb = FindPlantedBomb();
            if (bomb == null || bomb.HasExploded || bomb.BombDefused) {
                return;
            }

            bomb.C4Blow = 1.0f;
        });
    }

    private void EndSession(CCSPlayerController player) {
        activeSessions.Remove(player.Handle);
        if (player.IsValid) {
            player.PrintToCenterHtml(" ");
        }
    }

    private void ClearAllSessions() {
        foreach (ActiveDefuseSession session in activeSessions.Values.ToList()) {
            if (session.Player.IsValid) {
                session.Player.PrintToCenterHtml(" ");
            }
        }

        activeSessions.Clear();
    }

    private bool IsDebugEnabled() {
        return config?.GetGlobalConfigValue($"{ModuleName}_debug", "0") == "1";
    }

    private PlayerButtons GetRandomDirection() {
        PlayerButtons[] directions = {
            PlayerButtons.Forward,
            PlayerButtons.Moveleft,
            PlayerButtons.Back,
            PlayerButtons.Moveright
        };

        return directions[random.Next(directions.Length)];
    }

    private static PlayerButtons? GetSelectedDirection(PlayerButtons pressed) {
        if ((pressed & PlayerButtons.Forward) != 0) {
            return PlayerButtons.Forward;
        }

        if ((pressed & PlayerButtons.Moveleft) != 0) {
            return PlayerButtons.Moveleft;
        }

        if ((pressed & PlayerButtons.Back) != 0) {
            return PlayerButtons.Back;
        }

        if ((pressed & PlayerButtons.Moveright) != 0) {
            return PlayerButtons.Moveright;
        }

        return null;
    }

    private static CPlantedC4? FindPlantedBomb() {
        return Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4").FirstOrDefault();
    }

    private static string BuildMenuHtml() {
        return string.Join("<br>", new[] {
            "<b>Quick Defuse</b>",
            "",
            "W = <font color='red'>─── Red ─────</font>",
            "A = <font color='deepskyblue'>─── Blue ─────</font>",
            "S = <font color='yellow'>─── Yellow ────</font>",
            "D = <font color='lime'>─── Green ────</font>",
            "",
            "<font color='grey'>Uses your movement binds</font>"
        });
    }

    private static string ColorWord(string text, char color) {
        return $"{color}{text}{ChatColors.Default}";
    }

    private static string GetColorNameText(PlayerButtons direction) {
        if (direction == PlayerButtons.Forward) {
            return ColorWord("Red", ChatColors.Red);
        }

        if (direction == PlayerButtons.Moveleft) {
            return ColorWord("Blue", ChatColors.Blue);
        }

        if (direction == PlayerButtons.Back) {
            return ColorWord("Yellow", ChatColors.Yellow);
        }

        return ColorWord("Green", ChatColors.Lime);
    }

    private static string GetOddsText(bool hasDefuseKit) {
        return hasDefuseKit
            ? $"{ChatColors.Green}1/4 (25%){ChatColors.Default}"
            : $"{ChatColors.Red}1/8 (12.5%){ChatColors.Default}";
    }

    private void PrintCableAttempt(CCSPlayerController player, PlayerButtons selectedDirection, bool hasDefuseKit) {
        player.PrintToChat($"[OSBase] Cut: {GetColorNameText(selectedDirection)} | Odds: {GetOddsText(hasDefuseKit)}");
    }

    private void PrintCorrectResult(CCSPlayerController player, PlayerButtons selectedDirection) {
        player.PrintToChat($"[OSBase] Result: {ChatColors.Green}Correct{ChatColors.Default} | Cable: {GetColorNameText(selectedDirection)}");
    }

    private void PrintIncorrectResult(CCSPlayerController player, PlayerButtons selectedDirection, PlayerButtons correctDirection) {
        player.PrintToChat(
            $"[OSBase] Result: {ChatColors.Red}Incorrect{ChatColors.Default} | Cut: {GetColorNameText(selectedDirection)} | Correct: {GetColorNameText(correctDirection)}"
        );
    }

    private void PrintNoKitFailResult(CCSPlayerController player, PlayerButtons selectedDirection) {
        player.PrintToChat(
            $"[OSBase] Result: {ChatColors.Red}Incorrect{ChatColors.Default} | Cable: {GetColorNameText(selectedDirection)} | Correct cable, but without a defuse kit the bomb exploded anyway."
        );
    }

    private class ActiveDefuseSession {
        public CCSPlayerController Player { get; set; } = null!;
        public PlayerButtons CorrectDirection { get; set; }
        public bool HasDefuseKit { get; set; }
        public bool SelectionLocked { get; set; }
        public float ExpiresAt { get; set; }
    }
}