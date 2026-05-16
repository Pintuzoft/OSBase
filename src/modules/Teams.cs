using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using MySqlConnector;

namespace OSBase.Modules {
    public class Teams : IModule {
        public string ModuleName => "teams";

        private static Teams? teams = null;
        private static bool matchActive = false;

        private OSBase? osbase;
        private Config? config;
        private Database? db;

        private bool handlersLoaded = false;
        private bool isActive = false;
        private bool warmupActive = false;

        private CounterStrikeSharp.API.Modules.Timers.Timer? pendingCheckTeamsTimer;
        private CounterStrikeSharp.API.Modules.Timers.Timer? teamPollTimer;

        private readonly Dictionary<string, TeamInfo> tList = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> teamWins = new(StringComparer.OrdinalIgnoreCase);

        private const int TEAM_T = (int)CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist;
        private const int TEAM_CT = (int)CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist;

        private const float TEAM_DETECT_DELAY = 0.75f;
        private const float TEAM_DETECT_RETRY_DELAY = 1.0f;
        private const float TEAM_POLL_INTERVAL = 1.0f;
        private const float ROUND_START_TEAM_CHECK_DELAY = 0.25f;

        private const int TEAM_DETECT_MAX_RETRIES = 10;
        private const int MIN_SIDE_MATCHES = 1;

        private int teamDetectRetries = 0;

        private TeamInfo currentTTeam = new("Terrorists");
        private TeamInfo currentCTTeam = new("CounterTerrorists");

        private string currentTTeamName = "Terrorists";
        private string currentCTTeamName = "CounterTerrorists";

        private string matchTeamOneName = string.Empty;
        private string matchTeamTwoName = string.Empty;

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

            db = new Database(osbase, config);

            CreateCustomConfigs();
            LoadConfig();
            CreateTables();
            ResetMatchState();
            LoadHandlers();

            teams = this;

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
        }

        public void Unload() {
            isActive = false;

            pendingCheckTeamsTimer?.Kill();
            pendingCheckTeamsTimer = null;

            teamPollTimer?.Kill();
            teamPollTimer = null;

            if (osbase != null && handlersLoaded) {
                osbase.DeregisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
                osbase.DeregisterEventHandler<EventRoundEnd>(OnRoundEnd);
                osbase.DeregisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);
                osbase.DeregisterEventHandler<EventStartHalftime>(OnStartHalftime);
                osbase.DeregisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
                osbase.DeregisterEventHandler<EventRoundStart>(OnRoundStart);

                osbase.RemoveListener<Listeners.OnMapStart>(OnMapStart);
                osbase.RemoveListener<Listeners.OnMapEnd>(OnMapEnd);

                handlersLoaded = false;
            }

            ResetMatchState();
            tList.Clear();
            teamWins.Clear();

            db = null;
            config = null;
            osbase = null;

            if (ReferenceEquals(teams, this)) {
                teams = null;
            }

