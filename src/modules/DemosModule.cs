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

public class DemosModule : IModule {
    public string ModuleName => "DemosModule";   
     private OSBase? osbase;
    private ConfigModule? config;

    public void Load(OSBase inOsbase, ConfigModule inConfig) {
        osbase = inOsbase;
        config = inConfig;

        // Register required global config values
        config.RegisterGlobalConfigValue("autorecord", "1");

        // Register event handlers and listeners
        osbase.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        osbase.RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
        osbase.RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEndEvent);

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    private void OnMapEnd() {
        if (config?.GetGlobalConfigValue("autorecord", "0") == "1") {
            osbase?.SendCommand("tv_stoprecord");
            osbase?.SendCommand("tv_enable 0");
            Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Autorecord is enabled. Stopped recording demo.");
        }
    }

    private HookResult OnWarmupEnd(EventWarmupEnd eventInfo, GameEventInfo gameEventInfo) {
        if (config != null && config.GetGlobalConfigValue("autorecord", "0") == "1") {
            string date = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Server.ExecuteCommand("tv_enable 1");
            if (osbase != null) {
                Server.ExecuteCommand($"tv_record {date}-{osbase.currentMap}.dem");
            }
            Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Autorecord enabled. Demo recording started.");
        }

        return HookResult.Continue;
    }

    private HookResult OnMatchEndEvent(EventCsWinPanelMatch eventInfo, GameEventInfo gameEventInfo) {
        if (config?.GetGlobalConfigValue("autorecord", "0") == "1") {
            osbase?.SendCommand("tv_stoprecord");
            osbase?.SendCommand("tv_enable 0");
            Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Autorecord is enabled. Stopped recording demo.");
        }
        return HookResult.Continue;
    }

}