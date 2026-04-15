using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;

namespace OSBase.Modules;

public class Welcome : IModule {
    public string ModuleName => "welcome";

    private const string WelcomeConfigFile = "welcome.cfg";

    private OSBase? osbase;
    private Config? config;

    private bool handlersLoaded = false;
    private bool isActive = false;

    private float delay = 5.0f;

    private readonly List<CounterStrikeSharp.API.Modules.Timers.Timer> pendingWelcomeTimers = new();

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;
        isActive = true;

        if (osbase == null || config == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] load failed (null deps).");
            isActive = false;
            return;
        }

        config.RegisterGlobalConfigValue(ModuleName, "1");
        config.RegisterGlobalConfigValue($"{ModuleName}_delay", "5.0");

        if (config.GetGlobalConfigValue(ModuleName, "0") != "1") {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in config.");
            isActive = false;
            return;
        }

        CreateCustomConfigs();
        LoadConfig();
        LoadHandlers();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    public void Unload() {
        isActive = false;

        KillPendingWelcomeTimers();

        if (osbase != null && handlersLoaded) {
            osbase.DeregisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
            handlersLoaded = false;
        }

        config = null;
        osbase = null;

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] unloaded.");
    }

    public void ReloadConfig(Config inConfig) {
        config = inConfig;
        LoadConfig();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] config reloaded.");
    }

    private void LoadHandlers() {
        if (osbase == null || handlersLoaded) {
            return;
        }

        osbase.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        handlersLoaded = true;
    }

    private void LoadConfig() {
        if (config == null) {
            return;
        }

        delay = 5.0f;
        string rawDelay = config.GetGlobalConfigValue($"{ModuleName}_delay", "5.0");

        if (!float.TryParse(rawDelay, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out delay)) {
            delay = 5.0f;
            Console.WriteLine($"[WARN] OSBase[{ModuleName}] Invalid {ModuleName}_delay '{rawDelay}', using {delay:0.0}");
        }

        if (delay < 0.0f) {
            delay = 0.0f;
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] config loaded. delay={delay:0.0}");
    }

    private void CreateCustomConfigs() {
        config?.CreateCustomConfig(
            WelcomeConfigFile,
            "// Welcome message\n// Example: Welcome to the server!\n"
        );
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive || osbase == null || config == null) {
            return HookResult.Continue;
        }

        var player = eventInfo.Userid;
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) {
            return HookResult.Continue;
        }

        List<string> messages = GetWelcomeMessages();
        if (messages.Count == 0) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] No welcome messages to send.");
            return HookResult.Continue;
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Scheduling welcome message for {player.PlayerName ?? "Unknown"} in {delay:0.0}s");

        CounterStrikeSharp.API.Modules.Timers.Timer? timer = null;
        timer = osbase.AddTimer(delay, () => {
            try {
                if (!isActive) {
                    return;
                }

                if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) {
                    return;
                }

                foreach (string message in messages) {
                    if (string.IsNullOrWhiteSpace(message) || message.StartsWith("//")) {
                        continue;
                    }

                    player.PrintToChat(message);
                }

                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Welcome messages sent to {player.PlayerName ?? "Unknown"}");
            } finally {
                if (timer != null) {
                    pendingWelcomeTimers.Remove(timer);
                }
            }
        });

        pendingWelcomeTimers.Add(timer);
        return HookResult.Continue;
    }

    private List<string> GetWelcomeMessages() {
        if (config == null) {
            return new List<string>();
        }

        List<string> rawLines = config.FetchCustomConfig(WelcomeConfigFile);
        List<string> messages = new();

        foreach (string line in rawLines) {
            string trimmed = line.Trim();

            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//")) {
                continue;
            }

            messages.Add(trimmed);
        }

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Loaded {messages.Count} welcome message lines.");
        return messages;
    }

    private void KillPendingWelcomeTimers() {
        foreach (var timer in pendingWelcomeTimers) {
            try {
                timer.Kill();
            } catch {
            }
        }

        pendingWelcomeTimers.Clear();
    }
}