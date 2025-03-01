using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;

namespace OSBase.Modules {
    public class Mug : IModule {
        public string ModuleName => "mug";
        private OSBase? osbase;
        private Config? config;
        private const int MinMoney = 0;
        private const int MaxMoney = 16000;

        public void Load(OSBase inOsbase, Config inConfig) {
            osbase = inOsbase;
            config = inConfig;

            config.RegisterGlobalConfigValue($"{ModuleName}", "1");

            if (osbase == null) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] osbase is null. {ModuleName} failed to load.");
                return;
            } 
            if (config == null) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] config is null. {ModuleName} failed to load.");
                return;
            }

            if (config?.GetGlobalConfigValue($"{ModuleName}", "0") == "1") {
                loadEventHandlers();
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
            } else {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {ModuleName} is disabled in the global configuration.");
            }
        }

        private void loadEventHandlers() {
            if(osbase == null) return;
            osbase.RegisterEventHandler<EventPlayerDeath>(onPlayerDeath);
        }

        private HookResult onPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
            if (config == null || osbase == null) 
                return HookResult.Continue;

            var attacker = eventInfo.Attacker;
            var victim = eventInfo.Userid;

            if (attacker == null || victim == null || !attacker.IsValid || !victim.IsValid) 
                return HookResult.Continue;

            if (!eventInfo.Weapon.Contains("knife")) 
                return HookResult.Continue;

            int victimMoney = victim.InGameMoneyServices?.Account ?? 0;

            if (attacker.Team == victim.Team) {
                if (victimMoney > 0) {
                    removeMoney(attacker, victimMoney);
                    addMoney(victim, victimMoney);
                    Server.PrintToChatAll($"{attacker.PlayerName} tried to mug their teammate {victim.PlayerName} and lost ${victimMoney} as punishment!");
                } else {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Attacker has no money to punish.");
                }
            } else {
                if (victimMoney > 0) {
                    removeMoney(victim, victimMoney);
                    addMoney(attacker, victimMoney);
                    Server.PrintToChatAll($"{attacker.PlayerName} mugged {victim.PlayerName} for ${victimMoney}!");
                } else {
                    Server.PrintToChatAll($"{attacker.PlayerName} mugged {victim.PlayerName} but they had no money!");
                }
            }

            return HookResult.Continue;
        }

        public void addMoney(CCSPlayerController player, int amount) {
            if (player == null || !player.IsValid || player.InGameMoneyServices == null)
                return;

            int finalAmount = player.InGameMoneyServices.Account + amount;
            finalAmount = Math.Clamp(finalAmount, MinMoney, MaxMoney);

            player.InGameMoneyServices.Account = finalAmount;
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] [AddMoney]: {player.PlayerName} now has {finalAmount} money.");
        }

        public void removeMoney(CCSPlayerController player, int amount) {
            addMoney(player, -amount);
        }
    }
}