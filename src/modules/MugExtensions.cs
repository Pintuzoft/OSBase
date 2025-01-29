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

namespace OSBase.Modules {
    public static class MugExtensions {
        private const int MinMoney = 0;
        private const int MaxMoney = 16000;

        // Method to add money (handles both adding and clamping)
        public static void AddMoney(this CCSPlayerController player, int amount) {
            if (player == null || !player.IsValid || player.InGameMoneyServices == null)
                return;

            // Calculate and clamp the new money value
            int finalAmount = player.InGameMoneyServices.Account + amount;
            finalAmount = Math.Clamp(finalAmount, MinMoney, MaxMoney);

            // Update the player's account
            player.InGameMoneyServices.Account = finalAmount;

            // Log the change
            Console.WriteLine($"[DEBUG] MugModuleExtensions[AddMoney]: {player.PlayerName} now has {finalAmount} money.");
        }

        // Method to remove money (forwards to AddMoney with a negative value)
        public static void RemoveMoney(this CCSPlayerController player, int amount) {
            player.AddMoney(-amount);
        }
    }
}