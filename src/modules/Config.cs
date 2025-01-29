using System;
using System.IO;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Events;
using System.Diagnostics.Tracing;
using System.Collections.Generic;
using System.Reflection;

namespace OSBase.Modules;

public class Config {
    public string ModuleName = "config";
    private readonly OSBase osbase;
    private readonly string configDirectory;
    private readonly string globalConfigPath;
    private readonly Dictionary<string, string> globalConfig = new();

    public Config(OSBase osbase) {
        this.osbase = osbase;

        // Define configuration directory and global config path
        configDirectory = Path.Combine(osbase.ModuleDirectory, "../../configs/plugins/OSBase");
        globalConfigPath = Path.Combine(configDirectory, "OSBase.cfg");

        // Ensure the configuration directory exists
        if (!Directory.Exists(configDirectory)) {
            Directory.CreateDirectory(configDirectory);
            Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Configuration directory created at {configDirectory}");
        }

        // Load or create global configuration
        LoadGlobalConfig();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    /************************************************************************
        GLOBAL CONFIGURATION MANAGEMENT
    ************************************************************************/

    private void LoadGlobalConfig() {
        if (!File.Exists(globalConfigPath)) {
            Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Global config not found. Creating an empty config.");
            File.WriteAllText(globalConfigPath, "// OSBase.cfg\n// Global configuration for OSBase plugin\n");
        }

        foreach (var line in File.ReadLines(globalConfigPath)) {
            string trimmedLine = line.Trim();
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//")) continue;

            var parts = trimmedLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) {
                globalConfig[parts[0]] = parts[1];
                Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Loaded global config: {parts[0]} = {parts[1]}");
            }
        }
    }

    public void RegisterGlobalConfigValue(string key, string defaultValue) {
        if (!globalConfig.ContainsKey(key)) {
            globalConfig[key] = defaultValue;
            SaveGlobalConfig();
            Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Added missing global config key: {key} = {defaultValue}");
        }
    }

    public string GetGlobalConfigValue(string key, string defaultValue = "0") {
        return globalConfig.TryGetValue(key, out var value) ? value : defaultValue;
    }

    private void SaveGlobalConfig() {
        var lines = new List<string> {
            "// OSBase.cfg",
            "// Global configuration for OSBase plugin"
        };

        foreach (var kvp in globalConfig) {
            lines.Add($"{kvp.Key} {kvp.Value}");
        }

        File.WriteAllLines(globalConfigPath, lines);
        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Global configuration saved to {globalConfigPath}");
    }

    /************************************************************************
        CUSTOM CONFIGURATION MANAGEMENT
    ************************************************************************/

    public void CreateCustomConfig(string fileName, string defaultContent) {
        string filePath = Path.Combine(configDirectory, fileName);

        if (!File.Exists(filePath)) {
            File.WriteAllText(filePath, defaultContent);
            Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Created custom configuration file: {filePath}");
        } else {
            Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Custom configuration file already exists: {filePath}");
        }
    }

    public void ExecuteCustomConfig(string fileName) {
        string filePath = Path.Combine(configDirectory, fileName);

        if (File.Exists(filePath)) {
            foreach (var line in File.ReadLines(filePath)) {
                string trimmedLine = line.Trim();

                // Skip comments and empty lines
                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//")) continue;

                Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Executing command from {fileName}: {trimmedLine}");
                Server.ExecuteCommand(trimmedLine);
            }
        } else {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: Custom configuration file not found: {filePath}");
        }
    }    
    
    public List<string> FetchCustomConfig(string fileName) {
        string filePath = Path.Combine(configDirectory, fileName);

        List<string> lines = new List<string>();

        if (File.Exists(filePath)) {
            foreach (var line in File.ReadLines(filePath)) {
                string trimmedLine = line.Trim();
                lines.Add(trimmedLine);
            }
        } else {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: Custom configuration file not found: {filePath}");
        }
        return lines;
    }
}