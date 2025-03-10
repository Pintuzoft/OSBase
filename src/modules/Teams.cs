using System;
using System.Net.NetworkInformation;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;


namespace OSBase.Modules {
    public class Teams : IModule {
        public string ModuleName => "teams";
        private static Teams? teams = null;
        private static bool matchActive = false;
        private OSBase? osbase;
        private Config? config;
        private Dictionary<string, TeamInfo> tList = new Dictionary<string, TeamInfo>();
        private const int TEAM_T = (int)CsTeam.Terrorist;
        private const int TEAM_CT = (int)CsTeam.CounterTerrorist;
        private TeamInfo tTeam = new TeamInfo("Terrorists");
        private TeamInfo ctTeam = new TeamInfo("CounterTerrorists");

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

            if (config?.GetGlobalConfigValue($"{ModuleName}", "0") == "1") {
                createCustomConfigs();
                LoadConfig();
                loadEventHandlers();
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
            } else {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {ModuleName} is disabled in the global configuration.");
            }
            teams = this;
        }

        private void createCustomConfigs() {
            if (config == null) 
                return;
            config.CreateCustomConfig($"{ModuleName}.cfg", "// Teams Configuration\n// Ex:\n// teamname1 steamid1:steamid2:steamid3:steamid4:steamid5:steamid6\n// teamname2 steamid7:steamid8:steamid9:steamid10:steamid11:steamid12\n");
        }

        // load the config file load the team names dynamically and the steamids as the players in the team

        private void LoadConfig() {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Loading config...");
            List<string> teamcfg = config?.FetchCustomConfig($"{ModuleName}.cfg") ?? new List<string>();

            foreach (var line in teamcfg) {
                string trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//"))
                    continue;
                
                var parts = trimmedLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                TeamInfo team = new TeamInfo(parts[0]);
                var steamids = parts[1].Split(':', StringSplitOptions.RemoveEmptyEntries);
                foreach (var steamid in steamids) {
                    try {
                        team.addPlayer(ulong.Parse(steamid));
                        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Added steamid {steamid} to team {parts[0]}");
                    } catch (Exception e) {
                        Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: Failed to parse steamid {steamid} for team {parts[0]} -> {e.Message}");
                    }
                }
                tList.Add(parts[0], team);
            }

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Loaded {tList.Count} teams.");
        }

        private void loadEventHandlers() {
            if(osbase == null) return;
            osbase?.RegisterEventHandler<EventPlayerTeam>(onPlayerTeam);
        }

        private HookResult onPlayerTeam (EventPlayerTeam eventInfo, GameEventInfo gameEventInfo) {
            osbase?.AddTimer(0.5f, () => {
                checkTeams();
            });
            return HookResult.Continue;
        }

        private void checkTeams ( ) {
            if (osbase == null) 
                return;

            // T
            foreach (var ti in tList) {
                ti.Value.resetMatches();
            }
            foreach ( var player in Utilities.GetPlayers()) {
                if (player.TeamNum == TEAM_T) {
                    foreach (var ti in tList) {
                        if ( ti.Value.isPlayerInTeam(player.SteamID)) {
                            ti.Value.incMatches();
                        }
                    }
                }
            }
            tTeam = findTeamWithMostMatches();

            // CT
            foreach (var ti in tList) {
                ti.Value.resetMatches();
            }
            foreach ( var player in Utilities.GetPlayers()) {
                if (player.TeamNum == TEAM_CT) {
                    foreach (var ti in tList) {
                        if ( ti.Value.isPlayerInTeam(player.SteamID)) {
                            ti.Value.incMatches();
                        }
                    }
                }
            }
            ctTeam = findTeamWithMostMatches();

            // Print teams
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Teams:");
            tTeam.printTeam();
            ctTeam.printTeam();

        }

        private TeamInfo findTeamWithMostMatches () {
            TeamInfo team = new TeamInfo("none");
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Finding team with most matches");
            foreach (var ti in tList) {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Team: {ti.Key} Matches: {ti.Value.getMatches()}");
                if ( ti.Value.getMatches() > team.getMatches() ) {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Found team with more matches: {ti.Key}");
                    team = ti.Value;
                }
            }
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}]: Team with most matches is {team.getTeamName()}");
            return team;
        }

        public static Teams getTeams ( ) {
            if (teams == null) {
                throw new InvalidOperationException("Teams module is not loaded.");
            }
            return teams;
        }

        public TeamInfo getT ( ) {
            return tTeam;
        }

        public TeamInfo getCT ( ) {
            return ctTeam;
        }

        public static bool isMatchActive ( ) {
            return matchActive;
        }
        public static void matchIsActive ( ) {
            matchActive = true;
        }
        public static void matchIsNotActive ( ) {
            matchActive = false;
        }

    }
    public class TeamInfo {
        private string TeamName { get; set; }
        private List<ulong> pList { get; set; }
        private int matched = 0;
        public TeamInfo(string teamName) {
            TeamName = teamName;
            pList = new List<ulong>();
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

        public void incMatches() {
            matched++;
        }

        public int getMatches() {
            return matched;
        }

        public bool isPlayerInTeam(ulong steamid) {
            return pList.Contains(steamid);
        }

        public int playerCount ( ) {
            return pList.Count;
        }
        public void printTeam ( ) {
            Console.WriteLine($"[DEBUG] Team: {TeamName}");
            foreach (var p in pList) {
                Console.WriteLine($"[DEBUG] Player: {p}");
            }
        }
    }
}