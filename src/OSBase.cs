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
    public override string ModuleVersion => "0.0.17";
    public override string ModuleAuthor => "Pintuz";
    public override string ModuleDescription => "Plugin for handling map events with config execution";
    
    private string currentMap = "";
    private bool isWarmup = true;

    private string configPluginPath = "../../configs/plugins/OSBase";

    private Dictionary<string, string> config = new();

    private readonly Dictionary<string, string> defaultConfigValues = new() {
        { "autorecord", "1" },          // Enable demo recording
        { "teamdamage_slaps", "1" }    // Enable slapping on team damage
    };

    public override void Load(bool hotReload) {
        Console.WriteLine("[INFO] OSBase plugin is loading...");
        
        CreateDefaultGlobalConfig ( );
        LoadGlobalConfig();

        RegisterEventHandler<EventRoundStart>(onRoundStart);
        RegisterEventHandler<EventWarmupEnd>(onWarmupEnd);
        RegisterListener<Listeners.OnMapStart>(onMapStart);
        RegisterListener<Listeners.OnMapEnd>(onMapEnd);
        RegisterEventHandler<EventCsWinPanelMatch>(onMatchEndEvent);

        RegisterEventHandler<EventPlayerHurt>(onPlayerHurt);
        RegisterEventHandler<EventPlayerDeath>(onPlayerDeath);

        Console.WriteLine("[INFO] OSBase plugin loaded successfully!");
    }
    

    /************************************************************************
        HANDLE CONFIGS 
    ************************************************************************/

    /* LOAD CONFIG */
    private void LoadGlobalConfig() {
        string configPath = Path.Combine(ModuleDirectory, $"{configPluginPath}/OSBase.cfg");

        try {
            if (!File.Exists(configPath)) {
                Console.WriteLine($"[ERROR] OSBase: Global configuration file not found at: {configPath}");
                return;
            }

            Console.WriteLine($"[INFO] OSBase: Loading global configuration from {configPath}");

            // Read and parse the configuration file
            var lines = File.ReadAllLines(configPath).ToList();
            foreach (var line in lines) {
                string trimmedLine = line.Trim();

                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//")) continue;

                var parts = trimmedLine.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2) {
                    config[parts[0]] = parts[1];
                    Console.WriteLine($"[INFO] OSBase: Config: {parts[0]} = {parts[1]}");
                }
            }

            // Check for missing variables
            foreach (var kvp in defaultConfigValues) {
                if (!config.ContainsKey(kvp.Key)) {
                    Console.WriteLine($"[INFO] OSBase: Adding missing config key: {kvp.Key} = {kvp.Value}");
                    lines.Add($"{kvp.Key} {kvp.Value}");
                    config[kvp.Key] = kvp.Value; // Add to the in-memory config
                }
            }

            // Write updated config back to file if changes were made
            File.WriteAllLines(configPath, lines);
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase: Failed to load global configuration: {ex.Message}");
        }
    }
  
    /* CREATE MISSING CONFIGS */
    private void CreateDefaultGlobalConfig() {
        try {
            // Resolve the base directory for the configuration files
            string configDirectory = Path.Combine(ModuleDirectory, configPluginPath);

            // Ensure the directory exists
            if (!Directory.Exists(configDirectory)) {
                Directory.CreateDirectory(configDirectory);
                Console.WriteLine($"[INFO] OSBase: Configuration directory created at: {configDirectory}");
            }

            // Define the global configuration file path
            string globalConfigPath = Path.Combine(configDirectory, "OSBase.cfg");

            // Create the global configuration file if it doesn't exist
            if (!File.Exists(globalConfigPath)) {
                var defaultGlobalConfig = new List<string> {
                    "// OSBase.cfg",
                    "// Global configuration for OSBase plugin"
                };

                // Populate the default configuration from defaultConfigValues
                foreach (var kvp in defaultConfigValues) {
                    defaultGlobalConfig.Add($"{kvp.Key} {kvp.Value}");
                }

                File.WriteAllLines(globalConfigPath, defaultGlobalConfig);
                Console.WriteLine($"[INFO] OSBase: Default global configuration created at: {globalConfigPath}");
            } else {
                Console.WriteLine($"[INFO] OSBase: Global configuration already exists at: {globalConfigPath}");
            }

            // Create individual configuration files if they don't exist
            CreateStageConfig(Path.Combine(configDirectory, "warmupstart.cfg"), "// Commands for warmup start\nsv_gravity 200\n");
            CreateStageConfig(Path.Combine(configDirectory, "warmupend.cfg"), "// Commands for warmup end\nsv_gravity 800\n");
            CreateStageConfig(Path.Combine(configDirectory, "mapstart.cfg"), "// Commands for map start\n");
            CreateStageConfig(Path.Combine(configDirectory, "mapend.cfg"), "// Commands for map end\n");
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase: Failed to create configuration: {ex.Message}");
        }
    }

    /* CREATE EVENT CONFIGS */
    private void CreateStageConfig(string configPath, string defaultContent) {
        try {
            if (!File.Exists(configPath)) {
                File.WriteAllText(configPath, defaultContent);
                Console.WriteLine($"[INFO] OSBase: Default stage configuration created at: {configPath}");
            } else {
                Console.WriteLine($"[INFO] OSBase: Stage configuration already exists at: {configPath}");
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase: Failed to create stage configuration: {ex.Message}");
        }
    }

    /* EXECUTE EVENT CONFIGS */
    private void ExecuteStageConfig(string configFile) {
        string configPath = Path.Combine(ModuleDirectory, $"{configPluginPath}/{configFile}");
        try {
            if (File.Exists(configPath)) {
                Console.WriteLine($"[INFO] Executing configuration file: {configPath}");
                foreach (var line in File.ReadLines(configPath)) {
                    string trimmedLine = line.Trim();

                    // Skip comments and empty lines
                    if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//")) {
                        continue;
                    }

                    // Execute the command
                    Console.WriteLine($"[INFO] OSBase: Executing command: {trimmedLine}");
                    SendCommand(trimmedLine);
                }
            } else {
                Console.WriteLine($"[ERROR] OSBase: Configuration file not found: {configPath}");
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase: Failed to execute configuration file: {ex.Message}");
        }
    }

    /* GET CONFIG VALUE */
    private string GetConfigValue(string key, string defaultValue) {
        if (config.ContainsKey(key)) {
            return config[key];
        } else {
            Console.WriteLine($"[INFO] OSBase: Config key '{key}' not found. Using default value: {defaultValue}");
            return defaultValue;
        }
    }
    

    /************************************************************************
        HANDLE EVENTS
    ************************************************************************/
    
    /* MAP END */
    private void onMapEnd ( ) {
        isWarmup = true;
        runEndOfMapCommands();
        Console.WriteLine("[INFO] OSBase: MAP END!!");
    }     

    /* MAP START */
    private void onMapStart ( string mapName ) {
        isWarmup = true;
        currentMap = mapName;
        runStartOfMapCommands();
        Console.WriteLine("[INFO] OSBase: MAP START!!");
    }   

    /* WARMUP END */
     private HookResult onWarmupEnd(EventWarmupEnd eventInfo, GameEventInfo gameEventInfo) {
        isWarmup = false;
        Console.WriteLine("[INFO] OSBase: WARMUP ENDED!!");
        runWarmupEndCommands ( );
        return HookResult.Continue;
    }

    /* MATCH END */
    private HookResult onMatchEndEvent(EventCsWinPanelMatch eventInfo, GameEventInfo gameEventInfo) {
        isWarmup = true;
        Console.WriteLine("[INFO] OSBase: WIN PANEL MATCH!!");
        runEndOfMapCommands();
        return HookResult.Continue;
    }

    /* ROUND START */
    private HookResult onRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {
        // Increment the round number
        if (isWarmup) {
            Console.WriteLine("[INFO] OSBase: WARMUP IS ON.");
            // Assume warmup ends after the first round
            isWarmup = false;
            runWarmupCommands();
        }
        return HookResult.Continue;
    }

    /* PLAYER HURT */
    private HookResult onPlayerHurt(EventPlayerHurt eventInfo, GameEventInfo gameEventInfo) {
        var attacker = eventInfo.Attacker;
        var victim = eventInfo.Userid;

        if (attacker != null && 
            victim != null &&
            attacker.Team == victim.Team) {
                SendCommand($"slap {attacker.UserId} {eventInfo.DmgHealth}");
                Console.WriteLine($"say [TD] {attacker.PlayerName} hurt {victim.PlayerName} for {eventInfo.DmgArmor} damage");
        }
        
        return HookResult.Continue;
    }

    /* PLAYER DEATH */  
    private HookResult onPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
        var attacker = eventInfo.Attacker;
        var victim = eventInfo.Userid;

        if (attacker != null && 
            victim != null &&
            attacker.Team == victim.Team) {
                SendCommand($"slap {attacker.UserId} {eventInfo.DmgHealth}");
                Console.WriteLine($"say [TK] {attacker.PlayerName} killed {victim.PlayerName}");
        }
        
        return HookResult.Continue;
    }

    /************************************************************************
        HANDLE COMMANDS
    ************************************************************************/

    /* END OF MAP */
    private void runEndOfMapCommands() {
        Console.WriteLine("[INFO] OSBase: Running end of map commands...");
        ExecuteStageConfig("mapend.cfg");
        if (GetConfigValue("autorecord", "0") == "1") {
            SendCommand("tv_stoprecord");
            SendCommand("tv_enable 0");
            Console.WriteLine("[INFO] OSBase: Autorecord is enabled. Stopped recording demo.");
        }
    }    

    /* START OF MAP */
    private void runStartOfMapCommands() {
        Console.WriteLine("[INFO] OSBase: Running end of map commands...");
        ExecuteStageConfig("mapstart.cfg");
    }

    /* WARMUP */
    private void runWarmupCommands() {
        Console.WriteLine("[INFO] OSBase: Running warmup commands...");
        ExecuteStageConfig("warmupstart.cfg");
    }

    /* WARMUP END */
    private void runWarmupEndCommands() {
        Console.WriteLine("[INFO] OSBase: Running warmup end commands...");

        ExecuteStageConfig("warmupend.cfg");

        // Check if autorecord is enabled
        if (GetConfigValue("autorecord", "0") == "1") {
            var date = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            SendCommand("tv_enable 1");
            SendCommand($"tv_record {date}-{currentMap}.dem");
            Console.WriteLine("[INFO] OSBase: Autorecord is enabled. Started recording demo.");
        } 
    }

    /* SEND COMMANDS */
    private void SendCommand(string command) {
        try {
            Console.WriteLine($"[INFO] OSBase: Sending command: {command}");
            Server.ExecuteCommand(command); // Directly execute the command
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase: Failed to send: {command}, Exception: {ex.Message}");
        }
    }

}