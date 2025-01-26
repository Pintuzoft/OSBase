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

    // Constants for maximum players and hit groups
    private const int MaxPlayers = 64;
    private const int MaxHitGroups = 10;

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
    public void Load(OSBase inOsbase, ConfigModule inConfig) {
        osbase = inOsbase; // Set the OSBase reference

        // Register event handlers for various game events
        osbase.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        osbase.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);


        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    // Event handler for player hurt event
    private HookResult OnPlayerHurt(EventPlayerHurt eventInfo, GameEventInfo gameEventInfo) {
        // Validate attacker and victim
        if (eventInfo.Attacker?.UserId == null || eventInfo.Userid?.UserId == null) {
            Console.WriteLine("[ERROR] Missing attacker or victim in OnPlayerHurt.");
            return HookResult.Continue;
        }

        int attacker = eventInfo.Attacker.UserId.Value;
        int victim = eventInfo.Userid.UserId.Value;
        int damage = eventInfo.DmgHealth;
        int hitgroup = eventInfo.Hitgroup;

        // Assign environmental damage
        if (attacker == victim && eventInfo.Weapon == "world") {
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
            osbase?.AddTimer(delay, () => DisplayDamageReport(eventInfo.Userid));
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
        osbase?.AddTimer(delay, () => {
            var playersList = Utilities.GetPlayers();
            foreach (var player in playersList) {
                if (player.IsValid &&
                    !player.IsHLTV &&
                    player.UserId.HasValue ) {
                    DisplayDamageReport(player);
                } 
            }
        });

        return HookResult.Continue;
    }

    // Check if a player is connected
    private bool IsPlayerConnected(int playerId) {
        var playersList = Utilities.GetPlayers();
        foreach (var player in playersList) {
            if (player.UserId.HasValue && player.UserId.Value == playerId) {
                return true;
            }
        }
        return false;
    }

    // Event handler for player connect
    private HookResult OnPlayerConnectFull(EventPlayerConnectFull eventInfo, GameEventInfo gameEventInfo) {
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
        if (player == null || player.UserId == null) return;
        int playerId = player.UserId.Value;

        if (reportedPlayers.Contains(playerId)) return;
        reportedPlayers.Add(playerId);

        bool hasVictimData = damageGiven.ContainsKey(playerId) && damageGiven[playerId].Count > 0;
        bool hasAttackerData = damageTaken.ContainsKey(playerId) && damageTaken[playerId].Count > 0;

        if (hasVictimData || hasAttackerData) {
            player.PrintToChat("===[ Damage Report (hits:damage) ]===");
        }

        if (hasVictimData) {
            player.PrintToChat($"Victims:");
            foreach (var victim in damageGiven[playerId]) {
                string victimName = playerNames.GetValueOrDefault(victim.Key, "Unknown");
                int hits = hitsGiven[playerId].GetValueOrDefault(victim.Key, 0);
                int damage = victim.Value;
                player.PrintToChat($" - {victimName}: {hits} hits, {damage} damage");
            }
        }

        if (hasAttackerData) {
            player.PrintToChat($"Attackers:");
            foreach (var attacker in damageTaken[playerId]) {
                string attackerName = playerNames.GetValueOrDefault(attacker.Key, "Unknown");
                int hits = hitsTaken[playerId].GetValueOrDefault(attacker.Key, 0);
                int damage = attacker.Value;
                player.PrintToChat($" - {attackerName}: {hits} hits, {damage} damage");
            }
        }
    }

    // Calculate the total damage a player has given
    private int TotalDamageGiven(int playerId) {
        int total = 0;

        // Check if the player has given damage
        if (damageGiven.ContainsKey(playerId)) {
            foreach (var victimEntry in damageGiven[playerId]) {
                total += victimEntry.Value; // Sum the damage given to each victim
            }
        }

        return total;
    }

    // Calculate the total hits a player has given
    private int TotalHitsGiven(int playerId) {
        int total = 0;

        // Check if the player has hit other players
        if (hitsGiven.ContainsKey(playerId)) {
            foreach (var victimEntry in hitsGiven[playerId]) {
                total += victimEntry.Value; // Sum the hits given to each victim
            }
        }

        return total;
    }

    // Calculate the total damage a player has taken
    private int TotalDamageTaken(int playerId) {
        int total = 0;

        // Check if the player has taken damage
        if (damageTaken.ContainsKey(playerId)) {
            foreach (var attackerEntry in damageTaken[playerId]) {
                total += attackerEntry.Value; // Sum the damage taken from each attacker
            }
        }

        return total;
    }

    // Calculate the total hits a player has taken
    private int TotalHitsTaken(int playerId) {
        int total = 0;

        // Check if the player has been hit
        if (hitsTaken.ContainsKey(playerId)) {
            foreach (var attackerEntry in hitsTaken[playerId]) {
                total += attackerEntry.Value; // Sum the hits taken from each attacker
            }
        }

        return total;
    }

    // Fetch detailed damage info for a victim
    private string FetchVictimDamageInfo(int attacker, int victim) {
        // Start building the info string with the victim's name
        string info = $"{(playerNames.ContainsKey(victim) ? playerNames[victim] : "Unknown")}";

        // Check if the attacker killed the victim
        if (killedPlayer.ContainsKey(attacker) && killedPlayer[attacker].Contains(victim)) {
            info += " (Killed)";
        }

        info += $": ";

        // Loop through hit groups to gather hitbox data
        if (hitboxGiven.ContainsKey(attacker) && 
            hitboxGiven[attacker].ContainsKey(victim) &&
            hitboxGivenDamage.ContainsKey(attacker) && 
            hitboxGivenDamage[attacker].ContainsKey(victim)) 
        {
            for (int hitGroup = 0; hitGroup < MaxHitGroups; hitGroup++) {
                if (hitboxGiven[attacker][victim].ContainsKey(hitGroup) && 
                    hitboxGiven[attacker][victim][hitGroup] > 0) {
                    info += $"{hitboxName[hitGroup]} {hitboxGiven[attacker][victim][hitGroup]}:{hitboxGivenDamage[attacker][victim][hitGroup]}, ";
                }
            }
        }

        // Trim trailing comma and space
        return info.TrimEnd(',', ' ');
    }

    // Fetch detailed damage info for an attacker
private string FetchAttackerDamageInfo(int attacker, int victim) {
    // Start building the info string with the attacker's name
    string info = $"{(playerNames.ContainsKey(attacker) ? playerNames[attacker] : "Unknown")}";

    // Check if the attacker killed the victim
    if (killedPlayer.ContainsKey(attacker) && killedPlayer[attacker].Contains(victim)) {
        info += " (Killed by)";
    }

    info += $": ";

    // Loop through hit groups to gather hitbox data
    if (hitboxTaken.ContainsKey(victim) && 
        hitboxTaken[victim].ContainsKey(attacker) &&
        hitboxTakenDamage.ContainsKey(victim) && 
        hitboxTakenDamage[victim].ContainsKey(attacker)) 
    {
        for (int hitGroup = 0; hitGroup < MaxHitGroups; hitGroup++) {
            if (hitboxTaken[victim][attacker].ContainsKey(hitGroup) && 
                hitboxTaken[victim][attacker][hitGroup] > 0) {
                info += $"{hitboxName[hitGroup]} {hitboxTaken[victim][attacker][hitGroup]}:{hitboxTakenDamage[victim][attacker][hitGroup]}, ";
            }
        }
    }

    // Trim trailing comma and space
    return info.TrimEnd(',', ' ');
}

    // Check if a player has inflicted damage on others
    private bool HasVictims(int playerId) {
        // Check if the player exists in the `damageGiven` dictionary and has inflicted damage on any victim
        return damageGiven.ContainsKey(playerId) && damageGiven[playerId].Count > 0;
    }

    // Check if a player has taken damage from others
    private bool HasAttackers(int playerId) {
        // Check if the player exists in the `damageTaken` dictionary and has taken damage from any attacker
        return damageTaken.ContainsKey(playerId) && damageTaken[playerId].Count > 0;
    }

    // Check if a player is a victim of another player
    private bool IsVictim(int attacker, int victim) {
        // Check if the attacker exists in the `damageGiven` dictionary and has inflicted damage on the victim
        return damageGiven.ContainsKey(attacker) && 
            damageGiven[attacker].ContainsKey(victim) && 
            damageGiven[attacker][victim] > 0;
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
}
