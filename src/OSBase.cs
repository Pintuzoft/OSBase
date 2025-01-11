using System.IO;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;

namespace OSBase;

public class OSBase : BasePlugin {
    public override string ModuleName => "OSBase";
    public override string ModuleVersion => "0.0.5";
    public override string ModuleAuthor => "Pintuz";
    public override string ModuleDescription => "Plugin for handling map events with config execution";
    private readonly string MapStartConfig = ResolveConfigPath("cfg/OSBase/mapstart.cfg");
    private readonly string MapEndConfig = ResolveConfigPath("cfg/OSBase/mapend.cfg");
    private readonly string WarmupStartConfig = ResolveConfigPath("cfg/OSBase/warmupstart.cfg");
    private readonly string WarmupEndConfig = ResolveConfigPath("cfg/OSBase/warmupend.cfg");
    private Timer? warmupPollingTimer;

    public override void Load(bool hotReload) {
        Console.WriteLine("[CRITICAL] OSBase plugin is loading...");
        
        // Register listeners for map events
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        
        EnsureDefaultConfig(MapStartConfig, "// Default mapstart.cfg");
        EnsureDefaultConfig(MapEndConfig, "// Default mapend.cfg");
        EnsureDefaultConfig(WarmupEndConfig, "// Default warmupend.cfg");

        Console.WriteLine("[INFO] OSBase plugin loaded successfully!");
    }

    private void OnMapStart(string mapName) {
        Console.WriteLine($"[INFO] Map '{mapName}' started!");

        // Execute mapstart configuration
        ExecuteConfigSafely("cfg/OSBase/mapstart.cfg");

        // Start warmup polling
        StartWarmupPolling();
        Console.WriteLine("[INFO] Map start logic completed.");
    }

    private void OnMapEnd() {
        Console.WriteLine("[INFO] Map ended!");
        ExecuteConfigSafely("cfg/OSBase/mapend.cfg");
    }

    private void StartWarmupPolling() {
        Console.WriteLine("[DEBUG] Starting warmup polling...");

        warmupPollingTimer = new Timer(state => {
            Console.WriteLine("[DEBUG] Polling warmup state...");
            if (!IsWarmupActive()) {
                Console.WriteLine("[INFO] Warmup has ended!");
                ExecuteConfigSafely("cfg/OSBase/warmupend.cfg");
                StopWarmupPolling();
            }
        }, null, 0, 1000); // Poll every 1 second
    }

    private void StopWarmupPolling() {
        if (warmupPollingTimer != null) {
            warmupPollingTimer.Dispose();
            warmupPollingTimer = null;
            Console.WriteLine("[DEBUG] Warmup polling stopped.");
        }
    }

    private bool IsWarmupActive() {
        Console.WriteLine("[DEBUG] Checking if warmup is active...");
        try {
            var conVar = ConVar.Find("mp_warmup_pausetimer"); // Replace with a valid ConVar for your server
            if (conVar == null) {
                Console.WriteLine("[ERROR] Failed to find ConVar: mp_warmup_pausetimer.");
                return false;
            }

            string value = conVar.StringValue;
            Console.WriteLine($"[DEBUG] ConVar Value: {value}");
            return value == "0"; // Adjust logic based on the meaning of the ConVar's value
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] Exception while checking warmup state: {ex.Message}");
            return false;
        }
    }
    
    private static string ResolveConfigPath(string relativePath) {
        string gameBaseDirectory = GetGameBaseDirectory(); // Adjust this to locate the game's base directory
        return Path.Combine(gameBaseDirectory, relativePath);
    }

    private void ExecuteConfigSafely(string configPath) {
        if (!File.Exists(configPath)) {
            Console.WriteLine($"[ERROR] Config file not found: {configPath}");
            return;
        }

        try {
            Console.WriteLine($"[INFO] Executing config: {configPath}");
            foreach (var line in File.ReadLines(configPath)) {
                // Skip comments and empty lines
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) continue;

                Console.WriteLine($"[DEBUG] Executing command: {line}");
                //Server.ExecuteCommand(line);
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] Failed to execute config: {configPath}, Exception: {ex.Message}");
        }
    }

    private static string GetGameBaseDirectory() {
        string currentDirectory = Directory.GetCurrentDirectory();
        Console.WriteLine($"[DEBUG] Current working directory: {currentDirectory}");

        // Adjust path to navigate to the game's root directory
        string gameBaseDirectory = Path.Combine(currentDirectory, "../../csgo");

        if (!Directory.Exists(gameBaseDirectory)) {
            throw new DirectoryNotFoundException($"[ERROR] Could not locate the game base directory at: {Path.GetFullPath(gameBaseDirectory)}");
        }

        Console.WriteLine($"[DEBUG] Resolved game base directory: {Path.GetFullPath(gameBaseDirectory)}");
        return Path.GetFullPath(gameBaseDirectory);
    }
    private void EnsureDefaultConfig(string configPath, string defaultContent) {
    if (!File.Exists(configPath)) {
        Console.WriteLine($"[DEBUG] Creating default config file: {configPath}");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? string.Empty);
        File.WriteAllText(configPath, defaultContent);
    }
}
}