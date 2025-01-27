using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;

namespace OSBase.Modules;

// Defines the Damage Report Module
public class DamageReportModule : IModule {
    // Module name property
    public string ModuleName => "DamageReportModule";
    private OSBase? osbase; // Reference to the main OSBase instance

    // 3D arrays to track hitbox-specific data

    private Dictionary<int, HashSet<int>> killedPlayer = new();
    private Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitboxGivenDamage = new();
    private Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitboxTakenDamage = new();
    private Dictionary<int, Dictionary<int, int>> damageGiven = new Dictionary<int, Dictionary<int, int>>();
    private Dictionary<int, Dictionary<int, int>> damageTaken = new Dictionary<int, Dictionary<int, int>>();
    private Dictionary<int, Dictionary<int, int>> hitsGiven = new Dictionary<int, Dictionary<int, int>>();
    private Dictionary<int, Dictionary<int, int>> hitsTaken = new Dictionary<int, Dictionary<int, int>>();
    private Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitboxGiven = new Dictionary<int, Dictionary<int, Dictionary<int, int>>>();
    private Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitboxTaken = new Dictionary<int, Dictionary<int, Dictionary<int, int>>>();

    private Dictionary<int, string> playerNames = new Dictionary<int, string>();

    private HashSet<int> reportedPlayers = new HashSet<int>();

    // Constant to represent environmental kills
    private const int ENVIRONMENT = -1;

    // Names of hit groups for easier identification in reports
    private readonly string[] hitboxName = {
        "Body", "Head", "Chest", "Stomach", "L-Arm", "R-Arm", "L-Leg", "R-Leg", "Neck", "Unknown", "Gear"
    };

    float delay = 0.0f; // Delay in seconds before sending damage reports

