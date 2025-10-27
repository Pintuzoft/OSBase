using System;
using System.Linq;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Config;
using CounterStrikeSharp.API.Core.Attributes.Registration;

namespace OSBase.Modules {

    public class TeamBalancer : IModule {
        public string ModuleName => "teambalancer";   
        public string ModuleNameNice => "TeamBalancer";

        private OSBase? osbase;
        private Config? config;
        private GameStats? gameStats;

        private const int TEAM_T  = (int)CsTeam.Terrorist;        // 2
        private const int TEAM_CT = (int)CsTeam.CounterTerrorist; // 3

        private readonly Dictionary<string, int> mapBombsites = new();
        private const string mapConfigFile = "teambalancer_mapinfo.cfg";
        private int bombsites = 2;

        private const float postRoundDelay = 6.5f;
        //private bool warmup = false;
        private bool halftimePending = false;
        private const int HALFTIME_ROUND = 10;
        
        private enum BalancePhase { WarmupEnd, HalftimeStart, RoundEnd }

        public void Load(OSBase inOsbase, Config inConfig) {
            osbase  = inOsbase;
            config  = inConfig;
            gameStats = osbase.GetGameStats();

            if (osbase == null || config == null) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] osbase/config is null.");
                return;
            }

            config.RegisterGlobalConfigValue($"{ModuleName}", "1");
            var enabled = config.GetGlobalConfigValue($"{ModuleName}", "0") == "1";
            if (!enabled) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled via config.");
                return;
            }

            RegisterHandlers();
            LoadMapInfo();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded.");
        }

        private void RegisterHandlers() {
            osbase!.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
            osbase!.RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
            osbase!.RegisterEventHandler<EventRoundStart>(OnRoundStartPre, HookMode.Pre);
            osbase!.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        }

        private void LoadMapInfo() {
            config?.CreateCustomConfig($"{mapConfigFile}", "// Map info\nde_dust2 2\n");
            var lines = config?.FetchCustomConfig($"{mapConfigFile}") ?? new List<string>();
            foreach (var raw in lines) {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//")) continue;
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[1], out int bs))
                    mapBombsites[parts[0]] = bs;
            }
        }

        private void OnMapStart(string mapName) {
            //warmup = true;
            halftimePending = false;

            if (mapBombsites.TryGetValue(mapName, out var bs)) {
                bombsites = bs;
            } else {
                bombsites = mapName.Contains("cs_") ? 1 : 2; // cs_* maps treated as 1-site (hostage) maps
                config?.AddCustomConfigLine($"{mapConfigFile}", $"{mapName} {bombsites}");
            }
        }

        [GameEventHandler(HookMode.Post)]
        private HookResult OnRoundEnd(EventRoundEnd ev, GameEventInfo info) {
            try {
                int round = gameStats?.roundNumber ?? 0;
                if (round == HALFTIME_ROUND) halftimePending = true; // mark halftime boundary after round 15 ends
            } catch { /* ignore */ }

            osbase?.AddTimer(postRoundDelay, () => BalanceTeams(BalancePhase.RoundEnd));
            return HookResult.Continue;
        }

        [GameEventHandler(HookMode.Pre)]
        private HookResult OnRoundStartPre(EventRoundStart ev, GameEventInfo info) {
            if (halftimePending) {
                BalanceTeams(BalancePhase.HalftimeStart); // before the first round after halftime
                halftimePending = false;
            }
            return HookResult.Continue;
        }

        [GameEventHandler(HookMode.Pre)]
        private HookResult OnWarmupEnd(EventWarmupEnd ev, GameEventInfo info) {
            if (gameStats == null) return HookResult.Continue;
            BalanceTeams(BalancePhase.WarmupEnd); // fix sizes on warmup end (odd totals handled)
            //warmup = false;
            return HookResult.Continue;
        }

        private (int idealT, int idealCT) ComputeIdealSizes(int tCount, int ctCount) {
            int total = tCount + ctCount;
            if (bombsites == 2) {
                // Defenders get extra on 2-site maps
                return (total / 2, total / 2 + total % 2);
            } else {
                // Attackers get extra on 1-site (hostage) maps
                return (total / 2 + total % 2, total / 2);
            }
        }

        private float GetDynamicThreshold() {
            int round = gameStats?.roundNumber ?? 0;
            return round switch {
                1 => 10000f, 
                2 => 10000f, 
                3 => 8000f, 
                4 => 7000f, 
                5 => 1000f,
                6 => 2000f, 
                7 => 5000f, 
                8 => 4000f, 
                9 => 5000f, 
                10 => 5000f,
                11 => 4000f, 
                12 => 3000f, 
                13 => 2000f, 
                14 => 3000f, 
                15 => 5000f,
                16 => 5000f, 
                17 => 6000f, 
                18 => 7000f, 
                19 => 8000f, 
                20 => 9000f,
                _ => 8000f + (round - 20) * 200f
            };
        }

        private void BalanceTeams(BalancePhase phase) {
            var gs = osbase?.GetGameStats();
            if (gs == null) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] BalanceTeams: GameStats is null.");
                return;
            }

            var tStats = gs.getTeam(TEAM_T);
            var ctStats = gs.getTeam(TEAM_CT);

            int tCount = tStats.numPlayers();
            int ctCount = ctStats.numPlayers();
            var (idealT, idealCT) = ComputeIdealSizes(tCount, ctCount);
            bool sizeOk = (tCount == idealT && ctCount == idealCT);

            // Use cached 90d team means (streak bias applied inside GameStats)
            float tAvg90 = gs.TeamAverage90d(TEAM_T);
            float ctAvg90 = gs.TeamAverage90d(TEAM_CT);

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Phase={phase} T:{tCount}({tAvg90:F0}) CT:{ctCount}({ctAvg90:F0}) IdealT:{idealT} IdealCT:{idealCT}");

            // At WarmupEnd/HalftimeStart: fix sizes first (handles odd totals), then consider skill
            if (!sizeOk) {
                bool moveFromT = tCount > idealT;
                int toMove = Math.Abs(moveFromT ? (tCount - idealT) : (ctCount - idealCT));
                evenTeamSizes(gs, moveFromT, tAvg90, ctAvg90, toMove, tStats, ctStats);
            }

            if (phase == BalancePhase.WarmupEnd) return; // no aggressive skill swaps during warmup

            float diff = Math.Abs(tAvg90 - ctAvg90);
            if (diff > GetDynamicThreshold()) {
                doSkillBalance(gs, tStats, ctStats);
            }
        }

        // Even sizes by picking players whose cached 90d skill best matches needed delta per move.
        private void evenTeamSizes(GameStats gs, bool moveFromT, float tAvg90, float ctAvg90, int toMove, TeamStats tStats, TeamStats ctStats) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] evenTeamSizes: moving {toMove} → {(moveFromT ? "CT" : "T")}");
            float totalDelta = (moveFromT ? tAvg90 - ctAvg90 : ctAvg90 - tAvg90);
            float perTarget  = toMove > 0 ? totalDelta / Math.Max(1, toMove) : 0f;

            for (int i = 0; i < toMove; i++) {
                var src = moveFromT ? tStats : ctStats;

                int bestUid = -1;
                float bestDiff = float.MaxValue;

                foreach (var kv in src.playerList) {
                    if (kv.Value.immune > 0) continue;
                    float s90 = gs.GetCached90dByUserId(kv.Key); // cached (fallback: live calc)
                    float d = Math.Abs(s90 - perTarget);
                    if (d < bestDiff) { bestDiff = d; bestUid = kv.Key; }
                }

                if (bestUid == -1) {
                    // Fallback to your helper
                    var pick = src.getPlayerBySkill(perTarget);
                    if (pick?.UserId.HasValue == true) bestUid = pick.UserId.Value;
                }

                if (bestUid == -1) {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] evenTeamSizes: no candidate.");
                    break;
                }

                var player = Utilities.GetPlayerFromUserid(bestUid);
                if (player == null || !player.UserId.HasValue) continue;

                movePlayer(gs, player, moveFromT ? TEAM_CT : TEAM_T, tStats, ctStats);
                Server.PrintToChatAll($"[TeamBalancer] Moved: {player.PlayerName} {(moveFromT ? "T→CT" : "CT→T")}");
            }
        }

        // Swap 2 players based on cached 90d difference. Validate that strong>weak by 90d before swapping.
        private void doSkillBalance(GameStats gs, TeamStats tStats, TeamStats ctStats) {
            float tAvg = gs.TeamAverage90d(TEAM_T);
            float ctAvg = gs.TeamAverage90d(TEAM_CT);
            float diff = Math.Abs(tAvg - ctAvg);
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] doSkillBalance: Δ={diff:F0} thr={GetDynamicThreshold():F0}");
            if (diff < GetDynamicThreshold()) return;

            bool tStrong = tAvg > ctAvg;
            float strongTarget = diff;
            float weakTarget   = diff / 2;

            var strongCand = tStrong ? tStats.GetPlayerByDeviation(strongTarget, true) : ctStats.GetPlayerByDeviation(strongTarget, true);
            var weakCand   = tStrong ? ctStats.GetPlayerByDeviation(weakTarget, false) : tStats.GetPlayerByDeviation(weakTarget, false);

            if (strongCand == null || weakCand == null) return;
            if (!strongCand.UserId.HasValue || !weakCand.UserId.HasValue) return;

            float strong90 = gs.GetCached90dByUserId(strongCand.UserId.Value);
            float weak90   = gs.GetCached90dByUserId(weakCand.UserId.Value);
            if (strong90 <= weak90) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] doSkillBalance: strong≤weak (90d), skip.");
                return;
            }

            movePlayer(gs, strongCand, tStrong ? TEAM_CT : TEAM_T, tStats, ctStats);
            movePlayer(gs, weakCand,   tStrong ? TEAM_T  : TEAM_CT, tStats, ctStats);
            Server.PrintToChatAll($"[TeamBalancer] Swapped: {strongCand.PlayerName} ↔ {weakCand.PlayerName}");
        }

        private void movePlayer(GameStats gs, CCSPlayerController player, int targetTeam, TeamStats tStats, TeamStats ctStats) {
            if (player == null || !player.UserId.HasValue) return;

            // Debug before
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - T roster:");
            tStats.printPlayers();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - CT roster:");
            ctStats.printPlayers();

            player.SwitchTeam((CsTeam)targetTeam);
            gs.movePlayer(player.UserId.Value, targetTeam);

            // short immunity to avoid ping-ponging
            gs.GetPlayerStats(player.UserId.Value).immune += 2;

            player.PrintToCenterAlert($"!! YOU HAVE BEEN MOVED TO {(targetTeam == TEAM_T ? "T" : "CT")} !!");
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] movePlayer: {player.PlayerName} → {(targetTeam == TEAM_T ? "T" : "CT")}");

            // Debug after
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - T roster (post):");
            tStats.printPlayers();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - CT roster (post):");
            ctStats.printPlayers();
        }
    }
}