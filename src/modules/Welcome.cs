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

public class Welcome : IModule {
    public string ModuleName => "welcome";   
    private OSBase? osbase;
    private Config? config;

    float delay = 5.0f;

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        // Register required global config values
        config.RegisterGlobalConfigValue($"{ModuleName}", "1");

        if (osbase == null) {
            Console.WriteLine($"[ERROR] OSBase is null. {ModuleName} failed to load.");
            return;
        } else if (config == null) {
            Console.WriteLine($"[ERROR] ConfigModule is null. {ModuleName} failed to load.");
            return;
        }

        if (config?.GetGlobalConfigValue($"{ModuleName}", "0") == "1") {
            createCustomConfigs();
            loadEventHandlers();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
        } else {
            Console.WriteLine($"[DEBUG] {ModuleName} is disabled in the global configuration.");
        }
    }

    private void createCustomConfigs() {
        if (config == null) return;
        config.CreateCustomConfig("welcome.cfg", "// Welcome message\n// Example: Welcome to the server!\n");
    }

    private void loadEventHandlers() {
        if(osbase == null) return;
        osbase.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull eventInfo, GameEventInfo gameEventInfo) {
        if (config == null) {
            Console.WriteLine("[DEBUG] OSBase[{ModuleName}]: Config module is null, skipping welcome message.");
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
                osbase.AddTimer(delay, () => {
                    foreach (string message in messages) {
                        if (playerId == null) break; // Ensure playerId is valid
                        if (message.StartsWith("//") || string.IsNullOrWhiteSpace(message)) {
                            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Skipping line: {message}");
                            continue;
                        }
                        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Sending message to Player: {playerId.PlayerName ?? "Unknown"}");
                        playerId.PrintToChat(message);
                    }
                });
            } else {
                Console.WriteLine("[DEBUG] OSBase[{ModuleName}]: Player ID is null, cannot send message.");
            }
        } else {
            Console.WriteLine("[DEBUG] OSBase[{ModuleName}]: osbase is null, cannot send message.");
        }
        return HookResult.Continue;
    }

}