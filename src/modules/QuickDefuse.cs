using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using CoreListeners = CounterStrikeSharp.API.Core.Listeners;

namespace OSBase.Modules;

public class QuickDefuse : IModule {
    public string ModuleName => "quickdefuse";

    private OSBase? osbase;
    private Config? config;
    private readonly Random random = new();
    private readonly Dictionary<IntPtr, ActiveDebugSession> activeSessions = new();
    private int renderTickCounter = 0;

    private const string ForwardColor = "Red";
    private const string LeftColor = "Blue";
    private const string BackColor = "Yellow";
    private const string RightColor = "Green";

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        config.RegisterGlobalConfigValue(ModuleName, "1");

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

        osbase.AddCommand("css_quickdefuse", "Start QuickDefuse debug test", OnQuickDefuseCommand);
        osbase.AddCommand("css_quickdefuse_clear", "Clear QuickDefuse debug test", OnQuickDefuseClearCommand);

        osbase.RegisterListener<CoreListeners.OnPlayerButtonsChanged>(OnPlayerButtonsChanged);
        osbase.RegisterListener<CoreListeners.OnTick>(OnTick);

        osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        osbase.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
    }

    private void OnQuickDefuseCommand(CCSPlayerController? player, CommandInfo command) {
        if (player == null || !player.IsValid) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] css_quickdefuse must be run by a player.");
            return;
        }

        StartDebugSession(player);
    }

    private void OnQuickDefuseClearCommand(CCSPlayerController? player, CommandInfo command) {
        if (player == null || !player.IsValid) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] css_quickdefuse_clear must be run by a player.");
            return;
        }

        EndSession(player);
        player.PrintToChat("[OSBase] QuickDefuse debug cleared.");
    }

    private HookResult OnRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {
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

        renderTickCounter++;
        if (renderTickCounter < 8) {
            return;
        }

        renderTickCounter = 0;

        foreach (ActiveDebugSession session in activeSessions.Values.ToList()) {
            CCSPlayerController player = session.Player;

            if (!player.IsValid) {
                activeSessions.Remove(player.Handle);
                continue;
            }

            player.PrintToCenterHtml(BuildMenuHtml());
        }
    }

    private void OnPlayerButtonsChanged(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released) {
        if (player == null || !player.IsValid) {
            return;
        }

        if (!activeSessions.TryGetValue(player.Handle, out ActiveDebugSession? session)) {
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
        HandleSelection(player, selectedDirection.Value, session.CorrectDirection);
    }

    private void StartDebugSession(CCSPlayerController player) {
        PlayerButtons correctDirection = GetRandomDirection();

        activeSessions[player.Handle] = new ActiveDebugSession {
            Player = player,
            CorrectDirection = correctDirection,
            SelectionLocked = false
        };

        player.PrintToChat("[OSBase] QuickDefuse debug started.");
        player.PrintToChat("[OSBase] Use your forward / left / back / right movement binds to choose.");
        player.PrintToChat($"[OSBase] DEBUG correct cable is: {GetColorForDirection(correctDirection)}");
    }

    private void HandleSelection(CCSPlayerController player, PlayerButtons selectedDirection, PlayerButtons correctDirection) {
        string selectedColor = GetColorForDirection(selectedDirection);
        string correctColor = GetColorForDirection(correctDirection);
        string selectedDirectionName = GetDirectionName(selectedDirection);
        string correctDirectionName = GetDirectionName(correctDirection);

        EndSession(player);

        if (selectedDirection == correctDirection) {
            player.PrintToChat($"[OSBase] Correct! You selected {selectedColor} using {selectedDirectionName}.");
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {player.PlayerName} selected correct cable {selectedColor} using {selectedDirectionName}.");
            return;
        }

        player.PrintToChat($"[OSBase] Wrong! You selected {selectedColor} using {selectedDirectionName}.");
        player.PrintToChat($"[OSBase] Correct cable was {correctColor} using {correctDirectionName}.");
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {player.PlayerName} selected wrong cable {selectedColor} using {selectedDirectionName}. Correct was {correctColor} using {correctDirectionName}.");
    }

    private void EndSession(CCSPlayerController player) {
        activeSessions.Remove(player.Handle);
        player.PrintToCenterHtml(" ");
    }

    private void ClearAllSessions() {
        foreach (ActiveDebugSession session in activeSessions.Values.ToList()) {
            if (session.Player != null && session.Player.IsValid) {
                session.Player.PrintToCenterHtml(" ");
            }
        }

        activeSessions.Clear();
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

    private PlayerButtons GetRandomDirection() {
        PlayerButtons[] directions = {
            PlayerButtons.Forward,
            PlayerButtons.Moveleft,
            PlayerButtons.Back,
            PlayerButtons.Moveright
        };

        return directions[random.Next(directions.Length)];
    }

    private static string GetColorForDirection(PlayerButtons direction) {
        if (direction == PlayerButtons.Forward) {
            return ForwardColor;
        }

        if (direction == PlayerButtons.Moveleft) {
            return LeftColor;
        }

        if (direction == PlayerButtons.Back) {
            return BackColor;
        }

        return RightColor;
    }

    private static string GetDirectionName(PlayerButtons direction) {
        if (direction == PlayerButtons.Forward) {
            return "Forward";
        }

        if (direction == PlayerButtons.Moveleft) {
            return "Left";
        }

        if (direction == PlayerButtons.Back) {
            return "Back";
        }

        return "Right";
    }

    private static string BuildMenuHtml() {
        return string.Join("<br>", new[] {
            "<b>Quick Defuse Debug</b>",
            "",
            "<font color='white'>W</font> = <font color='red'>────&nbsp;Red&nbsp;&nbsp;&nbsp;&nbsp;────</font>",
            "<font color='white'>A</font> = <font color='deepskyblue'>────&nbsp;Blue&nbsp;&nbsp;────</font>",
            "<font color='white'>S</font> = <font color='yellow'>───&nbsp;Yellow&nbsp;───</font>",
            "<font color='white'>D</font> = <font color='lime'>───&nbsp;Green&nbsp;&nbsp;───</font>",
            "",
            "<font color='grey'>Uses your movement binds</font>"
        });
    }
    private class ActiveDebugSession {
        public CCSPlayerController Player { get; set; } = null!;
        public PlayerButtons CorrectDirection { get; set; }
        public bool SelectionLocked { get; set; }
    }
}