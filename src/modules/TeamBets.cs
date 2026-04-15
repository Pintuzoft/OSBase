using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules;

public class TeamBets : IModule {
    public string ModuleName => "teambets";

    private OSBase? osbase;
    private Config? config;

    private bool handlersLoaded = false;
    private bool isActive = false;

    private const int TeamT = (int)CsTeam.Terrorist;
    private const int TeamCt = (int)CsTeam.CounterTerrorist;
    private const int MinMoney = 0;
    private const int MaxMoney = 16000;

    private bool roundLive = false;

    // userid -> bet
    private readonly Dictionary<int, Bet> bets = new();

    private class Bet {
        public int Amount { get; }
        public int Team { get; }
        public float Odds { get; }

        public Bet(int amount, int team, float odds) {
            Amount = amount;
            Team = team;
            Odds = odds;
        }
    }

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

        bets.Clear();
        roundLive = false;

        LoadHandlers();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    public void Unload() {
        isActive = false;

        if (osbase != null && handlersLoaded) {
            osbase.DeregisterEventHandler<EventRoundStart>(OnRoundStart);
            osbase.DeregisterEventHandler<EventRoundEnd>(OnRoundEnd);
            osbase.DeregisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
            osbase.DeregisterEventHandler<EventPlayerChat>(OnPlayerChat);

            handlersLoaded = false;
        }

        bets.Clear();
        roundLive = false;

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

        osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        osbase.RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        osbase.RegisterEventHandler<EventPlayerChat>(OnPlayerChat);

        handlersLoaded = true;
    }

