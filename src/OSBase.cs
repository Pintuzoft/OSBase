using System.IO;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Events;
using System.Diagnostics.Tracing;

namespace OSBase;

public class OSBase : BasePlugin {
    public override string ModuleName => "OSBase";
    public override string ModuleVersion => "0.0.12";
    public override string ModuleAuthor => "Pintuz";
    public override string ModuleDescription => "Plugin for handling map events with config execution";
    
    private string currentMap = "";
    private bool isWarmup = true;

    private string configPluginPath = "../../configs/plugins/OSBase";

    private Dictionary<string, string> config = new();

    public override void Load(bool hotReload) {
        Console.WriteLine("[INFO] OSBase plugin is loading...");
        
        CreateDefaultGlobalConfig ( );
        
        LoadGlobalConfig();

        // Register listeners for round events

        RegisterEventHandler<EventRoundStart>(onRoundStart);
        RegisterEventHandler<EventWarmupEnd>(onWarmupEnd);

        RegisterListener<Listeners.OnMapStart>(onMapStart);
        RegisterListener<Listeners.OnMapEnd>(onMapEnd);
        RegisterEventHandler<EventCsWinPanelMatch>(onMatchEndEvent);

        Console.WriteLine("[INFO] OSBase plugin loaded successfully!");
    }
    

private void LoadGlobalConfig() {
    try {
        // Resolve the path to the global configuration file
        string configPath = Path.Combine(ModuleDirectory, "{configPluginPath}/OSBase.cfg");

        // Ensure the global configuration file exists
        if (!File.Exists(configPath)) {
            Console.WriteLine($"[ERROR] Global configuration file not found at: {configPath}");
            return; // Exit if the file is missing
        }

        // Read the global configuration file
        Console.WriteLine($"[INFO] Loading global configuration from {configPath}");
        foreach (var line in File.ReadLines(configPath)) {
            string trimmedLine = line.Trim();

            // Skip comments and empty lines
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//")) continue;

            // Parse key-value pairs
            var parts = trimmedLine.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) {
                config[parts[0]] = parts[1];
                Console.WriteLine($"[INFO] Config: {parts[0]} = {parts[1]}");
            }
        }
    } catch (Exception ex) {
        Console.WriteLine($"[ERROR] Failed to load global configuration: {ex.Message}");
    }
}
  
    private void CreateDefaultGlobalConfig ( ) {
        try {
            // Resolve the base directory for the configuration files
            string configDirectory = Path.Combine(ModuleDirectory, configPluginPath);


            // Ensure the directory exists
            if (!Directory.Exists(configDirectory)) {
                Directory.CreateDirectory(configDirectory);
                Console.WriteLine($"[INFO] Configuration directory created at: {configDirectory}");
            }

            // Define the global configuration file path
            string globalConfigPath = Path.Combine(configDirectory, "OSBase.cfg");

            // Create the global configuration file if it doesn't exist
            if (!File.Exists(globalConfigPath)) {
                var defaultGlobalConfig = new[] {
                    "// OSBase.cfg",
                    "// Global configuration for OSBase plugin",
                    "autorecord 1" // Enable demo recording
                };

                File.WriteAllLines(globalConfigPath, defaultGlobalConfig);
                Console.WriteLine($"[INFO] Default global configuration created at: {globalConfigPath}");
            } else {
                Console.WriteLine($"[INFO] Global configuration already exists at: {globalConfigPath}");
            }

            // Create individual configuration files if they don't exist
            CreateStageConfig(Path.Combine(configDirectory, "warmupstart.cfg"), "// Commands for warmup start\nsv_gravity 200\n");
            CreateStageConfig(Path.Combine(configDirectory, "warmupend.cfg"), "// Commands for warmup end\nsv_gravity 800\n");
            CreateStageConfig(Path.Combine(configDirectory, "mapstart.cfg"), "// Commands for map start\n");
            CreateStageConfig(Path.Combine(configDirectory, "mapend.cfg"), "// Commands for map end\ntv_stoprecord\n");
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] Failed to create configuration: {ex.Message}");
        }
    }
    private void CreateStageConfig(string configPath, string defaultContent) {
        try {
            if (!File.Exists(configPath)) {
                File.WriteAllText(configPath, defaultContent);
                Console.WriteLine($"[INFO] Default stage configuration created at: {configPath}");
            } else {
                Console.WriteLine($"[INFO] Stage configuration already exists at: {configPath}");
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] Failed to create stage configuration: {ex.Message}");
        }
    }

    private void onMapEnd ( ) {
        isWarmup = true;
        runEndOfMapCommands();
        Console.WriteLine("[INFO] OSBase: MAP END!!");
    }     
    private void onMapStart ( string mapName ) {
        isWarmup = true;
        currentMap = mapName;
        runStartOfMapCommands();
        Console.WriteLine("[INFO] OSBase: MAP START!!");
    }   
     private HookResult onWarmupEnd(EventWarmupEnd eventInfo, GameEventInfo gameEventInfo) {
        isWarmup = false;
        Console.WriteLine("[INFO] OSBase: WARMUP ENDED!!");
        runWarmupEndCommands ( );
        return HookResult.Continue;
    }
    private HookResult onMatchEndEvent(EventCsWinPanelMatch eventInfo, GameEventInfo gameEventInfo) {
        isWarmup = true;
        Console.WriteLine("[INFO] OSBase: WIN PANEL MATCH!!");
        runEndOfMapCommands();
        return HookResult.Continue;
    }

    private HookResult onRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {
        // Increment the round number
        Console.WriteLine($"[INFO] OSBase: Round started!");

        if (isWarmup) {
            Console.WriteLine("[INFO] OSBase: WARMUP IS ON.");
            // Assume warmup ends after the first round
            isWarmup = false;
            runWarmupCommands();
        } else {
            Console.WriteLine("[INFO] OSBase: Warmup has ended. This is a live round.");
        }
        return HookResult.Continue;
    }

    private void SendCommand(string command) {
        try {
            Console.WriteLine($"[INFO] OSBase: Sending command: {command}");
            Server.ExecuteCommand(command); // Directly execute the command
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase: Failed to send: {command}, Exception: {ex.Message}");
        }
    }

    private void runEndOfMapCommands() {
        Console.WriteLine("[INFO] OSBase: Running end of map commands...");
        ExecuteStageConfig("mapend.cfg");
    }    
    private void runStartOfMapCommands() {
        Console.WriteLine("[INFO] OSBase: Running end of map commands...");
        ExecuteStageConfig("mapstart.cfg");
    }
    private void runWarmupCommands() {
        Console.WriteLine("[INFO] OSBase: Running warmup commands...");
        ExecuteStageConfig("warmupstart.cfg");
    }

    private void runWarmupEndCommands() {
        Console.WriteLine("[INFO] OSBase: Running warmup end commands...");

        ExecuteStageConfig("warmupend.cfg");

        // Check if autorecord is enabled
        if (GetConfigValue("autorecord", "0") == "1") {
            var date = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            SendCommand("tv_enable 1");
            SendCommand($"tv_record {date}-{currentMap}.dem");
            Console.WriteLine("[INFO] Autorecord is enabled. Started recording demo.");
        } else {
            Console.WriteLine("[INFO] Autorecord is disabled. Skipping demo recording.");
        }
    }

    private void ExecuteStageConfig(string configName) {
        string configPath = Path.Combine(ModuleDirectory, $"{configPluginPath}/{configName}");
        if (File.Exists(configPath)) {
            Console.WriteLine($"[INFO] Executing configuration: {configPath}");
            SendCommand($"exec {configPath}");
        } else {
            Console.WriteLine($"[WARNING] Configuration file not found: {configPath}");
        }
    }
    private string GetConfigValue(string key, string defaultValue) {
        if (config.ContainsKey(key)) {
            return config[key];
        } else {
            Console.WriteLine($"[INFO] Config key '{key}' not found. Using default value: {defaultValue}");
            return defaultValue;
        }
    }
}