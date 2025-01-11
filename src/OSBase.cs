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
    public override string ModuleVersion => "0.0.5";
    public override string ModuleAuthor => "Pintuz";
    public override string ModuleDescription => "Plugin for handling map events with config execution";
    
    private string currentMap = "";
    private bool isWarmup = true;

    public override void Load(bool hotReload) {
        Console.WriteLine("[INFO] OSBase plugin is loading...");

        // Register listeners for round events

        RegisterEventHandler<EventRoundStart>(onRoundStart);
        RegisterEventHandler<EventWarmupEnd>(onWarmupEnd);

        RegisterListener<Listeners.OnMapStart>(onMapStart);
        RegisterListener<Listeners.OnMapEnd>(onMapEnd);
        RegisterEventHandler<EventCsWinPanelMatch>(onMatchEndEvent);

        Console.WriteLine("[INFO] OSBase plugin loaded successfully!");
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
        Console.WriteLine($"[INFO] Round started!");

        if (isWarmup) {
            Console.WriteLine("[INFO] Warmup is ongoing. This is the first round.");
            // Assume warmup ends after the first round
            isWarmup = false;
        } else {
            Console.WriteLine("[INFO] Warmup has ended. This is a live round.");
        }
        return HookResult.Continue;
    }

    private void SendCommand(string command) {
        try {
            Console.WriteLine($"[INFO] Sending command: {command}");
            Server.ExecuteCommand(command); // Directly execute the command
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] Failed to send: {command}, Exception: {ex.Message}");
        }
    }

    private void runEndOfMapCommands() {
        Console.WriteLine("[INFO] OSBase: Running end of map commands...");
        SendCommand("tv_stoprecord");
    }    
    private void runStartOfMapCommands() {
        Console.WriteLine("[INFO] OSBase: Running end of map commands...");
        var date = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");
        SendCommand("tv_startrecord demo-"+date+"-"+currentMap+".dem");
    }
}