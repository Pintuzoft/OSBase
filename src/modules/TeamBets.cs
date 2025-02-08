using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using System.Reflection.Metadata;

namespace OSBase.Modules {

    public class TeamBets : IModule {
        public string ModuleName => "teambets";
        private OSBase? osbase;
        private Config? config;
        private const int TEAM_T = (int)CsTeam.Terrorist;
        private const int TEAM_CT = (int)CsTeam.CounterTerrorist;


        // A hilariously named collection for dead betters
        private Dictionary<int, Bet> betters = new Dictionary<int, Bet>();

        // Simple bet structure
        private class Bet {
            public int amount { get; set; }
            public int team { get; set; } 
            public Bet(int inAmount, int inTeam) {
                amount = inAmount;
                team = inTeam;
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
                loadEventHandlers();
                osbase.AddCommand("bet", "bet", handleBetCommand);
                osbase.RegisterEventHandler<EventPlayerChat>(OnPlayerChat);
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
            } else {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {ModuleName} is disabled in the global configuration.");
            }
        }

        private void loadEventHandlers() {
            if (osbase == null)
                return;

            // When the round ends, we process all the bets
            osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
            // Also clear any lingering bets on round start
            osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        }

        private HookResult OnPlayerChat(EventPlayerChat eventInfo, GameEventInfo gameEventInfo) {
            Console.WriteLine("[DEBUG] TeamBets: OnPlayerChat");

            return HookResult.Continue;
        }

        // Command format: bet <t/ct> <amount|all>
        private void handleBetCommand(CCSPlayerController? player, CommandInfo? commandInfo) {
            Console.WriteLine("[DEBUG] TeamBets: handleBetCommand");
            if (player != null) {
                player.PrintToChat("[TeamBets]: WooHoo! you are here!!");
            }

            // Check if the player is valid and has an InGameMoneyServices instance
            if ( player == null || 
                 player.IsValid == false || 
                 ! player.UserId.HasValue || 
                 player.InGameMoneyServices == null ) {
			    return;
            }

            // Check if the player is alive
            if (commandInfo == null || commandInfo.ArgCount < 3) {
                player.PrintToChat("[TeamBets]: Usage: bet <t/ct> <amount|all|half>");
                return;
            }

            // Check if the player is alive
            if ( player.PawnIsAlive ) {
                player.PrintToChat("[TeamBets]: You must be dead to bet on the round!");
                return;
            }

            // Check if the player is on a team
            if ( ! isOnATeam(player.Team) ) {
                player.PrintToChat("[TeamBets]: You must be on a team to bet on the round!");
                return;
            }

            // Check if the player has already placed a bet
            if ( betters.ContainsKey(player.UserId.Value) ) {
                player.PrintToChat("[TeamBets]: You've already placed your bet this round!");
                return;
            }

            // Check if the player bet on a valid team
            string teamBetName = commandInfo.ArgByIndex(0).ToLower();
            int teamBet = 0;
            if ( teamBetName == "t" ) {
                teamBet = TEAM_T;
            } else if ( teamBetName == "ct" ) {
                teamBet = TEAM_CT;
            } else {
                player.PrintToChat("[TeamBets]: Invalid team. Use 't' or 'ct'.");
                return;
            }

            // Check if the player bet a valid amount
            string betInput = commandInfo.ArgByIndex(1).ToLower();
            int betAmount = 0;
            int cashCow = player.InGameMoneyServices?.Account ?? 0;

            if (betInput == "all") {
                betAmount = cashCow;
            } else if (betInput == "half") {
                betAmount = (int)Math.Round(cashCow / 2f);
            } else if (!int.TryParse(betInput, out betAmount)) {
                player.PrintToChat("[TeamBets]: Invalid bet amount!");
                return;
            }
            
            if (betAmount <= 0) {
                player.PrintToChat("[TeamBets]: Bet amount must be greater than 0!");
                return;
            }

            // Check if the player has enough cash to bet
            if (cashCow < betAmount) {
                player.PrintToChat("[TeamBets]: You don't have enough cash to bet that amount!");
                return;
            }

            // Place the bet
            betters.Add(player.UserId.Value, new Bet(betAmount, teamBet));

            // Deduct the bet
            player.RemoveMoney(betAmount);

            // Announce the bet
            player.PrintToChat($"[TeamBets]: ${betAmount} on {(teamBet == TEAM_T ? "Terrorists" : "Counter-Terrorists")}.");
            return;
        }

        // Clear bets at the start of a new round
        private HookResult OnRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {
            betters.Clear();
            return HookResult.Continue;
        }

        // Process bets at round end
        private HookResult OnRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
            if (osbase == null)
                return HookResult.Continue;

            // Assume eventInfo.WinningTeam is "t" or "ct"
            int winningTeam = eventInfo.Winner;

            int totalT = 0, totalCT = 0;
            foreach (var bet in betters.Values) {
                switch ( bet.team ) {
                    case TEAM_T :
                        totalT += bet.amount;
                        break;

                    case TEAM_CT :
                        totalCT += bet.amount;
                        break;
                }
            }

            // Determine the losers' pot and total winning bets
            int losersPool = winningTeam == TEAM_T ? totalCT : totalT;
            int winnersTotal = winningTeam == TEAM_T ? totalT : totalCT;

            // Distribute winnings proportionally
            foreach (var kvp in betters) {
                int playerId = kvp.Key;
                Bet bet = kvp.Value;
                var player = Utilities.GetPlayerFromUserid(playerId);
                if (player == null)
                    continue;

                if (bet.team == winningTeam) {
                    // Your payout equals your bet plus your share of the losers' pool
                    int payout = bet.amount;
                    if (winnersTotal > 0)
                        payout += (int)((double)bet.amount / winnersTotal * losersPool);

                    player.AddMoney(payout);
                    player.PrintToChat($"[TeamBets]: Congrats! You won ${payout} betting on {(winningTeam == TEAM_T ? "Terrorists" : "Counter-Terrorists")}!");
                } else {
                    player.PrintToChat("[TeamBets]: Your bet lost! Better luck next round.");
                }
            }

            betters.Clear();
            return HookResult.Continue;
        }

        private bool isOnATeam ( CsTeam team ) {
            return team == ( CsTeam.Terrorist | CsTeam.CounterTerrorist );
        }

    }
}