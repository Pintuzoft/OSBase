using System.IO;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;

namespace OSBase;

public class OSBase : BasePlugin {
    public override string ModuleName => "OSBase";
    public override string ModuleVersion => "0.0.5";
    public override string ModuleAuthor => "Pintuz";
    public override string ModuleDescription => "Plugin for handling map events with config execution";
    private Timer? warmupPollingTimer;

    public override void Load(bool hotReload) {
        Console.WriteLine("[INFO] OSBase plugin is loading...");

        // Register listeners for map events
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

        Console.WriteLine("[INFO] OSBase plugin loaded successfully!");
    }

    private void OnMapStart(string mapName) {
        Console.WriteLine($"[INFO] Map '{mapName}' started!");

        // Send dummy command at map start
        SendDummyCommand("say Map has started!");

        // Start warmup polling
        StartWarmupPolling();
    }

    private void OnMapEnd() {
        Console.WriteLine("[INFO] Map ended!");

        // Send dummy command at map end
        SendDummyCommand("say Map has ended!");
    }

    private void StartWarmupPolling() {
        Console.WriteLine("[DEBUG] Starting warmup polling...");

        warmupPollingTimer = new Timer(state => {
            Console.WriteLine("[DEBUG] Polling warmup state...");

            if (!IsWarmupActive()) {
                Console.WriteLine("[INFO] Warmup has ended!");

                // Send dummy command when warmup ends
                SendDummyCommand("say Warmup has ended!");

                StopWarmupPolling();
            }
        }, null, 0, 1000); // Poll every 1 second
    }

    private void StopWarmupPolling() {
        if (warmupPollingTimer != null) {
            warmupPollingTimer.Dispose();
            warmupPollingTimer = null;
            Console.WriteLine("[DEBUG] Warmup polling stopped.");
        }
    }

    private bool IsWarmupActive() {
        Console.WriteLine("[DEBUG] Checking if warmup is active...");
        try {
            var conVar = ConVar.Find("mp_warmup_pausetimer");
            if (conVar == null) {
                Console.WriteLine("[ERROR] Failed to find ConVar: mp_warmup_pausetimer.");
                return false;
            }

            string value = conVar.StringValue;
            Console.WriteLine($"[DEBUG] Warmup state value: {value}");
            return value == "0"; // Adjust based on your warmup logic
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] Exception while checking warmup state: {ex.Message}");
            return false;
        }
    }

    private void SendDummyCommand(string command) {
        try {
            Console.WriteLine($"[INFO] Sending dummy command: {command}");
            Server.ExecuteCommand(command); // Directly execute the command
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] Failed to send command: {command}, Exception: {ex.Message}");
        }
    }
}