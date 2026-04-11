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
    private readonly HashSet<IntPtr> enabledPlayers = new();

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

        osbase.RegisterListener<CoreListeners.OnPlayerButtonsChanged>(OnPlayerButtonsChanged);
        osbase.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        osbase.AddCommand("css_quickdefuse", "Toggle QuickDefuse input test mode", OnToggleCommand);
    }

    private void OnToggleCommand(CCSPlayerController? player, CommandInfo command) {
        if (player == null || !player.IsValid) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] css_quickdefuse must be run by a player.");
            return;
        }

        if (enabledPlayers.Contains(player.Handle)) {
            enabledPlayers.Remove(player.Handle);
            player.PrintToChat("[OSBase] QuickDefuse test disabled.");
            return;
        }

        enabledPlayers.Add(player.Handle);
        player.PrintToChat("[OSBase] QuickDefuse test enabled. Press 1/2/3/4 and watch chat.");
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect eventInfo, GameEventInfo gameEventInfo) {
        CCSPlayerController? player = eventInfo.Userid;

        if (player != null) {
            enabledPlayers.Remove(player.Handle);
        }

        return HookResult.Continue;
    }

    private void OnPlayerButtonsChanged(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released) {
        if (player == null || !player.IsValid) {
            return;
        }

        if (!enabledPlayers.Contains(player.Handle)) {
            return;
        }

        ulong pressedValue = Convert.ToUInt64(pressed);
        ulong releasedValue = Convert.ToUInt64(released);

        if (pressedValue == 0 && releasedValue == 0) {
            return;
        }

        string pressedText = ButtonsToString(pressed);
        string releasedText = ButtonsToString(released);

        player.PrintToChat(
            $"[OSBase] pressed: {pressedText} ({pressedValue}) | released: {releasedText} ({releasedValue})"
        );
    }

    private static string ButtonsToString(PlayerButtons buttons) {
        ulong value = Convert.ToUInt64(buttons);

        if (value == 0) {
            return "none";
        }

        List<string> names = Enum.GetValues<PlayerButtons>()
            .Where(button => Convert.ToUInt64(button) != 0 && buttons.HasFlag(button))
            .Select(button => button.ToString())
            .ToList();

        if (names.Count == 0) {
            return $"unknown({value})";
        }

        return string.Join(", ", names);
    }
}