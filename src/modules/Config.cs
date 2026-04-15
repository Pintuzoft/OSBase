using System;
using System.Collections.Generic;
using System.IO;
using CounterStrikeSharp.API;

namespace OSBase.Modules;

public class Config {
    public string ModuleName => "config";

    private readonly OSBase osbase;
    private readonly string configDirectory;
    private readonly string globalConfigPath;
    private readonly Dictionary<string, string> globalConfig = new(StringComparer.OrdinalIgnoreCase);

    public Config(OSBase osbase) {
        this.osbase = osbase;

        configDirectory = Path.Combine(osbase.ModuleDirectory, "../../configs/plugins/OSBase");
        globalConfigPath = Path.Combine(configDirectory, "OSBase.cfg");

        EnsureConfigDirectory();
        ReloadGlobalConfig();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: loaded successfully!");
    }

    public string GetConfigDirectory() {
        return configDirectory;
    }

    public void ReloadGlobalConfig() {
        globalConfig.Clear();

        if (!File.Exists(globalConfigPath)) {
            Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Global config not found. Creating default config.");
            File.WriteAllLines(globalConfigPath, new[] {
                "// OSBase.cfg",
                "// Global configuration for OSBase plugin"
            });
        }

        foreach (var rawLine in File.ReadLines(globalConfigPath)) {
            var line = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) {
                continue;
            }

            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) {
                Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Invalid config line skipped: {line}");
                continue;
            }

            var key = parts[0].Trim();
            var value = parts[1].Trim();

            globalConfig[key] = value;
            Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Loaded global config: {key} = {value}");
        }
    }

    public void RegisterGlobalConfigValue(string key, string defaultValue) {
        if (globalConfig.ContainsKey(key)) {
            return;
        }

        globalConfig[key] = defaultValue;
        SaveGlobalConfig();

        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Added missing global config key: {key} = {defaultValue}");
    }

    public string GetGlobalConfigValue(string key, string defaultValue = "0") {
        return globalConfig.TryGetValue(key, out var value) ? value : defaultValue;
    }

    public int GetGlobalConfigInt(string key, int defaultValue = 0) {
        var value = GetGlobalConfigValue(key, defaultValue.ToString());

        if (int.TryParse(value, out var parsed)) {
            return parsed;
        }

        Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Invalid integer for key '{key}': {value}. Using default {defaultValue}");
        return defaultValue;
    }

    public bool IsModuleEnabled(string moduleName) {
        return GetGlobalConfigInt(moduleName, 0) == 1;
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

    public void CreateCustomConfig(string fileName, string defaultContent) {
        var filePath = Path.Combine(configDirectory, fileName);

        if (File.Exists(filePath)) {
            Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Custom configuration file already exists: {filePath}");
            return;
        }

        File.WriteAllText(filePath, defaultContent);
        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Created custom configuration file: {filePath}");
    }

    public void ExecuteCustomConfig(string fileName) {
        var filePath = Path.Combine(configDirectory, fileName);

        if (!File.Exists(filePath)) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: Custom configuration file not found: {filePath}");
            return;
        }

        foreach (var rawLine in File.ReadLines(filePath)) {
            var line = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) {
                continue;
            }

            Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Executing command from {fileName}: {line}");
            Server.ExecuteCommand(line);
        }
    }

    public List<string> FetchCustomConfig(string fileName) {
        var filePath = Path.Combine(configDirectory, fileName);
        var lines = new List<string>();

        if (!File.Exists(filePath)) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: Custom configuration file not found: {filePath}");
            return lines;
        }

        foreach (var rawLine in File.ReadLines(filePath)) {
            lines.Add(rawLine.Trim());
        }

        return lines;
    }

    public void AddCustomConfigLine(string fileName, string line) {
        var filePath = Path.Combine(configDirectory, fileName);

        if (!File.Exists(filePath)) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: Custom configuration file not found: {filePath}");
            return;
        }

        File.AppendAllText(filePath, line + Environment.NewLine);
        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Added line to custom configuration file: {fileName}: {line}");
    }

    private void EnsureConfigDirectory() {
        if (Directory.Exists(configDirectory)) {
            return;
        }

        Directory.CreateDirectory(configDirectory);
        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Configuration directory created at {configDirectory}");
    }
}