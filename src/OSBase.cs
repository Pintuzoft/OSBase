using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using OSBase.Modules;
using static CounterStrikeSharp.API.Core.Listeners;

namespace OSBase;

public class OSBase : BasePlugin {
    public override string ModuleName => "OSBase";
    public override string ModuleVersion => "0.0.489";
    public override string ModuleAuthor => "Pintuz";
    public override string ModuleDescription => "Plugin for managing CS2 servers";

    public string currentMap = string.Empty;
    public string CurrentMap => currentMap;

    private Config? config;
    private GameStats? gameStats;

    private readonly Dictionary<string, Type> discoveredModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IModule> loadedModules = new(StringComparer.OrdinalIgnoreCase);

    public override void Load(bool hotReload) {
        Console.WriteLine("[INFO] OSBase: plugin is loading...");

        currentMap = Server.MapName ?? string.Empty;

        config = new Config(this);
        gameStats = new GameStats(this, config);

        DiscoverModules();
        ReloadAllConfigsAndModules();

        RegisterListener<OnMapStart>(HandleMapStart);

        Console.WriteLine("[INFO] OSBase: plugin loaded successfully!");
    }

    public override void Unload(bool hotReload) {
        UnloadAllModules();
        loadedModules.Clear();
        discoveredModules.Clear();

        Console.WriteLine("[INFO] OSBase: plugin unloaded.");
    }

    private void HandleMapStart(string mapName) {
        currentMap = mapName;

        Console.WriteLine($"[INFO] OSBase: map started: {mapName}, reloading config and modules...");
        ReloadAllConfigsAndModules();
    }

    private void DiscoverModules() {
        discoveredModules.Clear();

        var moduleTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => typeof(IModule).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .OrderBy(t => t.Name);

        foreach (var moduleType in moduleTypes) {
            try {
                var module = (IModule)Activator.CreateInstance(moduleType)!;

                if (discoveredModules.ContainsKey(module.ModuleName)) {
                    Console.WriteLine($"[ERROR] OSBase: Duplicate module name detected: {module.ModuleName}");
                    continue;
                }

                discoveredModules[module.ModuleName] = moduleType;
                Console.WriteLine($"[DEBUG] OSBase: Module discovered: {module.ModuleName}");
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] OSBase: Failed to discover module {moduleType.Name}. Exception: {ex.Message}");
            }
        }
    }

    public void ReloadAllConfigsAndModules() {
        if (config == null) {
            config = new Config(this);
        } else {
            config.ReloadGlobalConfig();
        }

        gameStats?.ReloadConfig(config);

        foreach (var kvp in discoveredModules) {
            var moduleName = kvp.Key;
            var moduleType = kvp.Value;
            var shouldBeEnabled = config.IsModuleEnabled(moduleName);
            var isLoaded = loadedModules.ContainsKey(moduleName);

            if (shouldBeEnabled && !isLoaded) {
                TryLoadModule(moduleName, moduleType);
                continue;
            }

            if (!shouldBeEnabled && isLoaded) {
                TryUnloadModule(moduleName);
                continue;
            }

            if (shouldBeEnabled && isLoaded) {
                TryReloadModuleConfig(moduleName);
            }
        }
    }

    private void TryLoadModule(string moduleName, Type moduleType) {
        try {
            var module = (IModule)Activator.CreateInstance(moduleType)!;
            module.Load(this, config!);
            loadedModules[moduleName] = module;

            Console.WriteLine($"[INFO] OSBase: Loaded module: {moduleName}");
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase: Failed to load module {moduleName}. Exception: {ex.Message}");
        }
    }

    private void TryUnloadModule(string moduleName) {
        if (!loadedModules.TryGetValue(moduleName, out var module)) {
            return;
        }

        try {
            module.Unload();
            loadedModules.Remove(moduleName);

            Console.WriteLine($"[INFO] OSBase: Unloaded module: {moduleName}");
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase: Failed to unload module {moduleName}. Exception: {ex.Message}");
        }
    }

    private void TryReloadModuleConfig(string moduleName) {
        if (!loadedModules.TryGetValue(moduleName, out var module)) {
            return;
        }

        try {
            module.ReloadConfig(config!);
            Console.WriteLine($"[INFO] OSBase: Reloaded config for module: {moduleName}");
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase: Failed to reload config for module {moduleName}. Exception: {ex.Message}");
        }
    }

    private void UnloadAllModules() {
        foreach (var moduleName in loadedModules.Keys.ToList()) {
            TryUnloadModule(moduleName);
        }
    }

    public void SendCommand(string command) {
        try {
            Console.WriteLine($"[INFO] OSBase: Sending command: {command}");
            Server.ExecuteCommand(command);
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase: Failed to send: {command}, Exception: {ex.Message}");
        }
    }

    public GameStats? GetGameStats() {
        return gameStats;
    }

    public T? GetModule<T>() where T : class, IModule {
        return loadedModules.Values.OfType<T>().FirstOrDefault();
    }

    public TeamBalancer? GetTeamBalancer() {
        return GetModule<TeamBalancer>();
    }

    public bool IsModuleLoaded(string moduleName) {
        return loadedModules.ContainsKey(moduleName);
    }
}