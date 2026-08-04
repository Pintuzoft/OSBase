using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using MySqlConnector;
using OSBase.Helpers;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace OSBase.Modules;

public class TeamBets : ModuleBase {
    public override string ModuleName => "teambets";

    private const int TeamT = (int)CsTeam.Terrorist;
    private const int TeamCt = (int)CsTeam.CounterTerrorist;
    private const int MinMoney = 0;
    private const int MaxMoney = 16000;
    private const float LeaderboardDelaySeconds = 1.5f;
    private const string TeamBetStatTable = "player_teambet_stat";
    private const string TeamBetLogTable = "player_teambet_log";

    private bool roundLive = false;

    // Ask 11's gate, duplicated here (own module, own config) -- decided at round start, held
    // for the whole round. Same reasoning as DamageReport.cs: two players farming bets on an
    // empty pub server must never reach the same lifetime counters as real play.
    private bool statsGateOpen;
    private int minPlayers = 4;

    private Database? db;
    private bool flushInProgress;
    private EloRating? eloRating;
    private GameStats? gameStats;

    // osbase-stat-contracts.md section 5, same fix as DamageReport.cs/EloRating.cs: delay
    // the flush's transaction off the exact round-end tick instead of firing with it.
    private const float RoundEndFlushDelaySeconds = 2.0f;
    private Timer? pendingFlushTimer;
    private readonly Dictionary<(ulong SteamId64, string Season), PendingTeamBetCounter> pendingTeamBetCounters = new();
    private readonly List<PendingTeamBetLogRow> pendingTeamBetLogRows = new();

    private sealed class PendingTeamBetCounter {
        public int Bets;
        public int Wins;
        public long Staked;
        public long Returned;
        public int BiggestWin;
        public int BiggestWinStake;
        public DateTime? BiggestWinAt;
    }

    // One row per resolved bet -- browsable log, never aggregated in memory like the
    // counter above. "outcome" is one of "won"/"lost"/"void"; payout is always a concrete
    // number, 0 on a loss or void, never NULL (osbase-stat-contracts.md section 3).
    private sealed class PendingTeamBetLogRow {
        public required ulong SteamId64;
        public required DateTime Stamp;
        public int? MatchId;
        public required string Mapname;
        public required int Round;
        public required int Pick;
        public required int Stake;
        public required int Payout;
        public required string Outcome;
    }

    // userid -> bet
    private readonly Dictionary<int, Bet> bets = new();

    private class Bet {
        public ulong SteamId64 { get; }
        public string PlayerName { get; }
        public int Amount { get; }
        public int Team { get; }
        public float Odds { get; }
        public int AliveT { get; }
        public int AliveCt { get; }

        // Captured at placement time (not resolution) -- "when" for the log means when the
        // person made the pick, and match_id/mapname/round place it in a session even if the
        // bet resolves a few seconds later at round end.
        public DateTime Stamp { get; }
        public int? MatchId { get; }
        public string Mapname { get; }
        public int Round { get; }

