using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;

namespace OSBase.Modules;

// Defines the Damage Report Module
public class DamageReport : IModule {
    // Module name property
    public string ModuleName => "damagereport";
    private OSBase? osbase; // Reference to the main OSBase instance
    private Config? config; // Reference to the ConfigModule instance

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

    float delay = 3.0f; // Delay in seconds before sending damage reports

    // Module initialization method
    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase; // Set the OSBase reference
        config = inConfig; // Set the ConfigModule reference

        // Register required global config values
        config.RegisterGlobalConfigValue($"{ModuleName}", "1");

        if (osbase == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] osbase is null. {ModuleName} failed to load.");
            return;
        } else if (config == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] config is null. {ModuleName} failed to load.");
            return;
        }

        if (config?.GetGlobalConfigValue($"{ModuleName}", "0") == "1") {
            loadEventHandlers();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
        } else {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {ModuleName} is disabled in the global configuration.");
        }
    }

    private void loadEventHandlers ( ) {
        if (osbase == null) return;
        osbase.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        osbase.RegisterEventHandler<EventPlayerConnect>(OnPlayerConnect);
        osbase.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnectEvent);
    }

    // Event handler for player hurt event
    private HookResult OnPlayerHurt(EventPlayerHurt eventInfo, GameEventInfo gameEventInfo) {
        try {
            if (eventInfo.Attacker == null || eventInfo.Userid == null) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Attacker or victim is null in OnPlayerHurt.");
                return HookResult.Continue;
            }

            if (eventInfo.Attacker?.UserId == null && eventInfo.Userid?.UserId == null) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Both Attacker and Victim UserId are null.");
                return HookResult.Continue;
            }

            if (eventInfo.DmgHealth <= 0) {
                return HookResult.Continue;
            }

            int attacker = eventInfo.Attacker?.UserId ?? ENVIRONMENT; // Default to ENVIRONMENT if null
            int victim = eventInfo.Userid?.UserId ?? -1;

            if (victim == -1) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Victim has invalid UserId in OnPlayerHurt.");
                return HookResult.Continue;
            }

            int damage = eventInfo.DmgHealth;
            int hitgroup = eventInfo.Hitgroup;

            if (attacker == victim && eventInfo.Weapon == "world") {
                attacker = ENVIRONMENT; // Environmental damage
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

            if (!hitboxGivenDamage.ContainsKey(attacker)) hitboxGivenDamage[attacker] = new Dictionary<int, Dictionary<int, int>>();
            if (!hitboxTakenDamage.ContainsKey(victim)) hitboxTakenDamage[victim] = new Dictionary<int, Dictionary<int, int>>();
            if (!hitboxGivenDamage[attacker].ContainsKey(victim)) hitboxGivenDamage[attacker][victim] = new Dictionary<int, int>();
            if (!hitboxTakenDamage[victim].ContainsKey(attacker)) hitboxTakenDamage[victim][attacker] = new Dictionary<int, int>();

            // Track damage
            damageGiven[attacker][victim] = damageGiven[attacker].GetValueOrDefault(victim, 0) + damage;
            damageTaken[victim][attacker] = damageTaken[victim].GetValueOrDefault(attacker, 0) + damage;

            // Track hits
            hitsGiven[attacker][victim] = hitsGiven[attacker].GetValueOrDefault(victim, 0) + 1;
            hitsTaken[victim][attacker] = hitsTaken[victim].GetValueOrDefault(attacker, 0) + 1;

            // Track hitbox-specific data
            hitboxGiven[attacker][victim][hitgroup] = hitboxGiven[attacker][victim].GetValueOrDefault(hitgroup, 0) + 1;
            hitboxTaken[victim][attacker][hitgroup] = hitboxTaken[victim][attacker].GetValueOrDefault(hitgroup, 0) + 1;

            // Correctly accumulate hitbox damage
            hitboxGivenDamage[attacker][victim][hitgroup] = hitboxGivenDamage[attacker][victim].GetValueOrDefault(hitgroup, 0) + damage;
            hitboxTakenDamage[victim][attacker][hitgroup] = hitboxTakenDamage[victim][attacker].GetValueOrDefault(hitgroup, 0) + damage;

            return HookResult.Continue;
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception in OnPlayerHurt: {ex.Message}\n{ex.StackTrace}");
            return HookResult.Continue;
        }
    }
    // Event handler for player death event
    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
        CCSPlayerController? player = eventInfo.Userid;
        int victimId = eventInfo.Userid?.UserId ?? -1;
        int attackerId = eventInfo.Attacker?.UserId ?? -1;

        if (attackerId >= 0 && victimId >= 0) {
            if (!killedPlayer.ContainsKey(attackerId)) {
                killedPlayer[attackerId] = new HashSet<int>();
            }
            killedPlayer[attackerId].Add(victimId);
        }

        // Schedule damage report    
        if (player != null && player.UserId.HasValue) {
            scheduleDamageReport(player);
        }

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
        var playersList = Utilities.GetPlayers();
        foreach (var player in playersList) {
            if (player.IsValid &&
                !player.IsHLTV &&
                player.UserId.HasValue ) {
                scheduleDamageReport(player);
            } 
        }

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
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Players list is null in UpdatePlayerNames.");
                return;
            }

            foreach (var player in playersList) {
                if (player == null) {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Found null player in players list.");
                    continue;
                }

                if (player.UserId.HasValue) {
                    int playerId = player.UserId.Value;

                    // Add or update the player's name in the dictionary
                    playerNames[playerId] = string.IsNullOrEmpty(player.PlayerName) ? "Bot" : player.PlayerName;

                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Updated player name: ID={playerId}, Name={playerNames[playerId]}");
                } else {
                    Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Player does not have a UserId.");
                }
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception in UpdatePlayerNames: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // Schedule a damage report for a specific player
    private void scheduleDamageReport(CCSPlayerController player) {
        if (player != null && player.UserId.HasValue && ! reportedPlayers.Contains(player.UserId.Value)) {
            reportedPlayers.Add(player.UserId.Value);
            osbase?.AddTimer(delay, () => {
                DisplayDamageReport(player);
            });
        }
    }

    // Display damage report for a specific player
    private void DisplayDamageReport(CCSPlayerController player) {
        List<string> report = new List<string>();
        if (player == null || player.UserId == null) return;
        int playerId = player.UserId.Value;

        bool hasVictimData = damageGiven.ContainsKey(playerId) && damageGiven[playerId].Count > 0;
        bool hasAttackerData = damageTaken.ContainsKey(playerId) && damageTaken[playerId].Count > 0;

        if (hasVictimData || hasAttackerData) {
            report.Add($"===[ Damage Report (hits:damage) ]===");
        }

        // Victim Section
        if (hasVictimData) {
            report.Add($"Victims:");
            foreach (var victim in damageGiven[playerId]) {
                string victimName = playerNames.GetValueOrDefault(victim.Key, "Unknown");
                int hits = hitsGiven[playerId].GetValueOrDefault(victim.Key, 0);
                int damage = victim.Value;

                string killedText = (killedPlayer.ContainsKey(playerId) && killedPlayer[playerId].Contains(victim.Key)) 
                    ? " (Killed)" 
                    : "";

                int calculatedHitboxDamage = 0;
                string hitGroupInfo = "";
                if (hitboxGiven.ContainsKey(playerId) && hitboxGiven[playerId].ContainsKey(victim.Key)) {
                    hitGroupInfo += " [";
                    foreach (var hitGroup in hitboxGiven[playerId][victim.Key]) {
                        int hitGroupDamage = hitboxGivenDamage[playerId][victim.Key].GetValueOrDefault(hitGroup.Key, 0);
                        calculatedHitboxDamage += hitGroupDamage;
                        hitGroupInfo += $"{hitboxName[hitGroup.Key]} {hitGroup.Value}:{hitGroupDamage}, ";
                    }
                    hitGroupInfo = hitGroupInfo.TrimEnd(',', ' ') + "]";
                }

                // Check for inconsistencies
                if (damage != calculatedHitboxDamage) {
                    hitGroupInfo += $" [Inconsistent: {damage} != {calculatedHitboxDamage}]";
                }

                report.Add($" - {victimName}{killedText}: {hits} hits, {damage} damage{hitGroupInfo}");
            }
        }

        // Attacker Section
        if (hasAttackerData) {
            report.Add($"Attackers:");
            foreach (var attacker in damageTaken[playerId]) {
                string attackerName = playerNames.GetValueOrDefault(attacker.Key, "Unknown");
                int hits = hitsTaken[playerId].GetValueOrDefault(attacker.Key, 0);
                int damage = attacker.Value;

                string killedByText = (killedPlayer.ContainsKey(attacker.Key) && killedPlayer[attacker.Key].Contains(playerId)) 
                    ? " (Killed by)" 
                    : "";

                int calculatedHitboxDamage = 0;
                string hitGroupInfo = "";
                if (hitboxTaken.ContainsKey(playerId) && hitboxTaken[playerId].ContainsKey(attacker.Key)) {
                    hitGroupInfo += " [";
                    foreach (var hitGroup in hitboxTaken[playerId][attacker.Key]) {
                        int hitGroupDamage = hitboxTakenDamage[playerId][attacker.Key].GetValueOrDefault(hitGroup.Key, 0);
                        calculatedHitboxDamage += hitGroupDamage;
                        hitGroupInfo += $"{hitboxName[hitGroup.Key]} {hitGroup.Value}:{hitGroupDamage}, ";
                    }
                    hitGroupInfo = hitGroupInfo.TrimEnd(',', ' ') + "]";
                }

                // Check for inconsistencies
                if (damage != calculatedHitboxDamage) {
                    hitGroupInfo += $" [Inconsistent: {damage} != {calculatedHitboxDamage}]";
                }

                report.Add($" - {attackerName}{killedByText}: {hits} hits, {damage} damage{hitGroupInfo}");
            }
        }

        if (report.Count > 0) {
            foreach (string line in report) {
                //Console.WriteLine(line);
                // Uncomment to send to chat
                player.PrintToChat(line);
            }
        }
    }


    // Helper method to clear all damage-related data
    private void ClearDamageData() {        
        damageGiven.Clear();
        damageTaken.Clear();
        hitsGiven.Clear();
        hitsTaken.Clear();
        killedPlayer.Clear();
        reportedPlayers.Clear();

        foreach (var attacker in hitboxGiven.Keys) {
            foreach (var victim in hitboxGiven[attacker].Keys) {
                hitboxGiven[attacker][victim].Clear();
            }
            hitboxGiven[attacker].Clear();
        }
        hitboxGiven.Clear();

        foreach (var victim in hitboxTaken.Keys) {
            foreach (var attacker in hitboxTaken[victim].Keys) {
                hitboxTaken[victim][attacker].Clear();
            }
            hitboxTaken[victim].Clear();
        }
        hitboxTaken.Clear();

        foreach (var attacker in hitboxGivenDamage.Keys) {
            foreach (var victim in hitboxGivenDamage[attacker].Keys) {
                hitboxGivenDamage[attacker][victim].Clear();
            }
            hitboxGivenDamage[attacker].Clear();
        }
        hitboxGivenDamage.Clear();

        foreach (var victim in hitboxTakenDamage.Keys) {
            foreach (var attacker in hitboxTakenDamage[victim].Keys) {
                hitboxTakenDamage[victim][attacker].Clear();
            }
            hitboxTakenDamage[victim].Clear();
        }
        hitboxTakenDamage.Clear();

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Damage data cleared.");
    }
    private void OnPlayerDisconnect(int playerId) {
        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Player disconnected: ID={playerId}");
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
