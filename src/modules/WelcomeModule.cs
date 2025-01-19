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

        // Create custom config files
        config.CreateCustomConfig("welcome.cfg", "// Welcome message\n// Example: Welcome to the server!\n");

        // Register event handlers and listeners
        osbase.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");

    }


    private HookResult OnPlayerConnectFull(EventPlayerConnectFull eventInfo, GameEventInfo gameEventInfo) {
        if (config == null) {
            Console.WriteLine("[DEBUG] OSBase[{ModuleName}]: Config module is null, skipping welcome message.");
            return HookResult.Continue;
        }
        
        // Check if welcome messages are enabled
        string welcomeMsgEnabled = config.GetGlobalConfigValue("welcome_msg", "0");
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Welcome messages enabled: {welcomeMsgEnabled}");

        if (welcomeMsgEnabled != "1") {
            Console.WriteLine("[DEBUG] OSBase[{ModuleName}]: Welcome messages are disabled, skipping.");
            return HookResult.Continue;
        }

        // Get the player's UserId
        var playerId = eventInfo.Userid;
        
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Sending welcome message to Player ID: {playerId}");

        // Fetch the welcome messages from the configuration file
        List<string> messages = config.FetchCustomConfig("welcome.cfg");
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Fetched {messages.Count} lines from welcome.cfg");

        // Send the message specifically to the connecting player
        if (osbase != null) {
            if (playerId != null && playerId.IsValid && !playerId.IsBot && !playerId.IsHLTV) {
                SendMessagesDelayed(3, playerId, messages);
            } else {
                Console.WriteLine("[DEBUG] OSBase[{ModuleName}]: Player ID is null, cannot send message.");
            }
        } else {
            Console.WriteLine("[DEBUG] OSBase[{ModuleName}]: osbase is null, cannot send message.");
        }

        return HookResult.Continue;
    }


    private void SendMessagesDelayed(int delaySeconds, dynamic playerId, List<string> messages) {
        // Create a timer to execute the task after the delay
        System.Threading.Timer? timer = null;
        timer = new System.Threading.Timer(_ => {
            // Loop through the messages and send them to the player
            foreach (string message in messages) {
                if (playerId == null) break; // Ensure playerId is valid
                if (message.StartsWith("//") || string.IsNullOrWhiteSpace(message)) {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Skipping line: {message}");
                    continue;
                }
                Console.WriteLine($"[DEBUG] Sending message to Player: {playerId.PlayerName ?? "Unknown"}");
                playerId.PrintToChat(message);
            }

            // Dispose of the timer after execution
            timer?.Dispose();
        }, null, delaySeconds * 1000, System.Threading.Timeout.Infinite);
    }

}