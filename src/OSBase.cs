using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using OSBase.Modules;

namespace OSBase;

public class OSBase : BasePlugin {
    public override string ModuleName => "OSBase";
    public override string ModuleVersion => "0.0.470";
    public override string ModuleAuthor => "Pintuz";
    public override string ModuleDescription => "Plugin for managing CS2 servers";

    public string currentMap = "";

    private Config? config;
    private GameStats? gameStats;
    private readonly List<IModule> loadedModules = new();

    public override void Load(bool hotReload) {
        Console.WriteLine("[INFO] OSBase: plugin is loading...");

        config = new Config(this);
        gameStats = new GameStats(this, config);

        if (config == null) {
            Console.WriteLine("[ERROR] OSBase: Failed to load configuration module.");
            return;
        }

        DiscoverAndLoadModules();

        Console.WriteLine("[INFO] OSBase plugin loaded successfully!");
    }

    private void DiscoverAndLoadModules() {
        var moduleTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => typeof(IModule).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .OrderBy(t => t.Name);

        foreach (var moduleType in moduleTypes) {
            try {
                var module = (IModule)Activator.CreateInstance(moduleType)!;
                module.Load(this, config!);
                loadedModules.Add(module);

                Console.WriteLine($"[DEBUG] OSBase: Module instantiated: {module.ModuleName}");
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] Failed to load module: {moduleType.Name}. Exception: {ex.Message}");
            }
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
        return loadedModules.OfType<T>().FirstOrDefault();
    }

    public TeamBalancer? GetTeamBalancer() {
        return GetModule<TeamBalancer>();
    }

    public bool isModuleLoaded(string moduleName) {
        return loadedModules.Any(m => string.Equals(m.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase));
    }
}