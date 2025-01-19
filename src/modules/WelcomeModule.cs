using System;
using System.IO;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Events;
using System.Diagnostics.Tracing;
using System.Reflection;

namespace OSBase.Modules;

using System.IO;

public class WelcomeModule : IModule {
    public string ModuleName => "WelcomeModule";   
     private OSBase? osbase;
    private ConfigModule? config;

    public void Load(OSBase inOsbase, ConfigModule inConfig) {
        osbase = inOsbase;
        config = inConfig;

        // Register required global config values
        config.RegisterGlobalConfigValue("welcome_msg", "1");

        config.CreateCustomConfig("welcome.cfg", "// Welcome message\n// Example: Welcome to the server!\n");

        // Register event handlers and listeners
        osbase.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);

    }

private HookResult OnPlayerConnectFull(EventPlayerConnectFull eventInfo, GameEventInfo gameEventInfo) {
    if (config == null) {
        Console.WriteLine("[DEBUG] Config module is null, skipping welcome message.");
        return HookResult.Continue;
    }

    // Check if welcome messages are enabled
    string welcomeMsgEnabled = config.GetGlobalConfigValue("welcome_msg", "0");
    Console.WriteLine($"[DEBUG] Welcome messages enabled: {welcomeMsgEnabled}");

    if (welcomeMsgEnabled != "1") {
        Console.WriteLine("[DEBUG] Welcome messages are disabled, skipping.");
        return HookResult.Continue;
    }

    // Get the player's UserId
    string playerId = eventInfo.Userid?.UserId?.ToString() ?? string.Empty;
    if (string.IsNullOrEmpty(playerId)) {
        Console.WriteLine("[DEBUG] Player ID is empty or null, skipping welcome message.");
        return HookResult.Continue;
    }
    Console.WriteLine($"[DEBUG] Sending welcome message to Player ID: {playerId}");

    // Fetch the welcome messages from the configuration file
    List<string> messages = config.FetchCustomConfig("welcome.cfg");
    Console.WriteLine($"[DEBUG] Fetched {messages.Count} lines from welcome.cfg");

    foreach (string message in messages) {
        if (message.StartsWith("//") || string.IsNullOrWhiteSpace(message)) {
            Console.WriteLine($"[DEBUG] Skipping line: {message}");
            continue;
        }

        // Send the message specifically to the connecting player
        if (osbase != null) {
            string command = $"css_psay #{playerId} {message}"; // Replace 'css_psay' with the correct command
            Console.WriteLine($"[DEBUG] Executing command: {command}");
            osbase.SendCommand(command);
        } else {
            Console.WriteLine("[DEBUG] OSBase is null, cannot send message.");
        }
    }

    return HookResult.Continue;
}

}