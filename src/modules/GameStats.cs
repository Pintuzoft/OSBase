using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core;

namespace OSBase.Modules {

    public class GameStats {
        public string ModuleName => "gamestats";
        private OSBase? osbase;
        private Config? config;
        private bool isWarmup = false;

        // Store player stats keyed by user id.
        private Dictionary<int, PlayerStats> playerStats = new Dictionary<int, PlayerStats>();

        public GameStats(OSBase inOsbase, Config inConfig) {
            this.osbase = inOsbase;
            this.config = inConfig;
            loadEventHandlers();
        }

        public void loadEventHandlers() {
            // Register relevant events.
            osbase?.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
            osbase?.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            osbase?.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
            osbase?.RegisterListener<Listeners.OnMapStart>(OnMapStart);
            osbase?.RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
        }

        private HookResult OnPlayerHurt(EventPlayerHurt eventInfo, GameEventInfo gameEventInfo) {
            if(isWarmup) return HookResult.Continue;
            if (eventInfo.Attacker != null && eventInfo.Attacker.UserId.HasValue) {
                int attackerId = eventInfo.Attacker.UserId.Value;
                if (!playerStats.ContainsKey(attackerId)) {
                    playerStats[attackerId] = new PlayerStats();
                }
                playerStats[attackerId].Damage += eventInfo.DmgHealth;
            }            
            return HookResult.Continue;
        }

        // Update stats when a player dies.
        private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
            if(isWarmup) return HookResult.Continue;
            if (eventInfo.Attacker != null && eventInfo.Attacker.UserId.HasValue) {
                int attackerId = eventInfo.Attacker.UserId.Value;
                if (!playerStats.ContainsKey(attackerId)) {
                    playerStats[attackerId] = new PlayerStats();
                }
                playerStats[attackerId].Kills++;
            }
            if (eventInfo.Userid != null && eventInfo.Userid.UserId.HasValue) {
                int victimId = eventInfo.Userid.UserId.Value;
                if (!playerStats.ContainsKey(victimId)) {
                    playerStats[victimId] = new PlayerStats();
                }
                playerStats[victimId].Deaths++;
            }
            // Optionally update assists if available.
            if (eventInfo.Assister != null && eventInfo.Assister.UserId.HasValue) {
                int assistId = eventInfo.Assister.UserId.Value;
                if (!playerStats.ContainsKey(assistId)) {
                    playerStats[assistId] = new PlayerStats();
                }
                playerStats[assistId].Assists++;
            }
            return HookResult.Continue;
        }

        // Print current stats at the end of a round.
        private HookResult OnRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
            if(isWarmup) return HookResult.Continue;
            Console.WriteLine("[DEBUG] OSBase[gamedata] - Round ended. Current player stats:");
            foreach (var entry in playerStats) {
                Console.WriteLine($"[DEBUG] OSBase[gamedata] - Player ID {entry.Key}: {entry.Value}");
            }
            return HookResult.Continue;
        }

        // Reset stats at the start of a new map.
         private void OnMapStart(string mapName) {
            this.isWarmup = true;
            playerStats.Clear();
            foreach (var player in Utilities.GetPlayers()) {
                if (player != null && player.UserId.HasValue) {
                    playerStats[player.UserId.Value] = new PlayerStats();
                }
            }            
        }

        private HookResult OnWarmupEnd(EventWarmupEnd eventInfo, GameEventInfo gameEventInfo) {
            isWarmup = false;
            return HookResult.Continue;
        }

        // Public method to access a player's stats.
        public PlayerStats GetPlayerStats(int userId) {
            if (playerStats.ContainsKey(userId)) {
                return playerStats[userId];
            }
            return new PlayerStats(); // Return an empty stats object if none exists.
        }

    }

    // Data container for game statistics.
    public class PlayerStats {
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Assists { get; set; }
        public int Damage { get; set; }
        public override string ToString() {
            return $"Kills: {Kills}, Deaths: {Deaths}, Assists: {Assists}, Damage: {Damage}";
        }
    }
}