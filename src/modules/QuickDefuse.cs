using System;
using System.Collections.Generic;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;

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

        osbase.AddCommand("css_quickdefuse", "Toggle QuickDefuse slot test mode", OnToggleCommand);

        osbase.AddCommandListener("slot1", OnSlot1);
        osbase.AddCommandListener("slot2", OnSlot2);
        osbase.AddCommandListener("slot3", OnSlot3);
        osbase.AddCommandListener("slot4", OnSlot4);

        osbase.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
    }

    private void OnToggleCommand(CCSPlayerController? player, CommandInfo command) {
        if (player == null || !player.IsValid) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] css_quickdefuse must be run by a player.");
            return;
        }

        if (enabledPlayers.Contains(player.Handle)) {
            enabledPlayers.Remove(player.Handle);
            player.PrintToChat("[OSBase] QuickDefuse slot test disabled.");
            return;
        }

        enabledPlayers.Add(player.Handle);
        player.PrintToChat("[OSBase] QuickDefuse slot test enabled. Press 1/2/3/4.");
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect eventInfo, GameEventInfo gameEventInfo) {
        CCSPlayerController? player = eventInfo.Userid;

        if (player != null) {
            enabledPlayers.Remove(player.Handle);
        }

        return HookResult.Continue;
    }

    private HookResult OnSlot1(CCSPlayerController? player, CommandInfo command) {
        return HandleSlot(player, 1);
    }

    private HookResult OnSlot2(CCSPlayerController? player, CommandInfo command) {
        return HandleSlot(player, 2);
    }

    private HookResult OnSlot3(CCSPlayerController? player, CommandInfo command) {
        return HandleSlot(player, 3);
    }

    private HookResult OnSlot4(CCSPlayerController? player, CommandInfo command) {
        return HandleSlot(player, 4);
    }

    private HookResult HandleSlot(CCSPlayerController? player, int slot) {
        if (player == null || !player.IsValid) {
            return HookResult.Continue;
        }

        if (!enabledPlayers.Contains(player.Handle)) {
            return HookResult.Continue;
        }

        player.PrintToChat($"[OSBase] slot{slot} detected.");
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {player.PlayerName} used slot{slot}.");

        return HookResult.Continue;
    }
}