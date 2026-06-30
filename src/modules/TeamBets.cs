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
    private const float LeaderboardDelaySeconds = 1.5f;

    private bool roundLive = false;

    // userid -> bet
    private readonly Dictionary<int, Bet> bets = new();

    private class Bet {
        public string PlayerName { get; }
        public int Amount { get; }
        public int Team { get; }
        public float Odds { get; }
        public int AliveT { get; }
        public int AliveCt { get; }

        public Bet(string playerName, int amount, int team, float odds, int aliveT, int aliveCt) {
            PlayerName = playerName;
            Amount = amount;
            Team = team;
            Odds = odds;
            AliveT = aliveT;
            AliveCt = aliveCt;
        }
    }

    private class BetResult {
        public string PlayerName { get; }
        public int Amount { get; }
        public int Team { get; }
        public float Odds { get; }
        public int AliveT { get; }
        public int AliveCt { get; }
        public int NetResult { get; }
        public string Note { get; }

        public BetResult(string playerName, int amount, int team, float odds, int aliveT, int aliveCt, int netResult, string note = "") {
            PlayerName = playerName;
            Amount = amount;
            Team = team;
            Odds = odds;
            AliveT = aliveT;
            AliveCt = aliveCt;
            NetResult = netResult;
            Note = note;
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
            // Use new EventBus system for bomb/round events
            osbase.UnsubscribeFromEvent<EventRoundStart>(OnRoundStart);
            osbase.UnsubscribeFromEvent<EventRoundEnd>(OnRoundEnd);
            osbase.UnsubscribeFromEvent<EventRoundFreezeEnd>(OnRoundFreezeEnd);
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

        // Use new EventBus system for bomb/round events
        osbase.SubscribeToEvent<EventRoundStart>(OnRoundStart);
        osbase.SubscribeToEvent<EventRoundEnd>(OnRoundEnd);
        osbase.SubscribeToEvent<EventRoundFreezeEnd>(OnRoundFreezeEnd);
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

        string playerName = string.IsNullOrWhiteSpace(player.PlayerName)
            ? $"UserID {player.UserId.Value}"
            : player.PlayerName;

        RemoveMoney(player, amount);

        bets[player.UserId.Value] = new Bet(
            playerName,
            amount,
            betTeam,
            odds,
            aliveT,
            aliveCt
        );

        player.PrintToChat(
            $"[TeamBets]: Bet placed " +
            FormatBetDetails(amount, betTeam, odds, aliveT, aliveCt)
        );

        Console.WriteLine(
            $"[DEBUG] OSBase[{ModuleName}] Bet placed by {playerName}: " +
            $"team={GetTeamName(betTeam)} amount={amount} odds={odds:0.00} aliveT={aliveT} aliveCt={aliveCt}"
        );
    }

    private HookResult OnRoundStart(EventRoundStart eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        bets.Clear();
        roundLive = false;
        return HookResult.Continue;
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        if (!IsWarmupActive()) {
            roundLive = true;
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd eventInfo) {
        if (!isActive) {
            return HookResult.Continue;
        }

        roundLive = false;

        if (bets.Count <= 0) {
            return HookResult.Continue;
        }

        int winningTeam = eventInfo.Winner;

        if (!IsPlayableTeam(winningTeam)) {
            RefundAllBets("No valid winning team.");
            bets.Clear();
            return HookResult.Continue;
        }

        List<BetResult> results = new();

        foreach (var kvp in bets) {
            int userId = kvp.Key;
            Bet bet = kvp.Value;

            CCSPlayerController? player = Utilities.GetPlayerFromUserid(userId);
            bool playerOnline = player != null && player.IsValid && player.InGameMoneyServices != null;

            if (bet.Team == winningTeam) {
                int payout = (int)Math.Round(bet.Amount * (1.0f + bet.Odds));
                int actualPaid = 0;
                string note = "";

                if (playerOnline && player != null && player.InGameMoneyServices != null) {
                    int balanceBefore = player.InGameMoneyServices.Account;

                    AddMoney(player, payout);

                    int balanceAfter = player.InGameMoneyServices.Account;
                    actualPaid = balanceAfter - balanceBefore;

                    if (actualPaid < payout) {
                        note = "cash cap";
                    }
                } else {
                    note = "disconnected";
                }

                int netResult = actualPaid - bet.Amount;

                results.Add(new BetResult(
                    bet.PlayerName,
                    bet.Amount,
                    bet.Team,
                    bet.Odds,
                    bet.AliveT,
                    bet.AliveCt,
                    netResult,
                    note
                ));

                Console.WriteLine(
                    $"[DEBUG] OSBase[{ModuleName}] Bet WON by {bet.PlayerName}: " +
                    $"amount={bet.Amount} payout={payout} actualPaid={actualPaid} net={netResult} odds={bet.Odds:0.00} online={playerOnline}"
                );
            } else {
                int netResult = -bet.Amount;

                results.Add(new BetResult(
                    bet.PlayerName,
                    bet.Amount,
                    bet.Team,
                    bet.Odds,
                    bet.AliveT,
                    bet.AliveCt,
                    netResult
                ));

                Console.WriteLine(
                    $"[DEBUG] OSBase[{ModuleName}] Bet LOST by {bet.PlayerName}: " +
                    $"amount={bet.Amount} team={GetTeamName(bet.Team)} odds={bet.Odds:0.00} online={playerOnline}"
                );
            }
        }

        PrintBetLeaderboardDelayed(winningTeam, results);

        bets.Clear();
        return HookResult.Continue;
    }

    private void PrintBetLeaderboardDelayed(int winningTeam, List<BetResult> results) {
        if (osbase == null) {
            PrintBetLeaderboard(winningTeam, results);
            return;
        }

        int finalWinningTeam = winningTeam;
        List<BetResult> finalResults = results.ToList();

        osbase.AddTimer(LeaderboardDelaySeconds, () => {
            if (!isActive) {
                return;
            }

            PrintBetLeaderboard(finalWinningTeam, finalResults);
        });
    }

    private void PrintBetLeaderboard(int winningTeam, List<BetResult> results) {
        List<BetResult> sortedResults = results
            .OrderByDescending(r => r.NetResult)
            .ThenBy(r => r.PlayerName)
            .ToList();

        BroadcastToChat($"[TeamBets]: {GetTeamName(winningTeam)} won. Betting leaderboard:");

        int rank = 1;

        foreach (BetResult result in sortedResults) {
            string moneyText = FormatMoneyResult(result.NetResult);
            string netLabel = FormatNetLabel(result.NetResult);
            string noteText = string.IsNullOrWhiteSpace(result.Note) ? "" : $" ({result.Note})";

            BroadcastToChat(
                $"[TeamBets]: #{rank} {result.PlayerName}: " +
                $"{moneyText} {netLabel} " +
                FormatBetDetails(result.Amount, result.Team, result.Odds, result.AliveT, result.AliveCt, noteText)
            );

            rank++;
        }
    }

    private string FormatBetDetails(int amount, int team, float odds, int aliveT, int aliveCt, string noteText = "") {
        return
            $"{ChatColors.Grey}| bet ${amount} on {GetTeamName(team)} @ {odds:0.00}x " +
            $"[{GetBetSituationText(team, aliveT, aliveCt)}]{noteText}" +
            $"{ChatColors.Default}";
    }

    private string FormatMoneyResult(int amount) {
        if (amount > 0) {
            return $"{ChatColors.Green}+${amount}{ChatColors.Default}";
        }

        if (amount < 0) {
            return $"{ChatColors.Red}-${Math.Abs(amount)}{ChatColors.Default}";
        }

        return $"{ChatColors.Default}$0";
    }

    private string FormatNetLabel(int amount) {
        if (amount > 0) {
            return $"{ChatColors.Green}profit{ChatColors.Default}";
        }

        if (amount < 0) {
            return $"{ChatColors.Red}loss{ChatColors.Default}";
        }

        return $"{ChatColors.Default}net";
    }

    private string GetBetSituationText(int betTeam, int aliveT, int aliveCt) {
        if (betTeam == TeamT) {
            return $"{aliveT}v{aliveCt}";
        }

        if (betTeam == TeamCt) {
            return $"{aliveCt}v{aliveT}";
        }

        return $"{aliveT}v{aliveCt}";
    }

    private void RefundAllBets(string reason) {
        BroadcastToChat($"[TeamBets]: {reason} Refunding all active bets.");

        foreach (var kvp in bets) {
            int userId = kvp.Key;
            Bet bet = kvp.Value;

            CCSPlayerController? player = Utilities.GetPlayerFromUserid(userId);
            if (player == null || !player.IsValid || player.InGameMoneyServices == null) {
                BroadcastToChat($"[TeamBets]: {bet.PlayerName} would have been refunded ${bet.Amount}, but is disconnected.");
                continue;
            }

            AddMoney(player, bet.Amount);
            BroadcastToChat($"[TeamBets]: {bet.PlayerName} was refunded ${bet.Amount}.");
        }
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

    private string GetTeamName(int teamNum) {
        return teamNum == TeamT ? "T" : "CT";
    }

    private void BroadcastToChat(string message) {
        foreach (CCSPlayerController player in Utilities.GetPlayers()) {
            if (player == null || !player.IsValid) {
                continue;
            }

            player.PrintToChat(message);
        }
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