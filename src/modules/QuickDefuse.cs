using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Menu;

namespace OSBase.Modules;

public class QuickDefuse : IModule {
    public string ModuleName => "quickdefuse";

    private OSBase? osbase;
    private Config? config;

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

        osbase.AddCommand("css_quickdefuse", "Open QuickDefuse menu test", OnQuickDefuseCommand);
        osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        osbase.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
    }

    private void OnQuickDefuseCommand(CCSPlayerController? player, CommandInfo command) {
        if (player == null || !player.IsValid) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] css_quickdefuse must be run by a player.");
            return;
        }

        OpenCableMenu(player);
    }

    private HookResult OnRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {
        foreach (CCSPlayerController player in Utilities.GetPlayers()) {
            if (!player.IsValid) {
                continue;
            }

            ForceCloseMenu(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
        CCSPlayerController? player = eventInfo.Userid;

        if (player != null && player.IsValid) {
            ForceCloseMenu(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect eventInfo, GameEventInfo gameEventInfo) {
        CCSPlayerController? player = eventInfo.Userid;

        if (player != null && player.IsValid) {
            ForceCloseMenu(player);
        }

        return HookResult.Continue;
    }

    private void OpenCableMenu(CCSPlayerController player) {
        if (osbase == null) {
            return;
        }

        ForceCloseMenu(player);

        CenterHtmlMenu menu = new CenterHtmlMenu("Quick Defuse - Cut a cable", osbase) {
            ExitButton = false,
            PostSelectAction = PostSelectAction.Close
        };

        menu.AddMenuOption("Red", (p, option) => OnCableSelected(p, "Red"));
        menu.AddMenuOption("Blue", (p, option) => OnCableSelected(p, "Blue"));
        menu.AddMenuOption("Yellow", (p, option) => OnCableSelected(p, "Yellow"));
        menu.AddMenuOption("Green", (p, option) => OnCableSelected(p, "Green"));

        menu.Open(player);
    }

    private void OnCableSelected(CCSPlayerController player, string cableColor) {
        player.PrintToChat($"[OSBase] You selected cable: {cableColor}");
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {player.PlayerName} selected cable: {cableColor}");

        ForceCloseMenu(player);
    }

    private static void ForceCloseMenu(CCSPlayerController player) {
        IMenuInstance? activeMenu = MenuManager.GetActiveMenu(player);
        activeMenu?.Close();

        player.PrintToCenterHtml(" ");
    }
}