    // Module initialization method
    public void Load(OSBase inOsbase, ConfigModule inConfig) {
        osbase = inOsbase; // Set the OSBase reference

        // Register event handlers for various game events
        osbase.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        osbase.RegisterEventHandler<EventPlayerConnect>(OnPlayerConnect);
        osbase.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnectEvent);

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    // Event handler for player hurt event
private HookResult OnPlayerHurt(EventPlayerHurt eventInfo, GameEventInfo gameEventInfo) {

    try {
        // Validate attacker and victim
        if (eventInfo.Attacker == null || eventInfo.Userid == null) {
            Console.WriteLine("[ERROR] Attacker or victim is null in OnPlayerHurt.");
            return HookResult.Continue;
        }

        if (eventInfo.Attacker?.UserId == null && eventInfo.Userid?.UserId == null) {
            Console.WriteLine("[ERROR] Both Attacker and Victim UserId are null.");
            return HookResult.Continue;
        }

        // Default attacker and victim IDs
        int attacker = eventInfo.Attacker?.UserId ?? ENVIRONMENT; // Default to ENVIRONMENT if null
        int victim = eventInfo.Userid?.UserId ?? -1;

        if (victim == -1) {
            Console.WriteLine("[ERROR] Victim has invalid UserId in OnPlayerHurt.");
            return HookResult.Continue;
        }

        int damage = eventInfo.DmgHealth;
        int hitgroup = eventInfo.Hitgroup;

        // Assign environmental damage
        if (attacker == victim && eventInfo.Weapon == "world") {
            Console.WriteLine("[OnPlayerHurt] Environmental damage detected.");
            attacker = ENVIRONMENT;
        }

        // Initialize dictionaries for attacker and victim
        if (!damageGiven.ContainsKey(attacker)) damageGiven[attacker] = new Dictionary<int, int>();
        if (!damageTaken.ContainsKey(victim)) damageTaken[victim] = new Dictionary<int, int>();
        if (!hitsGiven.ContainsKey(attacker)) hitsGiven[attacker] = new Dictionary<int, int>();
        if (!hitsTaken.ContainsKey(victim)) hitsTaken[victim] = new Dictionary<int, int>();
        if (!hitboxGiven.ContainsKey(attacker)) hitboxGiven[attacker] = new Dictionary<int, Dictionary<int, int>>();
        if (!hitboxTaken.ContainsKey(victim)) hitboxTaken[victim] = new Dictionary<int, Dictionary<int, int>>();
        if (!hitboxGiven[attacker].ContainsKey(victim)) hitboxGiven[attacker][victim] = new Dictionary<int, int>();
        if (!hitboxTaken[victim].ContainsKey(attacker)) hitboxTaken[victim][attacker] = new Dictionary<int, int>();

        // Track damage
        damageGiven[attacker][victim] = damageGiven[attacker].GetValueOrDefault(victim, 0) + damage;
        damageTaken[victim][attacker] = damageTaken[victim].GetValueOrDefault(attacker, 0) + damage;

        // Track hits
        hitsGiven[attacker][victim] = hitsGiven[attacker].GetValueOrDefault(victim, 0) + 1;
        hitsTaken[victim][attacker] = hitsTaken[victim].GetValueOrDefault(attacker, 0) + 1;

        // Track hitbox-specific data
        hitboxGiven[attacker][victim][hitgroup] = hitboxGiven[attacker][victim].GetValueOrDefault(hitgroup, 0) + 1;
        hitboxTaken[victim][attacker][hitgroup] = hitboxTaken[victim][attacker].GetValueOrDefault(hitgroup, 0) + 1;

        return HookResult.Continue;
    } catch (Exception ex) {
        Console.WriteLine($"[ERROR] Exception in OnPlayerHurt: {ex.Message}\n{ex.StackTrace}");
        return HookResult.Continue;
    }
}
    // Event handler for player death event
    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {

        int victimId = eventInfo.Userid?.UserId ?? -1;
        int attackerId = eventInfo.Attacker?.UserId ?? -1;

        if (attackerId >= 0 && victimId >= 0) {
            if (!killedPlayer.ContainsKey(attackerId)) {
                killedPlayer[attackerId] = new HashSet<int>();
            }
            killedPlayer[attackerId].Add(victimId);
        }

        // Schedule damage report
        if (eventInfo.Userid != null) {
//            osbase?.AddTimer(delay, () => {
                DisplayDamageReport(eventInfo.Userid);
//            });
        };
        return HookResult.Continue;
    }
    // Event handler for round start
    private HookResult OnRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {

        ClearDamageData(); // Reset all damage data
        UpdatePlayerNames(); // Refresh player names
        return HookResult.Continue;
    }

    // Event handler for round end
    private HookResult OnRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {

        // Add a delay to allow all post-round damage to be recorded
//        osbase?.AddTimer(delay, () => {
            var playersList = Utilities.GetPlayers();
            foreach (var player in playersList) {
                if (player.IsValid &&
                    !player.IsHLTV &&
                    player.UserId.HasValue ) {
                    DisplayDamageReport(player);
                } 
            }
//        });

        return HookResult.Continue;
    }


    private HookResult OnPlayerDisconnectEvent(EventPlayerDisconnect eventInfo, GameEventInfo gameEventInfo) {

        if (eventInfo.Userid != null) {
            if (eventInfo.Userid?.UserId != null) {
                OnPlayerDisconnect(eventInfo.Userid.UserId.Value);
            }
        }
        return HookResult.Continue;
    }
    // Event handler for player connect
    private HookResult OnPlayerConnect(EventPlayerConnect eventInfo, GameEventInfo gameEventInfo) {
        UpdatePlayerNames(); // Refresh player names upon connection
        return HookResult.Continue;
    }

