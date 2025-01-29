using System;
using System.IO;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Events;
using System.Diagnostics.Tracing;
using System.Reflection;

namespace OSBase.Modules;

using System.IO;

public class Demos : IModule {
    public string ModuleName => "demos";   
     private OSBase? osbase;
    private Config? config;

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        // Register required global config values
        config.RegisterGlobalConfigValue($"{ModuleName}", "1");

        if (osbase == null) {
            Console.WriteLine($"[ERROR] OSBase is null. {ModuleName} failed to load.");
            return;
        } else if (config == null) {
            Console.WriteLine($"[ERROR] ConfigModule is null. {ModuleName} failed to load.");
            return;
        }

        if (config?.GetGlobalConfigValue($"{ModuleName}", "0") == "1") {
            loadEventHandlers();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
        } else {
            Console.WriteLine($"[DEBUG] {ModuleName} is disabled in the global configuration.");
        }
    }

    private void loadEventHandlers() {
        if(osbase == null) return;
        osbase.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        osbase.RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
        osbase.RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEndEvent);
    }

    /*
        EVENT HANDLERS
    */

    private void OnMapEnd() {
        runMapEnd();
    }

    private HookResult OnWarmupEnd(EventWarmupEnd eventInfo, GameEventInfo gameEventInfo) {
        runWarmupEnd(); 
        return HookResult.Continue;
    }

    private HookResult OnMatchEndEvent(EventCsWinPanelMatch eventInfo, GameEventInfo gameEventInfo) {
        runMapEnd();
        return HookResult.Continue;
    }


    /*
        METHODS
    */

    private void runMapEnd() {
        osbase?.SendCommand("tv_stoprecord");
        osbase?.SendCommand("tv_enable 0");
        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Autorecord is enabled. Stopped recording demo.");
    }
    private void runWarmupEnd() {
        string date = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Server.ExecuteCommand("tv_enable 1");
        if (osbase != null) {
            Server.ExecuteCommand($"tv_record {date}-{osbase.currentMap}.dem");
        }
        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Autorecord enabled. Demo recording started.");
    }
}