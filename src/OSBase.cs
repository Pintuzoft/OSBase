using System.IO;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace OSBase;

public class OSBase : BasePlugin {
    public override string ModuleName => "OSBase";
    public override string ModuleVersion => "0.0.5";
    public override string ModuleAuthor => "Pintuz";
    public override string ModuleDescription => "Plugin for handling map events with config execution";

    private readonly string MapStartConfig = ResolveConfigPath("cfg/OSBase/mapstart.cfg");
    private readonly string MapEndConfig = ResolveConfigPath("cfg/OSBase/mapend.cfg");

    private static string GetGameBaseDirectory() {
        // Start from the current working directory
        string currentDirectory = Directory.GetCurrentDirectory();
        Console.WriteLine($"[DEBUG] Current working directory: {currentDirectory}");

        // Navigate up to the csgo directory
        string gameBaseDirectory = Path.GetFullPath(Path.Combine(currentDirectory, "../../csgo"));

        // Verify the directory exists
        if (!Directory.Exists(gameBaseDirectory)) {
            throw new DirectoryNotFoundException($"[ERROR] Could not locate the 'csgo' directory at: {gameBaseDirectory}");
        }

        Console.WriteLine($"[DEBUG] Resolved game base directory: {gameBaseDirectory}");
        return gameBaseDirectory; // Already normalized by Path.GetFullPath
    }

    private static string ResolveConfigPath(string relativePath) {
        string gameBaseDirectory = GetGameBaseDirectory();
        return Path.Combine(gameBaseDirectory, relativePath);
    }

    private void EnsureDefaultConfig(string configPath, string defaultContent) {
        string? directory = Path.GetDirectoryName(configPath);

        if (!string.IsNullOrEmpty(directory)) {
            Console.WriteLine($"[DEBUG] Ensuring directory exists: {Path.GetFullPath(directory)}");
            try {
                Directory.CreateDirectory(directory);
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] Failed to create directory: {directory}, Exception: {ex.Message}");
            }
        } else {
            Console.WriteLine($"[ERROR] Invalid directory path for config: {configPath}");
            return;
        }

        if (!File.Exists(configPath)) {
            Console.WriteLine($"[DEBUG] Creating config file: {Path.GetFullPath(configPath)}");
            try {
                File.WriteAllText(configPath, defaultContent);
                Console.WriteLine($"[INFO] Default config created: {Path.GetFullPath(configPath)}");
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] Failed to create file: {configPath}, Exception: {ex.Message}");
            }
        } else {
            Console.WriteLine($"[INFO] Config file already exists: {Path.GetFullPath(configPath)}");
        }
    }

    public override void Load(bool hotReload) {
        Console.WriteLine("[DEBUG] OSBase is loading...");

        EnsureDefaultConfig(MapStartConfig, "// Default mapstart.cfg\n");
        EnsureDefaultConfig(MapEndConfig, "// Default mapend.cfg\n");

        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);

        Console.WriteLine("[INFO] OSBase loaded!");
    }

    private void OnMapStart(string mapName) {
        Console.WriteLine($"Map '{mapName}' started! Executing {MapStartConfig}...");
        ExecuteConfigSafely(MapStartConfig);
    }

    private void OnMapEnd() {
        Console.WriteLine($"Map ended! Executing {MapEndConfig}...");
        ExecuteConfigSafely(MapEndConfig);
    }

    private void ExecuteConfigSafely(string configPath) {
        if (!File.Exists(configPath)) {
            Console.WriteLine($"[ERROR] Config file not found: {configPath}");
            return;
        }

        try {
            Console.WriteLine($"[INFO] Executing config: {configPath}");
            Server.ExecuteCommand($"exec {configPath}");
            Console.WriteLine($"[INFO] Successfully executed config: {configPath}");
            
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] Failed to execute config: {configPath}, Exception: {ex.Message}");
        }
    }
}