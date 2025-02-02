using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules {

    public class TeamBalancer : IModule {
        public string ModuleName => "teambalancer";   
        private OSBase? osbase;
        private Config? config;
        private const int TEAM_T = (int)CsTeam.Terrorist;
        private const int TEAM_CT = (int)CsTeam.CounterTerrorist;
        private Dictionary<string, int> mapBombsites = new Dictionary<string, int>();
        private string mapConfigFile = "teambalancer_mapinfo.cfg";
        private int bombsites = 2;

        public void Load(OSBase inOsbase, Config inConfig) {
            osbase = inOsbase;
            config = inConfig;
            config.RegisterGlobalConfigValue($"{ModuleName}", "1");

            if (osbase == null) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] osbase is null. {ModuleName} failed to load.");
                return;
            } else if (config == null) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] config is null. {ModuleName} failed to load.");
                return;
            }

            if (config.GetGlobalConfigValue($"{ModuleName}", "0") == "1") {
                loadEventHandlers();
                LoadMapInfo();
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
            } else {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {ModuleName} is disabled in the global configuration.");
            }
        }

        private void loadEventHandlers() {
            if(osbase == null) return;
            osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
            osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        }

        private void LoadMapInfo() {
            config?.CreateCustomConfig($"{mapConfigFile}", "// Map info\nde_dust2 2\n");
            List<string> maps = config?.FetchCustomConfig($"{mapConfigFile}") ?? new List<string>();

            foreach (var line in maps) {
                string trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//"))
                    continue;

                var parts = trimmedLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2) {
                    mapBombsites[parts[0]] = int.Parse(parts[1]);
                    Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Loaded map info: {parts[0]} = {parts[1]}");
                }
            }
        }

        private void OnMapStart(string mapName) {
            if (mapBombsites.ContainsKey(mapName)) {
                bombsites = mapBombsites[mapName];
                Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Map {mapName} started. Bombsites: {bombsites}");
            } else {
                bombsites = mapName.Contains("cs_") ? 1 : 2;
                config?.AddCustomConfigLine($"{mapConfigFile}", $"{mapName} {bombsites}");
                Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Map {mapName} started. Default bombsites: {bombsites}");
            }
        }

        private HookResult OnRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
            var playersList = Utilities.GetPlayers();
            // Filter connected players with a valid UserID
            var connectedPlayers = playersList
                .Where(player => player.Connected == PlayerConnectedState.PlayerConnected && player.UserId.HasValue)
                .ToList();

            int totalPlayers = connectedPlayers.Count;
            if (totalPlayers == 0)
                return HookResult.Continue;

            int tCount = connectedPlayers.Count(p => p.TeamNum == TEAM_T);
            int ctCount = connectedPlayers.Count(p => p.TeamNum == TEAM_CT);

            // Calculate ideal team sizes based on bombsite configuration:
            // - For maps with 2+ bombsites, CT gets the extra player on odd counts.
            // - For maps with 0-1 bombsites, T gets the extra player on odd counts.
            int idealCT, idealT;
            if (bombsites >= 2) {
                idealCT = totalPlayers / 2 + totalPlayers % 2;
                idealT  = totalPlayers / 2;
            } else {
                idealT  = totalPlayers / 2 + totalPlayers % 2;
                idealCT = totalPlayers / 2;
            }
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Total: {totalPlayers}, T: {tCount} (ideal: {idealT}), CT: {ctCount} (ideal: {idealCT})");

            // Determine which team is oversized and how many players need switching.
            int playersToMove = 0;
            bool moveFromT = false;
            if (tCount > idealT) {
                playersToMove = tCount - idealT;
                moveFromT = true;
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Bombsites = {bombsites}: Moving {playersToMove} player(s) from Terrorists to CT.");
            } else if (ctCount > idealCT) {
                playersToMove = ctCount - idealCT;
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Bombsites = {bombsites}: Moving {playersToMove} player(s) from CT to Terrorists.");
            } else {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Teams are balanced as per bombsite config. No moves required.");
                return HookResult.Continue;
            }

            // Select candidates from the overstaffed team (lowest score first)
            var playersToSwitch = connectedPlayers
                .Where(p => moveFromT ? p.TeamNum == TEAM_T : p.TeamNum == TEAM_CT)
                .Select(p => new { Id = p.UserId!.Value, Score = p.Score, Name = p.PlayerName })
                .OrderBy(p => p.Score)
                .Take(playersToMove)
                .ToList();

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Selected {playersToSwitch.Count} candidate(s) for team switching.");

            foreach (var candidate in playersToSwitch) {
                var player = Utilities.GetPlayerFromUserid(candidate.Id);
                if (player != null) {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - Switching player '{candidate.Name}' (ID: {candidate.Id}) from {(moveFromT ? "Terrorists" : "CT")} to {(moveFromT ? "CT" : "Terrorists")}.");
                    player.SwitchTeamsOnNextRoundReset = true;
                } else {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}] - Could not find player with ID {candidate.Id}.");
                }
            }
            return HookResult.Continue;
        }
    }
}