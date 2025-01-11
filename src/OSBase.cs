using System.IO;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Core.Attributes.Registration;

namespace OSBase;

public class OSBase : BasePlugin {
    public override string ModuleName => "OSBase";
    public override string ModuleVersion => "0.0.5";
    public override string ModuleAuthor => "Pintuz";
    public override string ModuleDescription => "Plugin for handling map events with config execution";
    private int roundNumber = 0;
    private bool isWarmup = true;

    public override void Load(bool hotReload) {
        Console.WriteLine("[INFO] OSBase plugin is loading...");

        // Register listeners for round events

        RegisterEventHandler<EventRoundStart>(onRoundStart);
        RegisterEventHandler<EventRoundEnd>(onRoundEnd);
        RegisterEventHandler<EventMapTransition>(eventMapTransition);

     //   RegisterEventHandler<EventGameEnd>(eventGameEnd);

     //   RegisterListener<OnRoundStart>(OnRoundStart);
     //   RegisterListener<OnRoundEnd>(OnRoundEnd);

        Console.WriteLine("[INFO] OSBase plugin loaded successfully!");
    }

    private HookResult eventMapTransition(EventMapTransition eventInfo, GameEventInfo gameEventInfo) {
        roundNumber = 0;
        isWarmup = true;
        return HookResult.Continue;
    }
//    private HookResult eventGameEnd(EventGameEnd eventInfo, GameEventInfo gameEventInfo) {
//        roundNumber = 0;
//        isWarmup = true;
//        return HookResult.Continue;
//    }
    private HookResult onRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
        if ( isWarmup ) {
            Console.WriteLine("[INFO] Warmup has ended. This is a live round.");
        } else {
            Console.WriteLine("[INFO] Round has ended. This is a live round.");
        }
        isWarmup = false;
        return HookResult.Continue;
    }
    private HookResult onRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {
        // Increment the round number
        roundNumber++;
        Console.WriteLine($"[INFO] Round {roundNumber} started!");

        if (isWarmup) {
            Console.WriteLine("[INFO] Warmup is ongoing. This is the first round.");
            SendDummyCommand("say Warmup has started!");

            // Assume warmup ends after the first round
            isWarmup = false;
        } else {
            Console.WriteLine("[INFO] Warmup has ended. This is a live round.");
            SendDummyCommand($"say Round {roundNumber} has started!");
        }
        return HookResult.Continue;
    }

    private void OnRoundEnd(int roundNumber, string winningTeam) {
        Console.WriteLine($"[INFO] Round {roundNumber} ended! Winning team: {winningTeam}");
        SendDummyCommand($"say Round {roundNumber} has ended! Winning team: {winningTeam}");
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