        public Bet(ulong steamId64, string playerName, int amount, int team, float odds, int aliveT, int aliveCt,
                   DateTime stamp, int? matchId, string mapname, int round) {
            SteamId64 = steamId64;
            PlayerName = playerName;
            Amount = amount;
            Team = team;
            Odds = odds;
            AliveT = aliveT;
            AliveCt = aliveCt;
            Stamp = stamp;
            MatchId = matchId;
            Mapname = mapname;
            Round = round;
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

    protected override void OnLoad() {
        bets.Clear();
        roundLive = false;

        CreateCustomConfigs();
        LoadConfig();

        db = new Database(osbase!, config!);
        CreateTable();
        eloRating = osbase?.GetModule<EloRating>();
        gameStats = osbase?.GetGameStats();
    }

    protected override void OnUnload() {
        pendingFlushTimer?.Kill();
        pendingFlushTimer = null;
        FlushPendingTeamBetStats("Unload");

        bets.Clear();
        roundLive = false;
        db = null;
    }

    protected override void OnReloadConfig() {
        CreateCustomConfigs();
        LoadConfig();
    }

    protected override void RegisterHandlers() {
        // Use new EventBus system for bomb/round events
        osbase?.SubscribeToEvent<EventRoundStart>(OnRoundStart);
        osbase?.SubscribeToEvent<EventRoundEnd>(OnRoundEnd);
        osbase?.SubscribeToEvent<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        osbase?.SubscribeToEvent<EventPlayerChat>(OnPlayerChat);
        osbase?.RegisterListener<Listeners.OnMapStart>(OnMapStart);
    }

    protected override void UnregisterHandlers() {
        // Use new EventBus system for bomb/round events
        osbase?.UnsubscribeFromEvent<EventRoundStart>(OnRoundStart);
        osbase?.UnsubscribeFromEvent<EventRoundEnd>(OnRoundEnd);
        osbase?.UnsubscribeFromEvent<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        osbase?.UnsubscribeFromEvent<EventPlayerChat>(OnPlayerChat);
        osbase?.RemoveListener<Listeners.OnMapStart>(OnMapStart);
    }

    private void OnMapStart(string mapName) {
        if (!isActive) {
            return;
        }

        pendingFlushTimer?.Kill();
        pendingFlushTimer = null;
        FlushPendingTeamBetStats("MapStart");
    }

    // ----- config (teambets.cfg) -----

    private void CreateCustomConfigs() {
        config?.CreateCustomConfig(
            $"{ModuleName}.cfg",
            "// TeamBets Configuration\n" +
            "// Gate for the durable player_teambet_stat table, same rule as DamageReport's\n" +
            "// ask 11: decided once at round start, held for the whole round. Warmup is\n" +
            "// always excluded and not configurable; min_players is, because the right\n" +
            "// threshold isn't known until there's real data to look at.\n" +
            "min_players 4\n"
        );
    }

    private void LoadConfig() {
        minPlayers = 4;

        List<string> cfg = config?.FetchCustomConfig($"{ModuleName}.cfg") ?? new List<string>();

        foreach (var rawLine in cfg) {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) {
                continue;
            }

            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) {
                Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Invalid config line skipped: {line}");
                continue;
            }

            string key = parts[0].Trim();
            string value = parts[1].Trim();

            switch (key.ToLowerInvariant()) {
                case "min_players":
                    minPlayers = ParseInt(value, 4, 0, 64);
                    break;
                default:
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Unknown config key {key}:{value}");
                    break;
            }
        }
    }

    private static int ParseInt(string value, int defaultValue, int min, int max) {
        if (!int.TryParse(value, out int parsed)) {
            return defaultValue;
        }

        return Math.Clamp(parsed, min, max);
    }

    private static int CountConnectedHumans() {
        return Utilities.GetPlayers().Count(p =>
            p != null && p.IsValid && !p.IsHLTV && !p.IsBot && p.Connected == PlayerConnectedState.Connected
        );
    }

    // Extracted to SeasonHelper 2026-08-04 (agent-chat #33) -- see that helper's comment.
    private static string CurrentSeason() => SeasonHelper.CurrentSeason();