            matchActive = false;

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] unloaded.");
        }

        public void ReloadConfig(Config inConfig) {
            config = inConfig;

            CreateCustomConfigs();
            LoadConfig();

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] config reloaded.");
        }

        private void LoadHandlers() {
            if (osbase == null || handlersLoaded) {
                return;
            }

            osbase.RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
            osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
            osbase.RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);
            osbase.RegisterEventHandler<EventStartHalftime>(OnStartHalftime);
            osbase.RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
            osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);

            osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);
            osbase.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

            handlersLoaded = true;
        }

        private void OnMapStart(string mapName) {
            ResetMatchState();

            warmupActive = true;

            ApplyScoreboardTeamNameForSide(TEAM_CT, currentCTTeamName);
            ApplyScoreboardTeamNameForSide(TEAM_T, currentTTeamName);

            teamDetectRetries = 0;

            ScheduleCheckTeams(0.25f, allowRetries: true);
            StartTeamPoller();

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] map start: {mapName}, warmup team poll started.");
        }

        private void OnMapEnd() {
            pendingCheckTeamsTimer?.Kill();
            pendingCheckTeamsTimer = null;

            teamPollTimer?.Kill();
            teamPollTimer = null;

            warmupActive = false;
        }

        private HookResult OnWarmupEnd(EventWarmupEnd eventInfo, GameEventInfo gameEventInfo) {
            if (!isActive) {
                return HookResult.Continue;
            }

            warmupActive = false;

            teamPollTimer?.Kill();
            teamPollTimer = null;

            // Final quick check before live match starts. No retry-loop leaking into match.
            ScheduleCheckTeams(0.05f, allowRetries: false);

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] warmup ended, team poll stopped.");

            return HookResult.Continue;
        }

        private HookResult OnRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {
            if (!isActive) {
                return HookResult.Continue;
            }

            if (warmupActive) {
                return HookResult.Continue;
            }

            // One light sanity check during freezetime.
            ScheduleCheckTeams(ROUND_START_TEAM_CHECK_DELAY, allowRetries: false);

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] round start, scheduled freezetime team check.");

            return HookResult.Continue;
        }

        private void CreateCustomConfigs() {
            config?.CreateCustomConfig(
                $"{ModuleName}.cfg",
                "// Teams Configuration\n" +
                "// Ex:\n" +
                "// teamname1 steamid1:steamid2:steamid3:steamid4:steamid5:steamid6\n" +
                "// teamname2 steamid7:steamid8:steamid9:steamid10:steamid11:steamid12\n"
            );
        }

        private void LoadConfig() {
            tList.Clear();

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Loading config...");

            List<string> teamcfg = config?.FetchCustomConfig($"{ModuleName}.cfg") ?? new List<string>();

            foreach (var rawLine in teamcfg) {
                string trimmedLine = rawLine.Trim();

                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//")) {
                    continue;
                }

                var parts = trimmedLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length != 2) {
                    Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Invalid config line skipped: {trimmedLine}");
                    continue;
                }

                string teamName = parts[0].Trim();

                if (string.IsNullOrEmpty(teamName)) {
                    continue;
                }

                TeamInfo team = new(teamName);
                var steamids = parts[1].Split(':', StringSplitOptions.RemoveEmptyEntries);

                foreach (var steamidRaw in steamids) {
                    string steamid = steamidRaw.Trim();

                    try {
                        team.addPlayer(ulong.Parse(steamid));
                        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Added steamid {steamid} to team {teamName}");
                    } catch (Exception e) {
                        Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: Failed to parse steamid {steamid} for team {teamName} -> {e.Message}");
                    }
                }

                tList[teamName] = team;
            }

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Loaded {tList.Count} teams.");
        }

        private void CreateTables() {
            if (db == null) {
                return;
            }

            const string query =
                "CREATE TABLE IF NOT EXISTS teams_match_log (" +
                "matchlog varchar(128), " +
                "datestr datetime" +
                ");";

            try {
                db.create(query);
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - teams_match_log ensured.");
            } catch (Exception e) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error creating table: {e.Message}");
            }
        }

        private HookResult OnRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
            if (!isActive) {
                return HookResult.Continue;
            }

            if (eventInfo.Winner == TEAM_T) {
                IncrementTeamWin(currentTTeamName);
            } else if (eventInfo.Winner == TEAM_CT) {
                IncrementTeamWin(currentCTTeamName);
            }

            RefreshCurrentTeamWinSnapshots();

            Console.WriteLine(
                $"[DEBUG] OSBase[{ModuleName}] - " +
                $"{currentTTeamName}: {GetTeamWins(currentTTeamName)} " +
                $"{currentCTTeamName}: {GetTeamWins(currentCTTeamName)}"
            );

            return HookResult.Continue;
        }

        private HookResult OnStartHalftime(EventStartHalftime eventInfo, GameEventInfo gameEventInfo) {
            if (!isActive) {
                return HookResult.Continue;
            }

            // Halftime/sideswap sanity check. No retry-loop.
            ScheduleCheckTeams(ROUND_START_TEAM_CHECK_DELAY, allowRetries: false);

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] halftime triggered, scheduled team check.");

            return HookResult.Continue;
        }

        private HookResult OnMatchEnd(EventCsWinPanelMatch eventInfo, GameEventInfo gameEventInfo) {
            if (!isActive || db == null) {
                return HookResult.Continue;
            }

            string teamOne = matchTeamOneName;
            string teamTwo = matchTeamTwoName;

            if (string.IsNullOrWhiteSpace(teamOne) || string.IsNullOrWhiteSpace(teamTwo)) {
                if (!IsDefaultTeamName(currentTTeamName) &&
                    !IsDefaultTeamName(currentCTTeamName) &&
                    !string.Equals(currentTTeamName, currentCTTeamName, StringComparison.OrdinalIgnoreCase)) {
                    teamOne = currentTTeamName;
                    teamTwo = currentCTTeamName;
                }
            }

            if (string.IsNullOrWhiteSpace(teamOne) || string.IsNullOrWhiteSpace(teamTwo)) {
                Console.WriteLine($"[WARN] OSBase[{ModuleName}] Match end logging skipped: could not resolve both team names.");
                return HookResult.Continue;
            }

            string logtext = $"{teamOne} [{GetTeamWins(teamOne)}]:[{GetTeamWins(teamTwo)}] {teamTwo}";
            const string query = "INSERT INTO teams_match_log (matchlog, datestr) VALUES (@logtext, NOW());";

            var parameters = new MySqlParameter[] {
                new MySqlParameter("@logtext", logtext)
            };

            try {
                Console.WriteLine($"[DEBUG] Writing to DB: {logtext}");
                db.insert(query, parameters);
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Inserted stats for match: {logtext}");
            } catch (Exception e) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Error inserting into table: {e.Message}");
            }

            matchIsNotActive();
            teamWins.Clear();
            matchTeamOneName = string.Empty;
            matchTeamTwoName = string.Empty;

            currentTTeam.setWins(0);
            currentCTTeam.setWins(0);

            return HookResult.Continue;
        }

        private HookResult OnPlayerTeam(EventPlayerTeam eventInfo, GameEventInfo gameEventInfo) {
            if (!isActive) {
                return HookResult.Continue;
            }

            // Aggressive debounce only during warmup.
            // During live match we avoid extra work and let round_start/freezetime do one sanity check.
            if (!warmupActive) {
                return HookResult.Continue;
            }

            teamDetectRetries = 0;
            ScheduleCheckTeams(TEAM_DETECT_DELAY, allowRetries: true);

            return HookResult.Continue;
        }

        private void ScheduleCheckTeams(float delay, bool allowRetries) {
            if (!isActive || osbase == null) {
                return;
            }

            pendingCheckTeamsTimer?.Kill();
            pendingCheckTeamsTimer = null;

            pendingCheckTeamsTimer = osbase.AddTimer(delay, () => {
                try {
                    pendingCheckTeamsTimer = null;

                    if (!isActive) {
                        return;
                    }

                    bool fullyResolved = CheckTeams();

                    if (allowRetries && !fullyResolved && teamDetectRetries < TEAM_DETECT_MAX_RETRIES) {
                        teamDetectRetries++;

                        Console.WriteLine(
                            $"[DEBUG] OSBase[{ModuleName}]: Team detect not fully resolved, retry " +
                            $"{teamDetectRetries}/{TEAM_DETECT_MAX_RETRIES} in {TEAM_DETECT_RETRY_DELAY}s."
                        );

                        ScheduleCheckTeams(TEAM_DETECT_RETRY_DELAY, allowRetries: true);
                    }
                } catch (Exception e) {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: CheckTeams timer failed: {e.Message}");
                }
            });
        }

        private void StartTeamPoller() {
            if (!isActive || osbase == null || !warmupActive) {
                return;
            }

            teamPollTimer?.Kill();
            teamPollTimer = null;

            teamPollTimer = osbase.AddTimer(TEAM_POLL_INTERVAL, () => {
                teamPollTimer = null;

                if (!isActive || !warmupActive) {
                    return;
                }

                try {
                    CheckTeams();
                } catch (Exception e) {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: Team poll failed: {e.Message}");
                }

                StartTeamPoller();
            });
        }

        private bool CheckTeams() {
            if (!isActive || osbase == null) {
                return false;
            }

            var tResolution = ResolveSideTeam(TEAM_T);
            var ctResolution = ResolveSideTeam(TEAM_CT);

            if (tResolution.IsResolved &&
                ctResolution.IsResolved &&
                string.Equals(tResolution.TeamName, ctResolution.TeamName, StringComparison.OrdinalIgnoreCase)) {
                return HandleSameTeamConflict(tResolution, ctResolution);
            }

            bool appliedAny = false;

            if (tResolution.IsResolved) {
                if (string.Equals(currentCTTeamName, tResolution.TeamName, StringComparison.OrdinalIgnoreCase)) {
                    Console.WriteLine(
                        $"[DEBUG] OSBase[{ModuleName}]: T resolved as {tResolution.TeamName}, clearing stale CT name first."
                    );

                    ResetSideToDefault(TEAM_CT);
                }

                appliedAny |= ApplyResolvedSide(TEAM_T, tResolution);
            } else {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: T side unresolved: {tResolution.DebugText}");
            }

            if (ctResolution.IsResolved) {
                if (string.Equals(currentTTeamName, ctResolution.TeamName, StringComparison.OrdinalIgnoreCase)) {
                    Console.WriteLine(
                        $"[DEBUG] OSBase[{ModuleName}]: CT resolved as {ctResolution.TeamName}, clearing stale T name first."
                    );

                    ResetSideToDefault(TEAM_T);
                }

                appliedAny |= ApplyResolvedSide(TEAM_CT, ctResolution);
            } else {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: CT side unresolved: {ctResolution.DebugText}");
            }

            if (appliedAny) {
                RememberMatchTeamsIfReady();
            }

            return AreBothCurrentTeamsKnown();
        }

        public bool PrepareMatchZyTeamNames() {
            pendingCheckTeamsTimer?.Kill();
            pendingCheckTeamsTimer = null;

            teamDetectRetries = 0;

            return CheckTeams();
        }

        private bool HandleSameTeamConflict(TeamResolution tResolution, TeamResolution ctResolution) {
            Console.WriteLine(
                $"[WARN] OSBase[{ModuleName}]: Same-team conflict: {tResolution.TeamName}. " +
                $"T={tResolution.Matches}/{tResolution.SidePlayerCount}, " +
                $"CT={ctResolution.Matches}/{ctResolution.SidePlayerCount}"
            );

            if (tResolution.Matches > ctResolution.Matches) {
                if (string.Equals(currentCTTeamName, tResolution.TeamName, StringComparison.OrdinalIgnoreCase)) {
                    ResetSideToDefault(TEAM_CT);
                }

                ApplyResolvedSide(TEAM_T, tResolution);
                RememberMatchTeamsIfReady();

                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Conflict winner: T={tResolution.TeamName}");
                return false;
            }

            if (ctResolution.Matches > tResolution.Matches) {
                if (string.Equals(currentTTeamName, ctResolution.TeamName, StringComparison.OrdinalIgnoreCase)) {
                    ResetSideToDefault(TEAM_T);
                }

                ApplyResolvedSide(TEAM_CT, ctResolution);
                RememberMatchTeamsIfReady();

                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Conflict winner: CT={ctResolution.TeamName}");
                return false;
            }

            Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Same-team conflict tied, keeping current names and waiting.");
            return false;
        }

        private TeamResolution ResolveSideTeam(int side) {
            ResetAllTeamMatches();

            int sidePlayerCount = 0;

            foreach (var player in Utilities.GetPlayers()) {
                if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) {
                    continue;
                }

                if (player.TeamNum != side) {
                    continue;
                }

                sidePlayerCount++;

                foreach (var ti in tList) {
                    if (ti.Value.isPlayerInTeam(player.SteamID)) {
                        ti.Value.incMatches();
                    }
                }
            }

            if (sidePlayerCount == 0) {
                return TeamResolution.Unresolved("no players on side");
            }

            var ranked = tList.Values
                .OrderByDescending(t => t.getMatches())
                .ThenByDescending(t => t.playerCount())
                .ThenBy(t => t.getTeamName(), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ranked.Count == 0) {
                return TeamResolution.Unresolved("no configured teams");
            }

            TeamInfo best = ranked[0];
            TeamInfo? second = ranked.Count > 1 ? ranked[1] : null;

            if (best.getMatches() < MIN_SIDE_MATCHES) {
                return TeamResolution.Unresolved(
                    $"best={best.getTeamName()} matches={best.getMatches()} required={MIN_SIDE_MATCHES}"
                );
            }

            if (second != null && second.getMatches() == best.getMatches()) {
                return TeamResolution.Unresolved(
                    $"tie at {best.getMatches()} between {best.getTeamName()} and {second.getTeamName()}"
                );
            }

            return TeamResolution.Resolved(
                best.getTeamName(),
                best.Clone(),
                $"best={best.getTeamName()} matches={best.getMatches()}/{sidePlayerCount}",
                best.getMatches(),
                sidePlayerCount
            );
        }

        private bool ApplyResolvedSide(int side, TeamResolution resolution) {
            if (!resolution.IsResolved) {
                return false;
            }

            bool changed = SetCurrentSideTeam(side, resolution.TeamName, resolution.Team);

            if (changed) {
                Console.WriteLine(
                    $"[DEBUG] OSBase[{ModuleName}]: " +
                    $"{SideName(side)} side resolved as {resolution.TeamName} " +
                    $"({resolution.Matches}/{resolution.SidePlayerCount})"
                );

                resolution.Team.printTeam();
                ApplyScoreboardTeamNameForSide(side, resolution.TeamName);
            }

            return changed;
        }

        private bool SetCurrentSideTeam(int side, string teamName, TeamInfo team) {
            string safeTeamName = SanitizeTeamName(teamName);

            if (string.IsNullOrWhiteSpace(safeTeamName) ||
                string.Equals(safeTeamName, "none", StringComparison.OrdinalIgnoreCase)) {
                Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Refusing to set invalid team name '{teamName}'.");
                return false;
            }

            if (side == TEAM_T) {
                bool changed = !string.Equals(currentTTeamName, safeTeamName, StringComparison.OrdinalIgnoreCase);

                currentTTeamName = safeTeamName;
                currentTTeam = team.Clone();
                currentTTeam.setWins(GetTeamWins(currentTTeamName));

                return changed;
            }

            if (side == TEAM_CT) {
                bool changed = !string.Equals(currentCTTeamName, safeTeamName, StringComparison.OrdinalIgnoreCase);

                currentCTTeamName = safeTeamName;
                currentCTTeam = team.Clone();
                currentCTTeam.setWins(GetTeamWins(currentCTTeamName));

                return changed;
            }

            return false;
        }

        private void ResetSideToDefault(int side) {
            if (side == TEAM_T) {
                currentTTeamName = "Terrorists";
                currentTTeam = new TeamInfo(currentTTeamName);
                currentTTeam.setWins(0);

                ApplyScoreboardTeamNameForSide(TEAM_T, currentTTeamName);
                return;
            }

            if (side == TEAM_CT) {
                currentCTTeamName = "CounterTerrorists";
                currentCTTeam = new TeamInfo(currentCTTeamName);
                currentCTTeam.setWins(0);

                ApplyScoreboardTeamNameForSide(TEAM_CT, currentCTTeamName);
            }
        }

        private void ApplyScoreboardTeamNameForSide(int side, string teamName) {
            if (osbase == null) {
                return;
            }

            string safeTeamName = SanitizeTeamName(teamName);

            if (string.IsNullOrWhiteSpace(safeTeamName)) {
                Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Refusing to apply empty team name.");
                return;
            }

            if (string.Equals(safeTeamName, "none", StringComparison.OrdinalIgnoreCase)) {
                Console.WriteLine($"[WARN] OSBase[{ModuleName}]: Refusing to apply 'none' team name.");
                return;
            }

            if (side == TEAM_CT) {
                osbase.SendCommand($"css_team1 {safeTeamName};");
                return;
            }

            if (side == TEAM_T) {
                osbase.SendCommand($"css_team2 {safeTeamName};");
                return;
            }
        }

        private static string SanitizeTeamName(string teamName) {
            return teamName
                .Trim()
                .Replace(";", "")
                .Replace("\r", "")
                .Replace("\n", "");
        }

        private void ResetAllTeamMatches() {
            foreach (var ti in tList) {
                ti.Value.resetMatches();
            }
        }

        private void RememberMatchTeamsIfReady() {
            if (!AreBothCurrentTeamsKnown()) {
                return;
            }

            matchIsActive();
            RememberMatchTeam(currentTTeamName);
            RememberMatchTeam(currentCTTeamName);

            Console.WriteLine(
                $"[DEBUG] OSBase[{ModuleName}]: Current teams ready: " +
                $"T={currentTTeamName} CT={currentCTTeamName}"
            );
        }

        private void RememberMatchTeam(string teamName) {
            if (string.IsNullOrWhiteSpace(teamName) || IsDefaultTeamName(teamName)) {
                return;
            }

            if (string.Equals(matchTeamOneName, teamName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(matchTeamTwoName, teamName, StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            if (string.IsNullOrEmpty(matchTeamOneName)) {
                matchTeamOneName = teamName;
                return;
            }

            if (string.IsNullOrEmpty(matchTeamTwoName)) {
                matchTeamTwoName = teamName;
            }
        }

        private bool AreBothCurrentTeamsKnown() {
            return
                !IsDefaultTeamName(currentTTeamName) &&
                !IsDefaultTeamName(currentCTTeamName) &&
                !string.Equals(currentTTeamName, currentCTTeamName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDefaultTeamName(string teamName) {
            return
                string.Equals(teamName, "Terrorists", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(teamName, "CounterTerrorists", StringComparison.OrdinalIgnoreCase);
        }

        private static string SideName(int side) {
            if (side == TEAM_T) {
                return "T";
            }

            if (side == TEAM_CT) {
                return "CT";
            }

            return $"team-{side}";
        }

        private void IncrementTeamWin(string teamName) {
            if (string.IsNullOrWhiteSpace(teamName)) {
                return;
            }

            if (!teamWins.ContainsKey(teamName)) {
                teamWins[teamName] = 0;
            }

            teamWins[teamName]++;
        }

        private int GetTeamWins(string teamName) {
            if (string.IsNullOrWhiteSpace(teamName)) {
                return 0;
            }

            return teamWins.TryGetValue(teamName, out int wins) ? wins : 0;
        }

        private void RefreshCurrentTeamWinSnapshots() {
            currentTTeam.setWins(GetTeamWins(currentTTeamName));
            currentCTTeam.setWins(GetTeamWins(currentCTTeamName));
        }

        private void ResetMatchState() {
            pendingCheckTeamsTimer?.Kill();
            pendingCheckTeamsTimer = null;

            teamPollTimer?.Kill();
            teamPollTimer = null;

            teamDetectRetries = 0;
            warmupActive = false;

            currentTTeamName = "Terrorists";
            currentCTTeamName = "CounterTerrorists";

            currentTTeam = new TeamInfo(currentTTeamName);
            currentCTTeam = new TeamInfo(currentCTTeamName);

            matchTeamOneName = string.Empty;
            matchTeamTwoName = string.Empty;

            matchActive = false;
            teamWins.Clear();
        }

        public static Teams getTeams() {
            if (teams == null) {
                throw new InvalidOperationException("Teams module is not loaded.");
            }

            return teams;
        }

        public TeamInfo getT() {
            return currentTTeam;
        }

        public TeamInfo getCT() {
            return currentCTTeam;
        }

        public static bool isMatchActive() {
            return matchActive;
        }

        public static void matchIsActive() {
            matchActive = true;
        }

        public static void matchIsNotActive() {
            matchActive = false;
        }

        private sealed class TeamResolution {
            public bool IsResolved { get; }
            public string TeamName { get; }
            public TeamInfo Team { get; }
            public string DebugText { get; }
            public int Matches { get; }
            public int SidePlayerCount { get; }

            private TeamResolution(
                bool isResolved,
                string teamName,
                TeamInfo team,
                string debugText,
                int matches,
                int sidePlayerCount
            ) {
                IsResolved = isResolved;
                TeamName = teamName;
                Team = team;
                DebugText = debugText;
                Matches = matches;
                SidePlayerCount = sidePlayerCount;
            }

            public static TeamResolution Resolved(
                string teamName,
                TeamInfo team,
                string debugText,
                int matches,
                int sidePlayerCount
            ) {
                return new TeamResolution(true, teamName, team, debugText, matches, sidePlayerCount);
            }

            public static TeamResolution Unresolved(string debugText) {
                return new TeamResolution(false, "none", new TeamInfo("none"), debugText, 0, 0);
            }
        }
    }

    public class TeamInfo {
        private string TeamName { get; set; }
        private HashSet<ulong> pList { get; set; }
        private int matched = 0;
        private int wins = 0;

        public TeamInfo(string teamName) {
            TeamName = teamName;
            pList = new HashSet<ulong>();
        }

        public TeamInfo Clone() {
            var clone = new TeamInfo(TeamName);

            foreach (var p in pList) {
                clone.addPlayer(p);
            }

            clone.matched = matched;
            clone.wins = wins;

            return clone;
        }

        public string getTeamName() {
            return TeamName;
        }

        public void addPlayer(ulong steamid) {
            pList.Add(steamid);
        }

        public void resetMatches() {
            matched = 0;
        }

        public void resetWins() {
            wins = 0;
        }

        public void setWins(int value) {
            wins = value;
        }

        public void incMatches() {
            matched++;
        }

        public void incWins() {
            wins++;
        }

        public int getWins() {
            return wins;
        }

        public int getMatches() {
            return matched;
        }

        public bool isPlayerInTeam(ulong steamid) {
            return pList.Contains(steamid);
        }

        public int playerCount() {
            return pList.Count;
        }

        public void printTeam() {
            Console.WriteLine($"[DEBUG] Team: {TeamName}");

            foreach (var p in pList) {
                Console.WriteLine($"[DEBUG] Player: {p}");
            }
        }
    }
}