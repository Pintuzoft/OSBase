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
            } 
            if (config == null) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] config is null. {ModuleName} failed to load.");
                return;
            }

            var globalConfig = config.GetGlobalConfigValue($"{ModuleName}", "0");
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Global config value: {globalConfig}");
            if (globalConfig == "1") {
                loadEventHandlers();
                LoadMapInfo();
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
            } else {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {ModuleName} is disabled in the global configuration.");
            }
        }

        private void loadEventHandlers() {
            if(osbase == null) return;
            try {
                osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
                osbase.RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);  // New warmup end handler
                osbase.RegisterListener<Listeners.OnMapStart>(OnMapStart);
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Event handlers registered successfully.");
            } catch(Exception ex) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Failed to register event handlers: {ex.Message}");
            }
        }

        private void LoadMapInfo() {
            config?.CreateCustomConfig($"{mapConfigFile}", "// Map info\nde_dust2 2\n");
            List<string> maps = config?.FetchCustomConfig($"{mapConfigFile}") ?? new List<string>();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Loaded {maps.Count} line(s) from {mapConfigFile}.");

            foreach (var line in maps) {
                string trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//"))
                    continue;

                var parts = trimmedLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[1], out int bs)) {
                    mapBombsites[parts[0]] = bs;
                    Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Loaded map info: {parts[0]} = {bs}");
                } else {
                    Console.WriteLine($"[ERROR] OSBase[{ModuleName}]: Failed to parse bombsites for map {parts[0]}");
                }
            }
        }

        private void OnMapStart(string mapName) {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] - OnMapStart triggered for map: {mapName}");
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
            Console.WriteLine("[DEBUG] OSBase[teambalancer] - OnRoundEnd triggered.");
            BalanceTeams();
            return HookResult.Continue;
        }

        private HookResult OnWarmupEnd(EventWarmupEnd eventInfo, GameEventInfo gameEventInfo) {
            Console.WriteLine("[DEBUG] OSBase[teambalancer] - OnWarmupEnd triggered.");
            BalanceTeams();
            return HookResult.Continue;
        }

        private void BalanceTeams() {
            var playersList = Utilities.GetPlayers();
            var connectedPlayers = playersList
                .Where(player => player.Connected == PlayerConnectedState.PlayerConnected && player.UserId.HasValue)
                .ToList();

            int totalPlayers = connectedPlayers.Count;
            Console.WriteLine($"[DEBUG] OSBase[teambalancer] - Connected players count: {totalPlayers}");
            if (totalPlayers == 0) {
                Console.WriteLine("[DEBUG] OSBase[teambalancer] - No connected players found.");
                return;
            }

            int tCount = connectedPlayers.Count(p => p.TeamNum == TEAM_T);
            int ctCount = connectedPlayers.Count(p => p.TeamNum == TEAM_CT);

            int idealCT, idealT;
            if (bombsites >= 2) {
                // Maps with 2+ bombsites: CT gets the extra player
                idealCT = totalPlayers / 2 + totalPlayers % 2;
                idealT  = totalPlayers / 2;
            } else {
                // Maps with 0-1 bombsites: T gets the extra player
                idealT  = totalPlayers / 2 + totalPlayers % 2;
                idealCT = totalPlayers / 2;
            }
            Console.WriteLine($"[DEBUG] OSBase[teambalancer] - Total: {totalPlayers}, T: {tCount} (ideal: {idealT}), CT: {ctCount} (ideal: {idealCT})");

            int playersToMove = 0;
            bool moveFromT = false;
            if (tCount > idealT) {
                playersToMove = tCount - idealT;
                moveFromT = true;
                Console.WriteLine($"[DEBUG] OSBase[teambalancer] - Bombsites = {bombsites}: Moving {playersToMove} player(s) from Terrorists to CT.");
            } else if (ctCount > idealCT) {
                playersToMove = ctCount - idealCT;
                Console.WriteLine($"[DEBUG] OSBase[teambalancer] - Bombsites = {bombsites}: Moving {playersToMove} player(s) from CT to Terrorists.");
            } else {
                Console.WriteLine($"[DEBUG] OSBase[teambalancer] - Teams are balanced. No moves required.");
                return;
            }

            var playersToSwitch = connectedPlayers
                .Where(p => moveFromT ? p.TeamNum == TEAM_T : p.TeamNum == TEAM_CT)
                .Select(p => new { Id = p.UserId!.Value, Score = p.Score, Name = p.PlayerName })
                .OrderBy(p => p.Score)
                .Take(playersToMove)
                .ToList();

            Console.WriteLine($"[DEBUG] OSBase[teambalancer] - Selected {playersToSwitch.Count} candidate(s) for team switching.");

            foreach (var candidate in playersToSwitch) {
                var player = Utilities.GetPlayerFromUserid(candidate.Id);
                if (player != null) {
                    Console.WriteLine($"[DEBUG] OSBase[teambalancer] - Switching player '{candidate.Name}' (ID: {candidate.Id}) from {(moveFromT ? "Terrorists" : "CT")} to {(moveFromT ? "CT" : "Terrorists")}.");
                    player.SwitchTeamsOnNextRoundReset = true;
                } else {
                    Console.WriteLine($"[ERROR] OSBase[teambalancer] - Could not find player with ID {candidate.Id}.");
                }
            }
        }
    }
}