    // Update player names by iterating through active players
    private void UpdatePlayerNames() {

        try {
            var playersList = Utilities.GetPlayers();

            if (playersList == null) {
                Console.WriteLine("[ERROR] Players list is null in UpdatePlayerNames.");
                return;
            }

            foreach (var player in playersList) {
                if (player == null) {
                    Console.WriteLine("[DEBUG] Found null player in players list.");
                    continue;
                }

                if (player.UserId.HasValue) {
                    int playerId = player.UserId.Value;

                    // Add or update the player's name in the dictionary
                    playerNames[playerId] = string.IsNullOrEmpty(player.PlayerName) ? "Bot" : player.PlayerName;

                    Console.WriteLine($"[DEBUG] Updated player name: ID={playerId}, Name={playerNames[playerId]}");
                } else {
                    Console.WriteLine("[DEBUG] Player does not have a UserId.");
                }
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] Exception in UpdatePlayerNames: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // Display damage report for a specific player
    private void DisplayDamageReport(CCSPlayerController player) {
        List<string> report = new List<string>();
        if (player == null || player.UserId == null) return;
        int playerId = player.UserId.Value;

        if (reportedPlayers.Contains(playerId)) return;
        reportedPlayers.Add(playerId);

        bool hasVictimData = damageGiven.ContainsKey(playerId) && damageGiven[playerId].Count > 0;
        bool hasAttackerData = damageTaken.ContainsKey(playerId) && damageTaken[playerId].Count > 0;

        if (hasVictimData || hasAttackerData) {
            report.Add($"===[ Damage Report for {playerNames.GetValueOrDefault(playerId, "Unknown")} ]===");
//            Console.WriteLine($"===[ Damage Report for {playerNames.GetValueOrDefault(playerId, "Unknown")} ]===");
//            player.PrintToChat("===[ Damage Report (hits:damage) ]===");
        }

        if (hasVictimData) {
            report.Add($"Victims:");
            Console.WriteLine($"Victims:");
//            player.PrintToChat($"Victims:");
            foreach (var victim in damageGiven[playerId]) {
                string victimName = playerNames.GetValueOrDefault(victim.Key, "Unknown");
                int hits = hitsGiven[playerId].GetValueOrDefault(victim.Key, 0);
                int damage = victim.Value;
                report.Add($" - {victimName}: {hits} hits, {damage} damage");
//                Console.WriteLine($" - {victimName}: {hits} hits, {damage} damage");
//                player.PrintToChat($" - {victimName}: {hits} hits, {damage} damage");
            }
        }

        if (hasAttackerData) {
            report.Add($"Attackers:");
            Console.WriteLine($"Attackers:");
//            player.PrintToChat($"Attackers:");
            foreach (var attacker in damageTaken[playerId]) {
                string attackerName = playerNames.GetValueOrDefault(attacker.Key, "Unknown");
                int hits = hitsTaken[playerId].GetValueOrDefault(attacker.Key, 0);
                int damage = attacker.Value;
                report.Add($" - {attackerName}: {hits} hits, {damage} damage");
//                Console.WriteLine($" - {attackerName}: {hits} hits, {damage} damage");
//                player.PrintToChat($" - {attackerName}: {hits} hits, {damage} damage");
            }
        }
        if ( report.Count > 0 ) {
            osbase?.AddTimer(delay, () => {
                foreach (var line in report) {
                    Console.WriteLine(line);
                    //player.PrintToChat(line);
                }
            });
        }
    }


    // Helper method to clear all damage-related data
    private void ClearDamageData() {
        damageGiven.Clear();
        damageTaken.Clear();
        hitsGiven.Clear();
        hitsTaken.Clear();
        hitboxGiven.Clear();
        hitboxTaken.Clear();
        killedPlayer.Clear();
        reportedPlayers.Clear();
    }
    private void OnPlayerDisconnect(int playerId) {
        damageGiven.Remove(playerId);
        damageTaken.Remove(playerId);
        hitsGiven.Remove(playerId);
        hitsTaken.Remove(playerId);
        hitboxGiven.Remove(playerId);
        hitboxTaken.Remove(playerId);
        killedPlayer.Remove(playerId);
        playerNames.Remove(playerId);
        reportedPlayers.Remove(playerId);
    }
}
