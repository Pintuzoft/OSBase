using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules {

    public class TeamBets : IModule {
        public string ModuleName => "teambets";

        private OSBase? osbase;
        private Config? config;

        private const int TEAM_T = (int)CsTeam.Terrorist;
        private const int TEAM_CT = (int)CsTeam.CounterTerrorist;
        private const int MinMoney = 0;
        private const int MaxMoney = 16000;

        // optional live round flag, but warmup is sourced from GameStats
        private bool roundLive = false;

        // userid -> bet
        private readonly Dictionary<int, Bet> betters = new();

        private class Bet {
            public int Amount { get; set; }
            public int Team { get; set; }
            public float Odds { get; set; }
            public int AliveT { get; set; }
            public int AliveCt { get; set; }

            public Bet(int amount, int team, float odds, int aliveT, int aliveCt) {
                Amount = amount;
                Team = team;
                Odds = odds;
                AliveT = aliveT;
                AliveCt = aliveCt;
            }
        }

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

            if (config.GetGlobalConfigValue($"{ModuleName}", "0") == "1") {
                LoadEventHandlers();
                osbase.RegisterEventHandler<EventPlayerChat>(OnPlayerChat);
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
            } else {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {ModuleName} is disabled in the global configuration.");
            }
        }

        private void LoadEventHandlers() {
            if (osbase == null)
                return;

            osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);
            osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
            osbase.RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        }

        private HookResult OnPlayerChat(EventPlayerChat eventInfo, GameEventInfo gameEventInfo) {
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

        private void HandleBetCommand(CCSPlayerController player, string command) {
            if (player == null || !player.IsValid || !player.UserId.HasValue || player.InGameMoneyServices == null) {
                return;
            }

            List<string> cmds = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

            if (cmds.Count < 3) {
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

            if (!IsOnATeam(player.Team)) {
                player.PrintToChat("[TeamBets]: You must be on a team to bet on the round!");
                return;
            }

            if (betters.ContainsKey(player.UserId.Value)) {
                player.PrintToChat("[TeamBets]: You've already placed a bet this round!");
                return;
            }

            var aliveTPlayers = Utilities.GetPlayers()
                .Where(p => p != null && p.IsValid && p.TeamNum == TEAM_T && p.PawnIsAlive)
                .ToList();

            var aliveCtPlayers = Utilities.GetPlayers()
                .Where(p => p != null && p.IsValid && p.TeamNum == TEAM_CT && p.PawnIsAlive)
                .ToList();

            int aliveT = aliveTPlayers.Count;
            int aliveCt = aliveCtPlayers.Count;

            if (aliveT <= 0 || aliveCt <= 0) {
                player.PrintToChat("[TeamBets]: Betting is closed for this round state.");
                return;
            }

            string teamStr = cmds[1].ToLowerInvariant();
            string amountStr = cmds[2].ToLowerInvariant();

            int team;
            float odds;

            switch (teamStr) {
                case "t":
                    team = TEAM_T;
                    odds = (float)aliveCt / aliveT;
                    break;

                case "ct":
                    team = TEAM_CT;
                    odds = (float)aliveT / aliveCt;
                    break;

                default:
                    player.PrintToChat("[TeamBets]: Invalid team. Use 't' or 'ct'.");
                    return;
            }

            int currentBalance = player.InGameMoneyServices.Account;
            int amount;

            switch (amountStr) {
                case "all":
                    amount = currentBalance;
                    break;

                case "half":
                    amount = (int)Math.Floor(currentBalance / 2f);
                    break;

                default:
                    if (!int.TryParse(amountStr, out amount)) {
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

            int oldBalance = currentBalance;
            RemoveMoney(player, amount);
            int newBalance = player.InGameMoneyServices.Account;

            betters[player.UserId.Value] = new Bet(amount, team, odds, aliveT, aliveCt);

            player.PrintToChat(
                $"[TeamBets]: Bet placed: ${amount} on {(team == TEAM_T ? "T" : "CT")} " +
                $"at {odds:0.00} odds. Alive T/CT: {aliveT}/{aliveCt}. Balance: ${oldBalance} -> ${newBalance}"
            );

            Console.WriteLine(
                $"[DEBUG] OSBase[{ModuleName}] Bet placed by {player.PlayerName}: " +
                $"team={(team == TEAM_T ? "T" : "CT")} amount={amount} odds={odds:0.00} aliveT={aliveT} aliveCt={aliveCt} balance={oldBalance}->{newBalance}"
            );
        }

        private HookResult OnRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {
            betters.Clear();
            roundLive = false;
            return HookResult.Continue;
        }

        private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd eventInfo, GameEventInfo gameEventInfo) {
            if (!IsWarmupActive()) {
                roundLive = true;
            }
            return HookResult.Continue;
        }

        private HookResult OnRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
            roundLive = false;

            int winningTeam = eventInfo.Winner;

            foreach (var kvp in betters) {
                int playerId = kvp.Key;
                Bet bet = kvp.Value;

                CCSPlayerController? player = Utilities.GetPlayerFromUserid(playerId);
                if (player == null || !player.IsValid || player.InGameMoneyServices == null) {
                    continue;
                }

                if (bet.Team == winningTeam) {
                    int payout = (int)Math.Round(bet.Amount * (1.0f + bet.Odds));
                    int profit = payout - bet.Amount;

                    int oldBalance = player.InGameMoneyServices.Account;
                    AddMoney(player, payout);
                    int newBalance = player.InGameMoneyServices.Account;

                    player.PrintToChat(
                        $"[TeamBets]: You won ${profit}! Total payout: ${payout}. " +
                        $"Bet was on {(winningTeam == TEAM_T ? "T" : "CT")} at {bet.Odds:0.00} odds. " +
                        $"Balance: ${oldBalance} -> ${newBalance}"
                    );

                    Console.WriteLine(
                        $"[DEBUG] OSBase[{ModuleName}] Bet WON by {player.PlayerName}: " +
                        $"amount={bet.Amount} payout={payout} profit={profit} odds={bet.Odds:0.00} balance={oldBalance}->{newBalance}"
                    );
                } else {
                    player.PrintToChat(
                        $"[TeamBets]: You lost ${bet.Amount} betting on {(bet.Team == TEAM_T ? "T" : "CT")} " +
                        $"at {bet.Odds:0.00} odds."
                    );

                    Console.WriteLine(
                        $"[DEBUG] OSBase[{ModuleName}] Bet LOST by {player.PlayerName}: " +
                        $"amount={bet.Amount} team={(bet.Team == TEAM_T ? "T" : "CT")} odds={bet.Odds:0.00}"
                    );
                }
            }

            betters.Clear();
            return HookResult.Continue;
        }

        private bool IsWarmupActive() {
            return GameStats.Current?.IsWarmup ?? true;
        }

        private void AddMoney(CCSPlayerController player, int amount) {
            if (player == null || !player.IsValid || player.InGameMoneyServices == null)
                return;

            int finalAmount = player.InGameMoneyServices.Account + amount;
            finalAmount = Math.Clamp(finalAmount, MinMoney, MaxMoney);
            player.InGameMoneyServices.Account = finalAmount;
        }

        private void RemoveMoney(CCSPlayerController player, int amount) {
            AddMoney(player, -amount);
        }

        private bool IsOnATeam(CsTeam team) {
            return team == CsTeam.Terrorist || team == CsTeam.CounterTerrorist;
        }
    }
}