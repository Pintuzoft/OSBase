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

        private CounterStrikeSharp.API.Modules.Timers.Timer? pendingCheckTeamsTimer;

        private readonly Dictionary<string, TeamInfo> tList = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> teamWins = new(StringComparer.OrdinalIgnoreCase);

        private const int TEAM_T = (int)CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist;
        private const int TEAM_CT = (int)CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist;

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

            if (osbase != null && handlersLoaded) {
                osbase.DeregisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
                osbase.DeregisterEventHandler<EventRoundEnd>(OnRoundEnd);
                osbase.DeregisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);
                osbase.DeregisterEventHandler<EventStartHalftime>(OnStartHalftime);

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

            osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);
            osbase.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

            handlersLoaded = true;
        }

        private void OnMapStart(string mapName) {
            ResetMatchState();
            ApplyScoreboardTeamNames(currentTTeamName, currentCTTeamName);
            ScheduleCheckTeams(1.0f);

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] map start: {mapName}, state reset.");
        }

        private void OnMapEnd() {
            pendingCheckTeamsTimer?.Kill();
            pendingCheckTeamsTimer = null;
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

                foreach (var steamid in steamids) {
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

            ScheduleCheckTeams(1.0f);
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] halftime triggered, scheduling fresh team detect.");

            return HookResult.Continue;
        }

        private HookResult OnMatchEnd(EventCsWinPanelMatch eventInfo, GameEventInfo gameEventInfo) {
            if (!isActive || db == null) {
                return HookResult.Continue;
            }

            string teamOne = matchTeamOneName;
            string teamTwo = matchTeamTwoName;

            if (string.IsNullOrWhiteSpace(teamOne) || string.IsNullOrWhiteSpace(teamTwo)) {
                if (!string.Equals(currentTTeamName, currentCTTeamName, StringComparison.OrdinalIgnoreCase) &&
                    currentTTeamName != "Terrorists" &&
                    currentCTTeamName != "CounterTerrorists") {
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

            return HookResult.Continue;
        }

        private HookResult OnPlayerTeam(EventPlayerTeam eventInfo, GameEventInfo gameEventInfo) {
            if (!isActive) {
                return HookResult.Continue;
            }

            ScheduleCheckTeams(0.5f);
            return HookResult.Continue;
        }

        private void ScheduleCheckTeams(float delay) {
            if (!isActive || osbase == null) {
                return;
            }

            pendingCheckTeamsTimer?.Kill();
            pendingCheckTeamsTimer = osbase.AddTimer(delay, () => {
                try {
                    if (!isActive) {
                        return;
                    }

                    CheckTeams();
                } finally {
                    pendingCheckTeamsTimer = null;
                }
            });
        }

        private void CheckTeams() {
            if (!isActive || osbase == null) {
                return;
            }

            var tResolution = ResolveSideTeam(TEAM_T, null);
            var ctResolution = ResolveSideTeam(TEAM_CT, tResolution.IsResolved ? tResolution.TeamName : null);

            if (tResolution.IsResolved && ctResolution.IsResolved &&
                !string.Equals(tResolution.TeamName, ctResolution.TeamName, StringComparison.OrdinalIgnoreCase)) {
                currentTTeamName = tResolution.TeamName;
                currentCTTeamName = ctResolution.TeamName;

                currentTTeam = tResolution.Team.Clone();
                currentCTTeam = ctResolution.Team.Clone();

                currentTTeam.setWins(GetTeamWins(currentTTeamName));
                currentCTTeam.setWins(GetTeamWins(currentCTTeamName));

                matchIsActive();
                RememberMatchTeams(currentTTeamName, currentCTTeamName);

                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Teams resolved: T={currentTTeamName} CT={currentCTTeamName}");

                currentTTeam.printTeam();
                currentCTTeam.printTeam();

                ApplyScoreboardTeamNames(currentTTeamName, currentCTTeamName);
                return;
            }

            Console.WriteLine(
                $"[WARN] OSBase[{ModuleName}]: Team detect unresolved. " +
                $"T={tResolution.DebugText} CT={ctResolution.DebugText}"
            );

            if (!isMatchActive()) {
                currentTTeamName = "Terrorists";
                currentCTTeamName = "CounterTerrorists";
                currentTTeam = new TeamInfo(currentTTeamName);
                currentCTTeam = new TeamInfo(currentCTTeamName);
            }
        }

        private TeamResolution ResolveSideTeam(int side, string? excludedTeamName) {
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
                    if (!string.IsNullOrEmpty(excludedTeamName) &&
                        string.Equals(ti.Key, excludedTeamName, StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }

                    if (ti.Value.isPlayerInTeam(player.SteamID)) {
                        ti.Value.incMatches();
                    }
                }
            }

            if (sidePlayerCount == 0) {
                return TeamResolution.Unresolved("no players on side");
            }

            int minimumRequiredMatches = Math.Max(2, (int)Math.Ceiling(sidePlayerCount / 2.0));

            var ranked = tList.Values
                .Where(t => string.IsNullOrEmpty(excludedTeamName) || !string.Equals(t.getTeamName(), excludedTeamName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.getMatches())
                .ThenByDescending(t => t.playerCount())
                .ThenBy(t => t.getTeamName(), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ranked.Count == 0) {
                return TeamResolution.Unresolved("no configured teams");
            }

            TeamInfo best = ranked[0];
            TeamInfo? second = ranked.Count > 1 ? ranked[1] : null;

            if (best.getMatches() < minimumRequiredMatches) {
                return TeamResolution.Unresolved($"best={best.getTeamName()} matches={best.getMatches()} required={minimumRequiredMatches}");
            }

            if (second != null && second.getMatches() == best.getMatches()) {
                return TeamResolution.Unresolved($"tie at {best.getMatches()} between {best.getTeamName()} and {second.getTeamName()}");
            }

            return TeamResolution.Resolved(best.getTeamName(), best.Clone(), $"best={best.getTeamName()} matches={best.getMatches()}/{sidePlayerCount}");
        }

        private void ResetAllTeamMatches() {
            foreach (var ti in tList) {
                ti.Value.resetMatches();
            }
        }

        private void ApplyScoreboardTeamNames(string tName, string ctName) {
            if (osbase == null) {
                return;
            }

            // Preserving your original mapping:
            // css_team1 <- CT
            // css_team2 <- T
            osbase.SendCommand($"css_team1 {ctName};");
            osbase.SendCommand($"css_team2 {tName};");
        }

        private void RememberMatchTeams(string tName, string ctName) {
            RememberMatchTeam(tName);
            RememberMatchTeam(ctName);
        }

        private void RememberMatchTeam(string teamName) {
            if (string.IsNullOrWhiteSpace(teamName)) {
                return;
            }

            if (teamName == "Terrorists" || teamName == "CounterTerrorists") {
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

            private TeamResolution(bool isResolved, string teamName, TeamInfo team, string debugText) {
                IsResolved = isResolved;
                TeamName = teamName;
                Team = team;
                DebugText = debugText;
            }

            public static TeamResolution Resolved(string teamName, TeamInfo team, string debugText) {
                return new TeamResolution(true, teamName, team, debugText);
            }

            public static TeamResolution Unresolved(string debugText) {
                return new TeamResolution(false, "none", new TeamInfo("none"), debugText);
            }
        }
    }

    public class TeamInfo {
        private string TeamName { get; set; }
        private List<ulong> pList { get; set; }
        private int matched = 0;
        private int wins = 0;

        public TeamInfo(string teamName) {
            TeamName = teamName;
            pList = new List<ulong>();
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