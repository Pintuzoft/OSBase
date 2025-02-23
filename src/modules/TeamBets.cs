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
            public float odds { get; set; }
            public Bet(int inAmount, int inTeam, float inOdds) {
                amount = inAmount;
                team = inTeam;
                odds = inOdds;
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
            if (eventInfo == null || eventInfo?.Userid == null) {
                return HookResult.Continue;
            }

            CCSPlayerController? player = eventInfo?.Userid != null ? Utilities.GetPlayerFromUserid(eventInfo.Userid) : null;



            if ( player == null || ! player.IsValid || ! player.UserId.HasValue ) {
                return HookResult.Continue;
            }

            if ( eventInfo != null && eventInfo.Text != null && eventInfo.Text.StartsWith("bet") ) {
                handleBetCommand(player, eventInfo.Text);
            }
            return HookResult.Continue;
        }

        // Command format: bet <t/ct> <amount|all>
        private void handleBetCommand(CCSPlayerController? player, string command) {
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

            // Parse the command
            List<string> cmds = command.Split(' ').ToList();

            // Check if the player is alive
            if (command == null || cmds.Count < 3) {
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
            string teamStr = cmds[1].ToLower();
            string amountStr = cmds[2].ToLower();
            var playersList = Utilities.GetPlayers();
            var terrorists = playersList
                .Where(p => p.TeamNum == TEAM_T)
                .ToList();

            var counterterrorists = playersList
                .Where(p => p.TeamNum == TEAM_CT)
                .ToList();


            int tSize = terrorists.Count;
            int ctSize = counterterrorists.Count;
            int team = 0;
            int amount = 0;
            float odds = 0.0f;


            switch (teamStr) {
                case "t":
                    team = TEAM_T;
                    odds = ctSize / tSize;
                    break;
                case "ct":
                    team = TEAM_CT;
                    odds = tSize / ctSize;
                    break;
                default:
                    player.PrintToChat("[TeamBets]: Invalid team. Use 't' or 'ct'.");
                    return;
            }

            // Check if the player bet a valid amount
            switch (amountStr) {
                case "all":
                    amount = player.InGameMoneyServices.Account;
                    break;
                case "half":
                    amount = (int)Math.Round(player.InGameMoneyServices.Account / 2f);
                    break;
                default:
                    try {
                        amount = int.Parse(amountStr);
                    } catch {
                        player.PrintToChat("[TeamBets]: Invalid bet amount!");
                        return;
                    }
                    break;
            }
            
            if (amount <= 0) {
                player.PrintToChat("[TeamBets]: Bet amount must be greater than 0!");
                return;

            } else if (amount > player.InGameMoneyServices.Account) {
                player.PrintToChat("[TeamBets]: You don't have enough cash to bet that amount!");
                return;
            } else {
                betters.Add(player.UserId.Value, new Bet(amount, team, odds));
                player.RemoveMoney(amount);
                player.PrintToChat($"[TeamBets]: ${amount} on {(team == TEAM_T ? "Terrorists" : "Counter-Terrorists")}");
            }

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