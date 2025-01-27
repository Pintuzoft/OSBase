using System;
using System.IO;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Events;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using OSBase.Modules;

namespace OSBase;

public class OSBase : BasePlugin {
    public override string ModuleName => "OSBase";
    public override string ModuleVersion => "0.0.114";
    public override string ModuleAuthor => "Pintuz";
    public override string ModuleDescription => "Plugin for handling map events with config execution";
    
    public string currentMap = "";
    private ConfigModule? config;
    private readonly List<IModule> loadedModules = new();
    public override void Load(bool hotReload) {
        Console.WriteLine("[INFO] OSBase: plugin is loading...");

        // Load the configuration module
        config = new ConfigModule(this);

        if ( config == null ) {
            Console.WriteLine("[ERROR] OSBase: Failed to load configuration module.");
            return;
        }

        // Dynamically discover and load modules
        DiscoverAndLoadModules();

        Console.WriteLine("[INFO] OSBase plugin loaded successfully!");
    }
    private void DiscoverAndLoadModules() {
        var moduleTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => typeof(IModule).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        foreach (var moduleType in moduleTypes) {
            try {
                var module = (IModule)Activator.CreateInstance(moduleType)!;
                module.Load(this, config!);
                Console.WriteLine($"[INFO] Loaded module: {module.ModuleName}");
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] Failed to load module: {moduleType.Name}. Exception: {ex.Message}");
            }
        }
    }

    /* SEND COMMANDS */
    public void SendCommand(string command) {
        try {
            Console.WriteLine($"[INFO] OSBase: Sending command: {command}");
            Server.ExecuteCommand(command); // Directly execute the command
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase: Failed to send: {command}, Exception: {ex.Message}");
        }
    }

}