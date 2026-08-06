using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;

namespace OSBase.Modules;

public class Mug : ModuleBase {
    public override string ModuleName => "mug";

    private const int MinMoney = 0;
    private const int MaxMoney = 16000;

    private DamageReport? damageReport;

    protected override void OnLoad() {
        damageReport = osbase?.GetModule<DamageReport>();
    }

    protected override void OnUnload() {
        damageReport = null;
    }

    protected override void OnReloadConfig() {
        damageReport = osbase?.GetModule<DamageReport>();
    }

    protected override void RegisterHandlers() {
        osbase?.SubscribeToEvent<EventPlayerDeath>(OnPlayerDeath);
    }

    protected override void UnregisterHandlers() {
        osbase?.UnsubscribeFromEvent<EventPlayerDeath>(OnPlayerDeath);
    }

    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo) {
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

        // Fixed 2026-08-06: used to be this module's own `.Contains("knife")` check on the
        // raw weapon string, which never matched a bayonet kill (raw name "weapon_bayonet",
        // no "knife" substring) -- silently exempting one whole skin family from both the
        // mugging and the teamkill penalty since the day this module existed. Ask the same
        // classifier DamageReport already uses instead of keeping a second, divergent
        // knife-detection list here.
        if (DamageReport.NormalizeWeapon(eventInfo.Weapon) != "knife") {
            return HookResult.Continue;
        }

        int victimMoney = victim.InGameMoneyServices.Account;
        string attackerName = attacker.PlayerName ?? "Unknown";
        string victimName = victim.PlayerName ?? "Unknown";

        // Ask 28: report the signed figure into the row DamageReport already buffered for
        // this exact kill (>0 taken from the victim, <0 paid as a teamkill penalty, =0 the
        // mechanic ran and moved nothing). Every exit past this point is a knife kill the
        // mechanic touched, so every one of them reports -- only a taser kill (filtered
        // above) leaves the column unreported, which is what makes NULL mean "never
        // touched" rather than "moved nothing".
        if (attacker.TeamNum == victim.TeamNum) {
            if (victimMoney <= 0) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Victim had no money, no team punish transfer applied.");
                damageReport?.ReportKnifeMoneyMoved(attacker.SteamID, victim.SteamID, 0);
                return HookResult.Continue;
            }

            int transferred = TransferMoney(attacker, victim, victimMoney);
            damageReport?.ReportKnifeMoneyMoved(attacker.SteamID, victim.SteamID, -transferred);

            if (transferred > 0) {
                Server.PrintToChatAll($"{attackerName} tried to mug their teammate {victimName} and lost ${transferred} as punishment!");
            } else {
                Server.PrintToChatAll($"{attackerName} tried to mug their teammate {victimName} but had no money to lose!");
            }

            return HookResult.Continue;
        }

        if (victimMoney <= 0) {
            Server.PrintToChatAll($"{attackerName} mugged {victimName} but they had no money!");
            damageReport?.ReportKnifeMoneyMoved(attacker.SteamID, victim.SteamID, 0);
            return HookResult.Continue;
        }

        int mugged = TransferMoney(victim, attacker, victimMoney);
        damageReport?.ReportKnifeMoneyMoved(attacker.SteamID, victim.SteamID, mugged);

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