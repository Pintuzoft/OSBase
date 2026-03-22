using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;

namespace OSBase.Helpers;

public static class ChatHelper {
    public static void PrintToAdmins(string message, string permission = "@css/generic", string prefix = " \x08[OSBase]\x01 ") {
        Console.WriteLine($"[DEBUG] ChatHelper.PrintToAdmins: message='{message}', permission='{permission}'");

        foreach (var player in Utilities.GetPlayers()) {
            if (player == null) {
                Console.WriteLine("[DEBUG] ChatHelper: skipped null player");
                continue;
            }

            if (!player.IsValid) {
                Console.WriteLine($"[DEBUG] ChatHelper: skipped invalid player slot={player.Slot}");
                continue;
            }

            if (player.IsBot) {
                Console.WriteLine($"[DEBUG] ChatHelper: skipped bot name='{player.PlayerName}' slot={player.Slot}");
                continue;
            }

            bool hasPermission = AdminManager.PlayerHasPermissions(player, permission);

            Console.WriteLine(
                $"[DEBUG] ChatHelper: player='{player.PlayerName}', steamid='{player.SteamID}', slot={player.Slot}, hasPermission={hasPermission}"
            );

            if (!hasPermission) {
                Console.WriteLine($"[DEBUG] ChatHelper: not sending to '{player.PlayerName}'");
                continue;
            }

            player.PrintToChat($"{prefix}{message}");
            Console.WriteLine($"[DEBUG] ChatHelper: sent to '{player.PlayerName}'");
        }
    }

    public static void PrintToPlayer(CCSPlayerController? player, string message, string prefix = " \x08[OSBase]\x01 ") {
        if (player == null) {
            Console.WriteLine("[DEBUG] ChatHelper.PrintToPlayer: skipped null player");
            return;
        }

        if (!player.IsValid) {
            Console.WriteLine($"[DEBUG] ChatHelper.PrintToPlayer: skipped invalid player slot={player.Slot}");
            return;
        }

        if (player.IsBot) {
            Console.WriteLine($"[DEBUG] ChatHelper.PrintToPlayer: skipped bot name='{player.PlayerName}' slot={player.Slot}");
            return;
        }

        player.PrintToChat($"{prefix}{message}");
        Console.WriteLine($"[DEBUG] ChatHelper.PrintToPlayer: sent to '{player.PlayerName}'");
    }
}