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
    private const float ConfirmTimeoutSeconds = 3.0f;

    private OSBase? osbase;
    private Config? config;
    private readonly Random random = new();

    private readonly Dictionary<IntPtr, ActivePlantSession> activePlantSessions = new();
    private readonly Dictionary<IntPtr, ActiveDefuseSession> activeDefuseSessions = new();

    private PlayerButtons? activeBombDirection = null;
    private bool handlersLoaded = false;

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
        ClearAllState();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    public void Unload() {
        if (osbase != null && handlersLoaded) {
            osbase.RemoveListener<CoreListeners.OnTick>(OnTick);
            osbase.RemoveListener<CoreListeners.OnMapEnd>(OnMapEnd);
            osbase.RemoveListener<CoreListeners.OnPlayerButtonsChanged>(OnPlayerButtonsChanged);

            osbase.DeregisterEventHandler<EventRoundStart>(OnRoundStart);

            osbase.DeregisterEventHandler<EventBombBeginplant>(OnBombBeginPlant);
            osbase.DeregisterEventHandler<EventBombAbortplant>(OnBombAbortPlant);
            osbase.DeregisterEventHandler<EventBombPlanted>(OnBombPlanted);

            osbase.DeregisterEventHandler<EventBombBegindefuse>(OnBombBeginDefuse);
            osbase.DeregisterEventHandler<EventBombAbortdefuse>(OnBombAbortDefuse);
            osbase.DeregisterEventHandler<EventBombDefused>(OnBombDefused);
            osbase.DeregisterEventHandler<EventBombExploded>(OnBombExploded);

            osbase.DeregisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            osbase.DeregisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);

            handlersLoaded = false;
        }

        ClearAllState();

        osbase = null;
        config = null;

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] unloaded.");
    }

    public void ReloadConfig(Config inConfig) {
        config = inConfig;
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] config reloaded.");
    }

    private void LoadHandlers() {
        if (osbase == null || handlersLoaded) {
            return;
        }

        osbase.RegisterListener<CoreListeners.OnTick>(OnTick);
        osbase.RegisterListener<CoreListeners.OnMapEnd>(OnMapEnd);
        osbase.RegisterListener<CoreListeners.OnPlayerButtonsChanged>(OnPlayerButtonsChanged);

        osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);

        osbase.RegisterEventHandler<EventBombBeginplant>(OnBombBeginPlant);
        osbase.RegisterEventHandler<EventBombAbortplant>(OnBombAbortPlant);
        osbase.RegisterEventHandler<EventBombPlanted>(OnBombPlanted);

        osbase.RegisterEventHandler<EventBombBegindefuse>(OnBombBeginDefuse);
        osbase.RegisterEventHandler<EventBombAbortdefuse>(OnBombAbortDefuse);
        osbase.RegisterEventHandler<EventBombDefused>(OnBombDefused);
        osbase.RegisterEventHandler<EventBombExploded>(OnBombExploded);

        osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        osbase.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);

        handlersLoaded = true;
    }

    private HookResult OnRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {
        ClearAllState();
        return HookResult.Continue;
    }

    private void OnMapEnd() {
        ClearAllState();
    }

    private HookResult OnBombBeginPlant(EventBombBeginplant eventInfo, GameEventInfo gameEventInfo) {
        CCSPlayerController? player = eventInfo.Userid;
        if (player == null || !player.IsValid || !player.PawnIsAlive) {
            return HookResult.Continue;
        }

        StartPlantSession(player);
        return HookResult.Continue;
    }

    private HookResult OnBombAbortPlant(EventBombAbortplant eventInfo, GameEventInfo gameEventInfo) {
        CCSPlayerController? player = eventInfo.Userid;
        if (player != null && player.IsValid) {
            EndPlantSession(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnBombPlanted(EventBombPlanted eventInfo, GameEventInfo gameEventInfo) {
        CCSPlayerController? player = eventInfo.Userid;

        PlayerButtons chosenDirection;
        bool wasRandom = true;

        if (player != null &&
            activePlantSessions.TryGetValue(player.Handle, out ActivePlantSession? session) &&
            session.SelectedDirection != null) {
            chosenDirection = session.SelectedDirection.Value;
            wasRandom = false;
        } else {
            chosenDirection = GetRandomDirection();
        }

        activeBombDirection = chosenDirection;

        if (player != null && player.IsValid) {
            if (wasRandom) {
                PrintPlantRandomResult(player, chosenDirection);
            } else {
                PrintPlantWiredResult(player, chosenDirection);
            }

            EndPlantSession(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnBombBeginDefuse(EventBombBegindefuse eventInfo, GameEventInfo gameEventInfo) {
        CCSPlayerController? player = eventInfo.Userid;
        if (player == null || !player.IsValid || !player.PawnIsAlive) {
            return HookResult.Continue;
        }

        StartDefuseSession(player, eventInfo.Haskit);
        return HookResult.Continue;
    }

    private HookResult OnBombAbortDefuse(EventBombAbortdefuse eventInfo, GameEventInfo gameEventInfo) {
        CCSPlayerController? player = eventInfo.Userid;
        if (player != null && player.IsValid) {
            EndDefuseSession(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnBombDefused(EventBombDefused eventInfo, GameEventInfo gameEventInfo) {
        ClearAllState();
        return HookResult.Continue;
    }

    private HookResult OnBombExploded(EventBombExploded eventInfo, GameEventInfo gameEventInfo) {
        ClearAllState();
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
        CCSPlayerController? player = eventInfo.Userid;
        if (player != null && player.IsValid) {
            EndPlantSession(player);
            EndDefuseSession(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect eventInfo, GameEventInfo gameEventInfo) {
        CCSPlayerController? player = eventInfo.Userid;
        if (player != null) {
            activePlantSessions.Remove(player.Handle);
            activeDefuseSessions.Remove(player.Handle);
        }

        return HookResult.Continue;
    }

    private void OnTick() {
        if (activePlantSessions.Count == 0 && activeDefuseSessions.Count == 0) {
            return;
        }

        foreach (ActivePlantSession session in activePlantSessions.Values.ToList()) {
            CCSPlayerController player = session.Player;

            if (!player.IsValid || !player.PawnIsAlive) {
                EndPlantSession(player);
                continue;
            }

            if (Server.CurrentTime >= session.ExpiresAt) {
                EndPlantSession(player);
                continue;
            }

            if (session.SelectionLocked) {
                continue;
            }

            player.PrintToCenterHtml(BuildPlantMenuHtml());
        }

        foreach (ActiveDefuseSession session in activeDefuseSessions.Values.ToList()) {
            CCSPlayerController player = session.Player;

            if (!player.IsValid || !player.PawnIsAlive) {
                EndDefuseSession(player);
                continue;
            }

            if (Server.CurrentTime >= session.ExpiresAt) {
                EndDefuseSession(player);
                continue;
            }

            CPlantedC4? bomb = FindPlantedBomb();
            if (bomb == null ||
                bomb.HasExploded ||
                bomb.BombDefused ||
                !bomb.BombTicking ||
                !bomb.BeingDefused ||
                bomb.CannotBeDefused) {
                EndDefuseSession(player);
                continue;
            }

            if (session.SelectionLocked) {
                continue;
            }

            if (session.IsConfirming && Server.CurrentTime >= session.ConfirmExpiresAt) {
                ResetDefuseConfirm(session);
            }

            if (session.IsConfirming && session.PendingDirection != null) {
                player.PrintToCenterHtml(BuildDefuseConfirmMenuHtml(session.PendingDirection.Value));
            } else {
                player.PrintToCenterHtml(BuildDefuseMenuHtml());
            }
        }
    }

    private void OnPlayerButtonsChanged(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released) {
        if (!player.IsValid || !player.PawnIsAlive) {
            return;
        }

        PlayerButtons? selectedDirection = GetSelectedDirection(pressed);
        if (selectedDirection == null) {
            return;
        }

        if (activeDefuseSessions.TryGetValue(player.Handle, out ActiveDefuseSession? defuseSession)) {
            HandleDefuseInput(player, defuseSession, selectedDirection.Value);
            return;
        }

        if (activePlantSessions.TryGetValue(player.Handle, out ActivePlantSession? plantSession)) {
            HandlePlantInput(player, plantSession, selectedDirection.Value);
        }
    }

    private void HandlePlantInput(CCSPlayerController player, ActivePlantSession session, PlayerButtons direction) {
        if (session.SelectionLocked) {
            return;
        }

        session.SelectionLocked = true;
        session.SelectedDirection = direction;

        ClearCenter(player);
        PrintPlantSelection(player, direction);
    }

    private void HandleDefuseInput(CCSPlayerController player, ActiveDefuseSession session, PlayerButtons direction) {
        if (session.SelectionLocked) {
            return;
        }

        if (!session.IsConfirming) {
            session.IsConfirming = true;
            session.PendingDirection = direction;
            session.ConfirmExpiresAt = Server.CurrentTime + ConfirmTimeoutSeconds;
            return;
        }

        if (session.PendingDirection == direction) {
            session.SelectionLocked = true;
            HandleDefuseSelection(player, direction, session.CorrectDirection, session.HasDefuseKit);
            return;
        }

        ResetDefuseConfirm(session);
    }

    private static void ResetDefuseConfirm(ActiveDefuseSession session) {
        session.IsConfirming = false;
        session.PendingDirection = null;
        session.ConfirmExpiresAt = 0.0f;
    }

    private void StartPlantSession(CCSPlayerController player) {
        activePlantSessions[player.Handle] = new ActivePlantSession {
            Player = player,
            SelectedDirection = null,
            SelectionLocked = false,
            ExpiresAt = Server.CurrentTime + SessionTimeoutSeconds
        };
    }

    private void StartDefuseSession(CCSPlayerController player, bool hasDefuseKit) {
        if (activeBombDirection == null) {
            activeBombDirection = GetRandomDirection();
        }

        activeDefuseSessions[player.Handle] = new ActiveDefuseSession {
            Player = player,
            HasDefuseKit = hasDefuseKit,
            CorrectDirection = activeBombDirection.Value,
            SelectionLocked = false,
            ExpiresAt = Server.CurrentTime + SessionTimeoutSeconds,
            IsConfirming = false,
            PendingDirection = null,
            ConfirmExpiresAt = 0.0f
        };

        if (IsDebugEnabled()) {
            player.PrintToChat($"[OSBase] DEBUG correct cable: {GetColorNameText(activeBombDirection.Value)}");
        }
    }

    private void HandleDefuseSelection(CCSPlayerController player, PlayerButtons selectedDirection, PlayerButtons correctDirection, bool hasDefuseKit) {
        bool correctCable = selectedDirection == correctDirection;

        EndDefuseSession(player);

        if (!correctCable) {
            PrintIncorrectResult(player, selectedDirection, correctDirection, hasDefuseKit);
            ForceBombExplode();
            return;
        }

        if (hasDefuseKit) {
            PrintCorrectResult(player, selectedDirection, hasDefuseKit);
            ForceInstantDefuse();
            return;
        }

        bool noKitSuccess = random.Next(2) == 0;
        if (!noKitSuccess) {
            PrintNoKitFailResult(player, selectedDirection);
            ForceBombExplode();
            return;
        }

        PrintCorrectResult(player, selectedDirection, hasDefuseKit);
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

    private void EndPlantSession(CCSPlayerController player) {
        activePlantSessions.Remove(player.Handle);
        if (player.IsValid) {
            ClearCenter(player);
        }
    }

    private void EndDefuseSession(CCSPlayerController player) {
        activeDefuseSessions.Remove(player.Handle);
        if (player.IsValid) {
            ClearCenter(player);
        }
    }

    private void ClearAllState() {
        foreach (ActivePlantSession session in activePlantSessions.Values.ToList()) {
            if (session.Player.IsValid) {
                ClearCenter(session.Player);
            }
        }

        foreach (ActiveDefuseSession session in activeDefuseSessions.Values.ToList()) {
            if (session.Player.IsValid) {
                ClearCenter(session.Player);
            }
        }

        activePlantSessions.Clear();
        activeDefuseSessions.Clear();
        activeBombDirection = null;
    }

    private static void ClearCenter(CCSPlayerController player) {
        player.PrintToCenterHtml(" ");
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

    private static string BuildPlantMenuHtml() {
        return string.Join("<br>", new[] {
            "<b>Bomb Wiring</b>",
            "",
            "W = <font color='red'>─── Red ─────</font>",
            "A = <font color='deepskyblue'>─── Blue ─────</font>",
            "S = <font color='yellow'>─── Yellow ────</font>",
            "D = <font color='lime'>─── Green ────</font>",
            "",
            "<font color='grey'>Uses your movement binds</font>"
        });
    }

    private static string BuildDefuseMenuHtml() {
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

    private static string BuildDefuseConfirmMenuHtml(PlayerButtons direction) {
        return string.Join("<br>", new[] {
            "<b>Quick Defuse</b>",
            "",
            $"<font color='white'>Press the same movement key again to cut {GetCenterColorNameHtml(direction)}</font>",
            $"<font color='grey'>Confirm expires in {ConfirmTimeoutSeconds:0} seconds</font>",
            "",
            "<font color='grey'>Press another movement key to cancel</font>"
        });
    }

    private static string GetCenterColorNameHtml(PlayerButtons direction) {
        if (direction == PlayerButtons.Forward) {
            return "<font color='red'>Red</font>";
        }

        if (direction == PlayerButtons.Moveleft) {
            return "<font color='deepskyblue'>Blue</font>";
        }

        if (direction == PlayerButtons.Back) {
            return "<font color='yellow'>Yellow</font>";
        }

        return "<font color='lime'>Green</font>";
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

    private void PrintPlantSelection(CCSPlayerController player, PlayerButtons selectedDirection) {
        player.PrintToChat($"[OSBase] Wiring selected: {GetColorNameText(selectedDirection)}");
    }

    private void PrintPlantWiredResult(CCSPlayerController player, PlayerButtons selectedDirection) {
        player.PrintToChat($"[OSBase] Bomb wired: {GetColorNameText(selectedDirection)}");
    }

    private void PrintPlantRandomResult(CCSPlayerController player, PlayerButtons selectedDirection) {
        player.PrintToChat($"[OSBase] No cable chosen | Bomb wired randomly: {GetColorNameText(selectedDirection)}");
    }

    private void PrintCorrectResult(CCSPlayerController player, PlayerButtons selectedDirection, bool hasDefuseKit) {
        Server.PrintToChatAll(
            $"[OSBase] {player.PlayerName} cut: {GetColorNameText(selectedDirection)} | Result: {ChatColors.Green}Correct{ChatColors.Default} | Odds: {GetOddsText(hasDefuseKit)}"
        );
    }

    private void PrintIncorrectResult(CCSPlayerController player, PlayerButtons selectedDirection, PlayerButtons correctDirection, bool hasDefuseKit) {
        Server.PrintToChatAll(
            $"[OSBase] {player.PlayerName} cut: {GetColorNameText(selectedDirection)} | Result: {ChatColors.Red}Incorrect{ChatColors.Default} | Correct: {GetColorNameText(correctDirection)} | Odds: {GetOddsText(hasDefuseKit)}"
        );
    }

    private void PrintNoKitFailResult(CCSPlayerController player, PlayerButtons selectedDirection) {
        Server.PrintToChatAll(
            $"[OSBase] {player.PlayerName} cut: {GetColorNameText(selectedDirection)} | Result: {ChatColors.Red}Incorrect{ChatColors.Default} | Correct: {GetColorNameText(selectedDirection)} | Odds: {GetOddsText(false)}"
        );
    }

    private class ActivePlantSession {
        public CCSPlayerController Player { get; set; } = null!;
        public PlayerButtons? SelectedDirection { get; set; }
        public bool SelectionLocked { get; set; }
        public float ExpiresAt { get; set; }
    }

    private class ActiveDefuseSession {
        public CCSPlayerController Player { get; set; } = null!;
        public PlayerButtons CorrectDirection { get; set; }
        public bool HasDefuseKit { get; set; }
        public bool SelectionLocked { get; set; }
        public float ExpiresAt { get; set; }
        public bool IsConfirming { get; set; }
        public PlayerButtons? PendingDirection { get; set; }
        public float ConfirmExpiresAt { get; set; }
    }
}