    private HookResult OnPlayerChat(EventPlayerChat eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        if (eventInfo?.Userid == null || string.IsNullOrWhiteSpace(eventInfo.Text)) {
            return HookResult.Continue;
        }

        CCSPlayerController? player = Utilities.GetPlayerFromUserid(eventInfo.Userid);
        if (player == null || !player.IsValid || !player.UserId.HasValue) {
            return HookResult.Continue;
        }

        string text = eventInfo.Text.Trim();

        if (text.Equals("bet", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("bet ", StringComparison.OrdinalIgnoreCase)) {
            HandleBetCommand(player, text);
        }

        return HookResult.Continue;
    }

    // Format: bet <t/ct> <amount|all|half>
    private void HandleBetCommand(CCSPlayerController player, string command) {
        if (!isActive || player == null || !player.IsValid || !player.UserId.HasValue || player.InGameMoneyServices == null) {
            return;
        }

        List<string> parts = command
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        if (parts.Count < 3) {
            player.PrintToChat("[TeamBets]: Usage: bet <t/ct> <amount|all|half>");
            return;
        }

        if (IsWarmupActive()) {
            player.PrintToChat("[TeamBets]: Betting is not allowed during warmup.");
            return;
        }

        if (!roundLive) {
            player.PrintToChat("[TeamBets]: Betting is only allowed during a live round.");
            return;
        }

        if (player.PawnIsAlive) {
            player.PrintToChat("[TeamBets]: You must be dead to bet on the round!");
            return;
        }

        if (!IsPlayableTeam(player.TeamNum)) {
            player.PrintToChat("[TeamBets]: You must be on a team to bet on the round!");
            return;
        }

        if (bets.ContainsKey(player.UserId.Value)) {
            player.PrintToChat("[TeamBets]: You've already placed a bet this round!");
            return;
        }

        int aliveT = CountAlivePlayers(TeamT);
        int aliveCt = CountAlivePlayers(TeamCt);

        if (aliveT <= 0 || aliveCt <= 0) {
            player.PrintToChat("[TeamBets]: Betting is closed for this round state.");
            return;
        }

        string teamArg = parts[1].ToLowerInvariant();
        string amountArg = parts[2].ToLowerInvariant();

        int betTeam;
        float odds;

        switch (teamArg) {
            case "t":
                betTeam = TeamT;
                odds = (float)aliveCt / aliveT;
                break;

            case "ct":
                betTeam = TeamCt;
                odds = (float)aliveT / aliveCt;
                break;

            default:
                player.PrintToChat("[TeamBets]: Invalid team. Use 't' or 'ct'.");
                return;
        }

        int currentBalance = player.InGameMoneyServices.Account;
        int amount;

        switch (amountArg) {
            case "all":
                amount = currentBalance;
                break;

            case "half":
                amount = (int)Math.Floor(currentBalance / 2f);
                break;

            default:
                if (!int.TryParse(amountArg, out amount)) {
                    player.PrintToChat("[TeamBets]: Invalid bet amount!");
                    return;
                }
                break;
        }

        if (amount <= 0) {
            player.PrintToChat("[TeamBets]: Bet amount must be greater than 0!");
            return;
        }

        if (amount > currentBalance) {
            player.PrintToChat("[TeamBets]: You don't have enough cash to bet that amount!");
            return;
        }

        RemoveMoney(player, amount);
        bets[player.UserId.Value] = new Bet(amount, betTeam, odds);

        player.PrintToChat(
            $"[TeamBets]: You bet ${amount} on {(betTeam == TeamT ? "T" : "CT")} " +
            $"at {odds:0.00} odds. Alive T/CT: {aliveT}/{aliveCt}"
        );

        Console.WriteLine(
            $"[DEBUG] OSBase[{ModuleName}] Bet placed by {player.PlayerName}: " +
            $"team={(betTeam == TeamT ? "T" : "CT")} amount={amount} odds={odds:0.00} aliveT={aliveT} aliveCt={aliveCt}"
        );
    }

    private HookResult OnRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        bets.Clear();
        roundLive = false;
        return HookResult.Continue;
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        if (!IsWarmupActive()) {
            roundLive = true;
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        roundLive = false;

        int winningTeam = eventInfo.Winner;

        foreach (var kvp in bets) {
            int userId = kvp.Key;
            Bet bet = kvp.Value;

            CCSPlayerController? player = Utilities.GetPlayerFromUserid(userId);
            if (player == null || !player.IsValid || player.InGameMoneyServices == null) {
                continue;
            }

            if (bet.Team == winningTeam) {
                int payout = (int)Math.Round(bet.Amount * (1.0f + bet.Odds));
                int profit = payout - bet.Amount;

                AddMoney(player, payout);

                player.PrintToChat(
                    $"[TeamBets]: You won ${profit} betting on {(winningTeam == TeamT ? "T" : "CT")}!"
                );

                Console.WriteLine(
                    $"[DEBUG] OSBase[{ModuleName}] Bet WON by {player.PlayerName}: " +
                    $"amount={bet.Amount} payout={payout} profit={profit} odds={bet.Odds:0.00}"
                );
            } else {
                player.PrintToChat(
                    $"[TeamBets]: You lost ${bet.Amount} betting on {(bet.Team == TeamT ? "T" : "CT")}."
                );

                Console.WriteLine(
                    $"[DEBUG] OSBase[{ModuleName}] Bet LOST by {player.PlayerName}: " +
                    $"amount={bet.Amount} team={(bet.Team == TeamT ? "T" : "CT")} odds={bet.Odds:0.00}"
                );
            }
        }

        bets.Clear();
        return HookResult.Continue;
    }

    private int CountAlivePlayers(int teamNum) {
        return Utilities.GetPlayers()
            .Count(p => p != null && p.IsValid && p.TeamNum == teamNum && p.PawnIsAlive);
    }

    private bool IsWarmupActive() {
        return GameStats.Current?.IsWarmup ?? true;
    }

    private bool IsPlayableTeam(int teamNum) {
        return teamNum == TeamT || teamNum == TeamCt;
    }

    private void AddMoney(CCSPlayerController player, int amount) {
        if (player == null || !player.IsValid || player.InGameMoneyServices == null) {
            return;
        }

        int finalAmount = player.InGameMoneyServices.Account + amount;
        finalAmount = Math.Clamp(finalAmount, MinMoney, MaxMoney);
        player.InGameMoneyServices.Account = finalAmount;
    }

    private void RemoveMoney(CCSPlayerController player, int amount) {
        AddMoney(player, -amount);
    }
}