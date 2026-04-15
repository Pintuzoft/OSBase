using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;

namespace OSBase.Modules;

public class Mug : IModule {
    public string ModuleName => "mug";

    private OSBase? osbase;
    private Config? config;

    private bool handlersLoaded = false;
    private bool isActive = false;

    private const int MinMoney = 0;
    private const int MaxMoney = 16000;

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;
        isActive = true;

        if (osbase == null || config == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] load failed (null deps).");
            isActive = false;
            return;
        }

        config.RegisterGlobalConfigValue(ModuleName, "1");

        if (config.GetGlobalConfigValue(ModuleName, "0") != "1") {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in config.");
            isActive = false;
            return;
        }

        LoadHandlers();
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    public void Unload() {
        isActive = false;

        if (osbase != null && handlersLoaded) {
            osbase.DeregisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            handlersLoaded = false;
        }

        config = null;
        osbase = null;

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] unloaded.");
    }

    public void ReloadConfig(Config inConfig) {
        config = inConfig;
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] config reloaded.");
    }

    private void LoadHandlers() {
        if (osbase == null || handlersLoaded) {
            return;
        }

        osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        handlersLoaded = true;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        var attacker = eventInfo.Attacker;
        var victim = eventInfo.Userid;

        if (attacker == null || victim == null || !attacker.IsValid || !victim.IsValid) {
            return HookResult.Continue;
        }

        if (!attacker.UserId.HasValue || !victim.UserId.HasValue) {
            return HookResult.Continue;
        }

        if (attacker.UserId.Value == victim.UserId.Value) {
            return HookResult.Continue;
        }

        if (attacker.InGameMoneyServices == null || victim.InGameMoneyServices == null) {
            return HookResult.Continue;
        }

        string weapon = eventInfo.Weapon ?? string.Empty;
        if (!weapon.Contains("knife", StringComparison.OrdinalIgnoreCase)) {
            return HookResult.Continue;
        }

        int victimMoney = victim.InGameMoneyServices.Account;
        string attackerName = attacker.PlayerName ?? "Unknown";
        string victimName = victim.PlayerName ?? "Unknown";

        if (attacker.TeamNum == victim.TeamNum) {
            if (victimMoney <= 0) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Victim had no money, no team punish transfer applied.");
                return HookResult.Continue;
            }

            int transferred = TransferMoney(attacker, victim, victimMoney);

            if (transferred > 0) {
                Server.PrintToChatAll($"{attackerName} tried to mug their teammate {victimName} and lost ${transferred} as punishment!");
            } else {
                Server.PrintToChatAll($"{attackerName} tried to mug their teammate {victimName} but had no money to lose!");
            }

            return HookResult.Continue;
        }

        if (victimMoney <= 0) {
            Server.PrintToChatAll($"{attackerName} mugged {victimName} but they had no money!");
            return HookResult.Continue;
        }

        int mugged = TransferMoney(victim, attacker, victimMoney);

        if (mugged > 0) {
            Server.PrintToChatAll($"{attackerName} mugged {victimName} for ${mugged}!");
        } else {
            Server.PrintToChatAll($"{attackerName} mugged {victimName} but couldn't carry any more money!");
        }

        return HookResult.Continue;
    }

    private int TransferMoney(CCSPlayerController from, CCSPlayerController to, int requestedAmount) {
        if (from == null || to == null || !from.IsValid || !to.IsValid) {
            return 0;
        }

        if (from.InGameMoneyServices == null || to.InGameMoneyServices == null) {
            return 0;
        }

        if (requestedAmount <= 0) {
            return 0;
        }

        int fromBalance = from.InGameMoneyServices.Account;
        int toBalance = to.InGameMoneyServices.Account;
        int receiverRoom = Math.Max(0, MaxMoney - toBalance);

        int transferable = Math.Min(requestedAmount, Math.Min(fromBalance, receiverRoom));
        if (transferable <= 0) {
            return 0;
        }

        from.InGameMoneyServices.Account = Math.Clamp(fromBalance - transferable, MinMoney, MaxMoney);
        to.InGameMoneyServices.Account = Math.Clamp(toBalance + transferable, MinMoney, MaxMoney);

        Console.WriteLine(
            $"[DEBUG] OSBase[{ModuleName}] [TransferMoney]: " +
            $"{from.PlayerName} -> {to.PlayerName}, requested={requestedAmount}, transferred={transferable}, " +
            $"fromNow={from.InGameMoneyServices.Account}, toNow={to.InGameMoneyServices.Account}"
        );

        return transferable;
    }

    public void AddMoney(CCSPlayerController player, int amount) {
        if (player == null || !player.IsValid || player.InGameMoneyServices == null) {
            return;
        }

        int finalAmount = player.InGameMoneyServices.Account + amount;
        finalAmount = Math.Clamp(finalAmount, MinMoney, MaxMoney);

        player.InGameMoneyServices.Account = finalAmount;
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] [AddMoney]: {player.PlayerName} now has {finalAmount} money.");
    }

    public void RemoveMoney(CCSPlayerController player, int amount) {
        AddMoney(player, -amount);
    }
}