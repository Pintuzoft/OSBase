using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Commands;
using CounterStrikeSharp.API.Modules.Commands;

namespace OSBase.Modules;

// Lets the site put a message in a single player's console before it kicks or bans them --
// the CS2 client drops the "Kicked by server." dialog's reason entirely, and neither chat
// nor a HUD panel survives the disconnect, so this is the only place left to say why.
// Registered SERVER_ONLY: this is an RCON command for the site, not something a client types.
public class ConsoleMessage : ModuleBase {
    public override string ModuleName => "consolemessage";

    private const string CommandName = "osbase_console";

    private CommandDefinition? commandDefinition;

    protected override void RegisterHandlers() {
        if (osbase == null) {
            return;
        }

        commandDefinition = new CommandDefinition(CommandName, "Print a message to one player's console: #<userid> <text>", OnConsoleCommand) {
            ExecutableBy = CommandUsage.SERVER_ONLY
        };
        osbase.CommandManager.RegisterCommand(commandDefinition);
    }

    protected override void UnregisterHandlers() {
        if (osbase == null || commandDefinition == null) {
            return;
        }

        osbase.CommandManager.RemoveCommand(commandDefinition);
        commandDefinition = null;
    }

    private void OnConsoleCommand(CCSPlayerController? player, CommandInfo commandInfo) {
        if (!isActive) {
            return;
        }

        string args = commandInfo.ArgString ?? string.Empty;
        int separator = args.IndexOf(' ');
        if (separator < 0) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {CommandName}: missing text, usage: {CommandName} #<userid> <text>");
            return;
        }

        string targetToken = args.Substring(0, separator);
        string text = args.Substring(separator + 1);

        if (targetToken.Length < 2 || targetToken[0] != '#' || !int.TryParse(targetToken.AsSpan(1), out int userId)) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {CommandName}: bad target token '{targetToken}', expected #<userid>");
            return;
        }

        if (text.Length == 0) {
            return;
        }

        CCSPlayerController? target = Utilities.GetPlayerFromUserid(userId);
        if (target == null || !target.IsValid || target.IsBot) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {CommandName}: no such player for userid {userId}, no-op.");
            return;
        }

        foreach (string line in text.Split("\\n")) {
            target.PrintToConsole(line);
        }
    }
}