    // player_teambet_stat is owned by this module alone. staked/returned are kept separate,
    // never netted -- net is a subtraction away, but you can't split it back into how much
    // someone risked, and the player churning 10x the volume for the same profit is the more
    // interesting one. biggest_win is NET profit on one winning bet (returned - staked for
    // that bet), not the total payout -- a topplist of total payout is really a wallet-size
    // topplist (staking 10000 to get back 10100 would top it over risking 100 to win 4900,
    // and the second one is the story people actually retell). biggest_win_stake is what was
    // risked for that specific win, kept alongside so the payout is recoverable
    // (biggest_win + biggest_win_stake) and the odds are visible in the retelling ("won 4900
    // on a hundred-dollar bet") -- otherwise that stake is exactly the kind of fact no later
    // migration could dig back out, same rule as everything else in this document.
    private void CreateTable() {
        if (db == null) {
            return;
        }

        string teamBetStatTable = $"""
        TABLE IF NOT EXISTS {TeamBetStatTable} (
            steamid64          VARCHAR(32) NOT NULL,
            season             VARCHAR(8) NOT NULL,
            bets               INT NOT NULL DEFAULT 0,
            wins               INT NOT NULL DEFAULT 0,
            staked             BIGINT NOT NULL DEFAULT 0,
            returned           BIGINT NOT NULL DEFAULT 0,
            biggest_win        INT NOT NULL DEFAULT 0,
            biggest_win_stake  INT NOT NULL DEFAULT 0,
            biggest_win_at     DATETIME NULL,
            first_seen         DATETIME NOT NULL,
            updated_at         DATETIME NOT NULL,
            PRIMARY KEY (steamid64, season)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        // osbase-stat-contracts.md section 3: player_teambet_stat stays the running-total
        // counter above; this is the browsable log alongside it ("show me how I bet during
        // July"), one row per resolved bet, never merged/summed. Index on (steamid64, stamp)
        // from day one -- without it, that query is a full scan of a table growing toward
        // ~2M rows/year (confirmed sizing in the contract doc).
        string teamBetLogTable = $"""
        TABLE IF NOT EXISTS {TeamBetLogTable} (
            id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
            steamid64  VARCHAR(32) NOT NULL,
            stamp      DATETIME NOT NULL,
            match_id   INT NULL,
            mapname    VARCHAR(64) NOT NULL,
            round      INT NOT NULL DEFAULT 0,
            pick       TINYINT UNSIGNED NOT NULL,
            stake      INT NOT NULL,
            payout     INT NOT NULL DEFAULT 0,
            outcome    VARCHAR(8) NOT NULL,
            PRIMARY KEY (id),
            KEY idx_steamid_stamp (steamid64, stamp)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """;

        try {
            db.create(teamBetStatTable);
            db.create(teamBetLogTable);
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] table ensured.");
        } catch (Exception e) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] failed creating table: {e.Message}");
        }
    }

    private void AddTeamBetResult(ulong steamId64, string season, bool won, int staked, int returned) {
        if (steamId64 == 0) {
            return;
        }

        var key = (steamId64, season);
        if (!pendingTeamBetCounters.TryGetValue(key, out var counter)) {
            counter = new PendingTeamBetCounter();
            pendingTeamBetCounters[key] = counter;
        }

        counter.Bets += 1;
        counter.Staked += staked;
        counter.Returned += returned;

        if (won) {
            counter.Wins += 1;

            int net = returned - staked;
            if (net > counter.BiggestWin) {
                counter.BiggestWin = net;
                counter.BiggestWinStake = staked;
                counter.BiggestWinAt = DateTime.UtcNow;
            }
        }
    }

    // outcome is "won"/"lost"/"void" (osbase-stat-contracts.md section 3's exact enum).
    // Called once per bet per resolution, independent of the counter above -- a log row
    // is never merged with another, so there's nothing to look up/accumulate here.
    private void LogTeamBet(Bet bet, string outcome, int payout) {
        if (bet.SteamId64 == 0) {
            return;
        }

        pendingTeamBetLogRows.Add(new PendingTeamBetLogRow {
            SteamId64 = bet.SteamId64,
            Stamp = bet.Stamp,
            MatchId = bet.MatchId,
            Mapname = bet.Mapname,
            Round = bet.Round,
            Pick = bet.Team,
            Stake = bet.Amount,
            Payout = payout,
            Outcome = outcome,
        });
    }

    // Buffered like DamageReport/EloRating: accumulate, flush between rounds. Unwritten rows
    // on a DB outage are merged back and retried, with biggest_win/biggest_win_at kept as the
    // max seen across whatever pending batches haven't landed yet.
    private void FlushPendingTeamBetStats(string source) {
        var database = db;
        if (database == null || flushInProgress || (pendingTeamBetCounters.Count == 0 && pendingTeamBetLogRows.Count == 0)) {
            return;
        }

        var batch = pendingTeamBetCounters.ToList();
        pendingTeamBetCounters.Clear();

        var logBatch = pendingTeamBetLogRows.ToList();
        pendingTeamBetLogRows.Clear();

        flushInProgress = true;

        Task.Run(() => {
            // Perf fix (osbase-stat-contracts.md section 5), same as DamageReport.cs/
            // EloRating.cs: one transaction instead of one connection/round-trip per row,
            // and the caller now delays invoking this flush so it lands off the exact
            // round-end tick. All-or-nothing per flush; on failure the whole batch (both
            // the counter and the log) goes back for retry.
            var writes = new List<(string query, MySqlParameter[] parameters)>();

            foreach (var row in logBatch) {
                writes.Add(($"INTO {TeamBetLogTable} (steamid64, stamp, match_id, mapname, round, pick, stake, payout, outcome) " +
                    "VALUES (@steamid64, @stamp, @match_id, @mapname, @round, @pick, @stake, @payout, @outcome)",
                    new MySqlParameter[] {
                        new("@steamid64", row.SteamId64.ToString()),
                        new("@stamp", row.Stamp),
                        new("@match_id", (object?)row.MatchId ?? DBNull.Value),
                        new("@mapname", row.Mapname),
                        new("@round", row.Round),
                        new("@pick", row.Pick),
                        new("@stake", row.Stake),
                        new("@payout", row.Payout),
                        new("@outcome", row.Outcome)
                    }));
            }

            foreach (var kv in batch) {
                var (steamId64, season) = kv.Key;
                var counter = kv.Value;

                writes.Add(($"INTO {TeamBetStatTable} (steamid64, season, bets, wins, staked, returned, biggest_win, biggest_win_stake, biggest_win_at, first_seen, updated_at) " +
                    "VALUES (@steamid64, @season, @bets, @wins, @staked, @returned, @biggest_win, @biggest_win_stake, @biggest_win_at, NOW(), NOW()) " +
                    "ON DUPLICATE KEY UPDATE " +
                    "bets=bets+@bets, wins=wins+@wins, staked=staked+@staked, returned=returned+@returned, " +
                    "biggest_win_at=IF(@biggest_win > biggest_win, @biggest_win_at, biggest_win_at), " +
                    "biggest_win_stake=IF(@biggest_win > biggest_win, @biggest_win_stake, biggest_win_stake), " +
                    "biggest_win=GREATEST(biggest_win, @biggest_win), updated_at=NOW()",
                    new MySqlParameter[] {
                        new("@steamid64", steamId64.ToString()),
                        new("@season", season),
                        new("@bets", counter.Bets),
                        new("@wins", counter.Wins),
                        new("@staked", counter.Staked),
                        new("@returned", counter.Returned),
                        new("@biggest_win", counter.BiggestWin),
                        new("@biggest_win_stake", counter.BiggestWinStake),
                        new("@biggest_win_at", (object?)counter.BiggestWinAt ?? DBNull.Value)
                    }));
            }

            bool ok = writes.Count == 0 || database.ExecuteTransaction(writes) > 0;

            var unwritten = ok ? new() : batch;
            var unwrittenLogRows = ok ? new() : logBatch;

            Server.NextFrame(() => {
                flushInProgress = false;

                foreach (var kv in unwritten) {
                    if (!pendingTeamBetCounters.TryGetValue(kv.Key, out var existing)) {
                        pendingTeamBetCounters[kv.Key] = kv.Value;
                    } else {
                        existing.Bets += kv.Value.Bets;
                        existing.Wins += kv.Value.Wins;
                        existing.Staked += kv.Value.Staked;
                        existing.Returned += kv.Value.Returned;
                        if (kv.Value.BiggestWin > existing.BiggestWin) {
                            existing.BiggestWin = kv.Value.BiggestWin;
                            existing.BiggestWinStake = kv.Value.BiggestWinStake;
                            existing.BiggestWinAt = kv.Value.BiggestWinAt;
                        }
                    }
                }

                pendingTeamBetLogRows.AddRange(unwrittenLogRows);

                if (unwritten.Count > 0 || unwrittenLogRows.Count > 0) {
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}] database unavailable ({source}): kept {unwritten.Count} teambet-stat row(s) and {unwrittenLogRows.Count} teambet-log row(s) cached for retry.");
                } else {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] flushed pending teambet writes ({source}): statRows={batch.Count}, logRows={logBatch.Count}");
                }
            });
        });
    }

    private HookResult OnPlayerChat(EventPlayerChat eventInfo) {
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
            player.SteamID,
            playerName,
            amount,
            betTeam,
            odds,
            aliveT,
            aliveCt,
            DateTime.UtcNow,
            eloRating?.CurrentMatchId,
            Server.MapName ?? osbase?.currentMap ?? string.Empty,
            gameStats?.roundNumber ?? 0
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

        // Ask 11's gate, decided here and held for the whole round -- see the field comment.
        bool warmup = IsWarmupActive();
        int humans = CountConnectedHumans();
        statsGateOpen = !warmup && humans >= minPlayers;
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: round gate {(statsGateOpen ? "open" : "closed")} (humans={humans} min={minPlayers} warmup={warmup})");

        // osbase-stat-contracts.md section 5's flush-on-the-way-out requirement: bounds the
        // backlog to at most one round even if the round-end delay timer below never fires.
        pendingFlushTimer?.Kill();
        pendingFlushTimer = null;
        FlushPendingTeamBetStats("RoundStart");

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
        string season = CurrentSeason();

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

                if (statsGateOpen) {
                    AddTeamBetResult(bet.SteamId64, season, won: true, staked: bet.Amount, returned: actualPaid);
                    LogTeamBet(bet, outcome: "won", payout: actualPaid);
                }

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

                if (statsGateOpen) {
                    AddTeamBetResult(bet.SteamId64, season, won: false, staked: bet.Amount, returned: 0);
                    LogTeamBet(bet, outcome: "lost", payout: 0);
                }

                Console.WriteLine(
                    $"[DEBUG] OSBase[{ModuleName}] Bet LOST by {bet.PlayerName}: " +
                    $"amount={bet.Amount} team={GetTeamName(bet.Team)} odds={bet.Odds:0.00} online={playerOnline}"
                );
            }
        }

        PrintBetLeaderboardDelayed(winningTeam, results);

        // Capture is already done above; only the flush's transaction is delayed off the
        // exact round-end tick (osbase-stat-contracts.md section 5).
        pendingFlushTimer?.Kill();
        pendingFlushTimer = osbase?.AddTimer(RoundEndFlushDelaySeconds, () => {
            pendingFlushTimer = null;
            FlushPendingTeamBetStats("RoundEnd");
        });

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

                // Still a void bet even though the money never made it back -- the boring
                // case osbase-stat-contracts.md section 3 asks to be consistent about.
                // payout=0 here for the same reason a disconnected win pays 0: nothing
                // actually came back to them.
                if (statsGateOpen) {
                    LogTeamBet(bet, outcome: "void", payout: 0);
                }
                continue;
            }

            AddMoney(player, bet.Amount);
            BroadcastToChat($"[TeamBets]: {bet.PlayerName} was refunded ${bet.Amount}.");

            if (statsGateOpen) {
                LogTeamBet(bet, outcome: "void", payout: bet.Amount);
            }
        }
    }

    private int CountAlivePlayers(int teamNum) {
        return Utilities.GetPlayers()
            .Count(p => p != null && p.IsValid && p.TeamNum == teamNum && p.PawnIsAlive);
    }

    private bool IsWarmupActive() {
        return osbase?.GetGameStats()?.IsWarmup ?? true;
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