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

    // 2D arrays to track damage and hits between players
    private int[,] damageGiven = new int[MaxPlayers + 1, MaxPlayers + 1];
    private int[,] hitsGiven = new int[MaxPlayers + 1, MaxPlayers + 1];

    // 3D arrays to track hitbox-specific data
    private int[,,] hitboxGiven = new int[MaxPlayers + 1, MaxPlayers + 1, MaxHitGroups + 1];
    private int[,,] hitboxGivenDamage = new int[MaxPlayers + 1, MaxPlayers + 1, MaxHitGroups + 1];

    private int[,] damageTaken = new int[MaxPlayers + 1, MaxPlayers + 1];
    private int[,] hitsTaken = new int[MaxPlayers + 1, MaxPlayers + 1];
    private int[,,] hitboxTaken = new int[MaxPlayers + 1, MaxPlayers + 1, MaxHitGroups + 1];
    private int[,,] hitboxTakenDamage = new int[MaxPlayers + 1, MaxPlayers + 1, MaxHitGroups + 1];
    private int[,] killedPlayer = new int[MaxPlayers + 1, MaxPlayers + 1]; // Tracks kills between players

    private string[] playerName = new string[MaxPlayers + 1]; // Stores player names

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

        // Initialize player names to "Unknown"
        for (int i = 0; i <= MaxPlayers; i++) {
            playerName[i] = "Unknown";
        }

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

        // Check if this is a slap (environmental damage)
        if (attacker == victim && eventInfo.Weapon == "world") {
            Console.WriteLine($"[DEBUG] Slap detected. Assigning damage to environment.");
            attacker = ENVIRONMENT; // Assign damage to environment
        }

        // Validate hitgroup index
        if (hitgroup < 0 || hitgroup >= MaxHitGroups) {
            Console.WriteLine($"[ERROR] Invalid hitgroup: {hitgroup}. Skipping hitgroup update.");
            return HookResult.Continue;
        }

        // Track damage and hits
        damageGiven[attacker, victim] += damage;
        damageTaken[victim, attacker] += damage;
        hitsGiven[attacker, victim]++;
        hitsTaken[victim, attacker]++;
        hitboxGiven[attacker, victim, hitgroup]++;
        hitboxGivenDamage[attacker, victim, hitgroup] += damage;
        hitboxTaken[victim, attacker, hitgroup]++;
        hitboxTakenDamage[victim, attacker, hitgroup] += damage;

        return HookResult.Continue;
    }

    // Event handler for player death event
    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
        CCSPlayerController? victim = eventInfo.Userid;
        int victimId = eventInfo.Userid?.UserId ?? -1;

        // Skip if the round has ended or the player was already reported
        if (osbase == null || victim == null || victimId < 0 || !IsPlayerConnected(victimId)) {
            return HookResult.Continue;
        }

        Console.WriteLine($"[DEBUG] Player {victimId} was killed. Scheduling damage report...");

        // Schedule the damage report
        osbase.AddTimer(delay, () => {
            Console.WriteLine($"[DEBUG] Sending delayed damage report to player {victimId} ({playerName[victimId]}).");
            DisplayDamageReport(victim);
        });

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
                    playerName[playerId] = string.IsNullOrEmpty(player.PlayerName) ? "Bot" : player.PlayerName;
                    Console.WriteLine($"[DEBUG] Updated player name: ID={playerId}, Name={playerName[playerId]}");
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
        if ( player == null || player.UserId == null ) {
            Console.WriteLine("[ERROR] Invalid player or player ID in DisplayDamageReport.");
            return;
        }
        int playerId = player.UserId.Value;
        bool hasVictimData = HasVictims(playerId);
        bool hasAttackerData = HasAttackers(playerId);

        if ( reportedPlayers.Contains(player.UserId.Value) ) {
            return;
        }

        // Mark player as reported so we dont report to them again
        reportedPlayers.Add(playerId);

        // Only send the title if there's any data to show
        if (hasVictimData || hasAttackerData) {
            player.PrintToChat("===[ Damage Report (hits:damage) ]===");
        }

        // Victims Section
        if (hasVictimData) {
            player.PrintToChat($"Victims ({TotalHitsGiven(playerId)} hits, {TotalDamageGiven(playerId)} damage):");
            for (int victim = 0; victim <= MaxPlayers; victim++) {
                if (IsVictim(playerId, victim)) {
                    string victimInfo = FetchVictimDamageInfo(playerId, victim);
                    player.PrintToChat($" - {victimInfo}");
                }
            }
        }

        // Attackers Section
        if (hasAttackerData) {
            player.PrintToChat($"Attackers ({TotalHitsTaken(playerId)} hits, {TotalDamageTaken(playerId)} damage):");
            for (int attacker = 0; attacker <= MaxPlayers; attacker++) {
                if (IsVictim(attacker, playerId)) {
                    string attackerInfo = FetchAttackerDamageInfo(attacker, playerId);
                    player.PrintToChat($" - {attackerInfo}");
                }
            }
        }
    }

    // Calculate the total damage a player has given
    private int TotalDamageGiven(int playerId) {
        int total = 0;
        for (int victim = 0; victim <= MaxPlayers; victim++) {
            total += damageGiven[playerId, victim];
        }
        return total;
    }

    // Calculate the total hits a player has given
    private int TotalHitsGiven(int playerId) {
        int total = 0;
        for (int victim = 0; victim <= MaxPlayers; victim++) {
            total += hitsGiven[playerId, victim];
        }
        return total;
    }

    // Calculate the total damage a player has taken
    private int TotalDamageTaken(int playerId) {
        int total = 0;
        for (int attacker = 0; attacker <= MaxPlayers; attacker++) {
            total += damageTaken[playerId, attacker];
        }
        return total;
    }

    // Calculate the total hits a player has taken
    private int TotalHitsTaken(int playerId) {
        int total = 0;
        for (int attacker = 0; attacker <= MaxPlayers; attacker++) {
            total += hitsTaken[playerId, attacker];
        }
        return total;
    }

    // Fetch detailed damage info for a victim
    private string FetchVictimDamageInfo(int attacker, int victim) {
        string info = $"{playerName[victim]}";

        if (killedPlayer[attacker, victim] == 1) {
            info += " (Killed)";
        }

        info += $": ";
        for (int hitGroup = 0; hitGroup < MaxHitGroups; hitGroup++) {
            if (hitboxGiven[attacker, victim, hitGroup] > 0) {
                info += $"{hitboxName[hitGroup]} {hitboxGiven[attacker, victim, hitGroup]}:{hitboxGivenDamage[attacker, victim, hitGroup]}, ";
            }
        }

        // Trim trailing comma and space
        return info.TrimEnd(',', ' ');
    }

    // Fetch detailed damage info for an attacker
    private string FetchAttackerDamageInfo(int attacker, int victim) {
        string info = $"{playerName[attacker]}";

        if (killedPlayer[attacker, victim] == 1) {
            info += " (Killed by)";
        }

        info += $": ";
        for (int hitGroup = 0; hitGroup < MaxHitGroups; hitGroup++) {
            if (hitboxTaken[victim, attacker, hitGroup] > 0) {
                info += $"{hitboxName[hitGroup]} {hitboxTaken[victim, attacker, hitGroup]}:{hitboxTakenDamage[victim, attacker, hitGroup]}, ";
            }
        }

        // Trim trailing comma and space
        return info.TrimEnd(',', ' ');
    }

    // Check if a player has inflicted damage on others
    private bool HasVictims(int playerId) {
        for (int victim = 0; victim <= MaxPlayers; victim++) {
            if (damageGiven[playerId, victim] > 0) 
                return true;
        }
        return false;
    }

    // Check if a player has taken damage from others
    private bool HasAttackers(int playerId) {
        for (int attacker = 0; attacker <= MaxPlayers; attacker++) {
            if (damageTaken[playerId, attacker] > 0) 
                return true;
        }
        return false;
    }

    // Check if a player is a victim of another player
    private bool IsVictim(int attacker, int victim) {
        return damageGiven[attacker, victim] > 0;
    }

    // Helper method to clear all damage-related data
    private void ClearDamageData() {
        Array.Clear(damageGiven, 0, damageGiven.Length);
        Array.Clear(damageTaken, 0, damageTaken.Length);
        Array.Clear(hitsGiven, 0, hitsGiven.Length);
        Array.Clear(hitsTaken, 0, hitsTaken.Length);
        Array.Clear(hitboxGiven, 0, hitboxGiven.Length);
        Array.Clear(hitboxGivenDamage, 0, hitboxGivenDamage.Length);
        Array.Clear(hitboxTaken, 0, hitboxTaken.Length);
        Array.Clear(hitboxTakenDamage, 0, hitboxTakenDamage.Length);
        Array.Clear(killedPlayer, 0, killedPlayer.Length);
        reportedPlayers.Clear();
    }
}
