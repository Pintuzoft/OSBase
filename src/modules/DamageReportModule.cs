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

    // Constant to represent environmental kills
    private const int ENVIRONMENT = -1;

    // Names of hit groups for easier identification in reports
    private readonly string[] hitboxName = {
        "Body", "Head", "Chest", "Stomach", "L-Arm", "R-Arm", "L-Leg", "R-Leg", "Neck", "Unknown", "Gear"
    };

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
        osbase.RegisterEventHandler<EventPlayerConnect>(OnPlayerConnect);

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

        // Validate hitgroup index
        if (hitgroup < 0 || hitgroup >= MaxHitGroups) {
            Console.WriteLine($"[ERROR] Invalid hitgroup: {hitgroup}. Skipping hitgroup update.");
            return HookResult.Continue;
        }

        // Track damage and hits for the attacker (Given) and victim (Taken)
        damageGiven[attacker, victim] += damage;
        damageTaken[victim, attacker] += damage;

        hitsGiven[attacker, victim]++;
        hitsTaken[victim, attacker]++;

        // Update hitgroup data for attacker (Given) and victim (Taken)
        hitboxGiven[attacker, victim, hitgroup]++;
        hitboxGivenDamage[attacker, victim, hitgroup] += damage;

        hitboxTaken[victim, attacker, hitgroup]++;
        hitboxTakenDamage[victim, attacker, hitgroup] += damage;

        // Debug logs
        Console.WriteLine($"[DEBUG] Damage recorded: Attacker {attacker} -> Victim {victim}, Damage: {damage}, HitGroup: {hitgroup}");
        Console.WriteLine($"[DEBUG] hitboxGiven[{attacker}, {victim}, {hitgroup}] = {hitboxGiven[attacker, victim, hitgroup]}");
        Console.WriteLine($"[DEBUG] hitboxTaken[{victim}, {attacker}, {hitgroup}] = {hitboxTaken[victim, attacker, hitgroup]}");

        return HookResult.Continue;
    }

    // Event handler for player death event
    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
        int victim = eventInfo.Userid?.UserId ?? -1; // Victim ID
        int attacker = eventInfo.Attacker?.UserId ?? ENVIRONMENT; // Attacker ID or ENVIRONMENT for world kills
        string weapon = eventInfo.Weapon ?? "unknown";

        Console.WriteLine($"[DEBUG] Player {victim} was killed by {attacker} with weapon: {weapon}");

        // Validate victim
        if (victim < 0 || victim > MaxPlayers) {
            Console.WriteLine($"[ERROR] Invalid victim ID: {victim}. Skipping death handling.");
            return HookResult.Continue;
        }

        // Register kills
        if (attacker == victim) {
            // Suicide
            killedPlayer[victim, victim] = 1;
            Console.WriteLine($"[DEBUG] Player {victim} committed suicide.");
        } else if (attacker == ENVIRONMENT) {
            // Environmental kill
            killedPlayer[ENVIRONMENT, victim] = 1;
            Console.WriteLine($"[DEBUG] Player {victim} was killed by environment (weapon: {weapon}).");
        } else {
            // Normal player kill
            killedPlayer[attacker, victim] = 1;
            Console.WriteLine($"[DEBUG] Player {attacker} killed Player {victim} with {weapon}.");
        }

        return HookResult.Continue;
    }
    // Event handler for round start
    private HookResult OnRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {
        ClearDamageData(); // Reset all damage data
        UpdatePlayerNames(); // Refresh player names
        Console.WriteLine("[INFO] Round started. Damage data cleared.");
        return HookResult.Continue;
    }

    // Event handler for round end
    private HookResult OnRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
        Console.WriteLine("[DEBUG] Round ended. Generating damage reports.");

        // Generate damage report for all active players
        for (int playerId = 0; playerId <= MaxPlayers; playerId++) {
            if (!string.IsNullOrEmpty(playerName[playerId]) && playerName[playerId] != "Disconnected") {
                DisplayDamageReport(playerId);
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
        var playersList = Utilities.GetPlayers();
        foreach (var player in playersList) {
            if (player.UserId.HasValue) {
                int playerId = player.UserId.Value;
                playerName[playerId] = string.IsNullOrEmpty(player.PlayerName) ? "Bot" : player.PlayerName;
                Console.WriteLine($"[DEBUG] Updated PlayerName[{playerId}] = {playerName[playerId]}.");
            }
        }
    }

    // Display damage report for a specific player
    private void DisplayDamageReport(int playerId) {
        Console.WriteLine($"===[ Damage Report for {playerName[playerId]} ]===");

        if (HasVictims(playerId)) {
            Console.WriteLine($"===[ Victims: Hits: {TotalHitsGiven(playerId)}, Damage: {TotalDamageGiven(playerId)} ]===");
            for (int victim = 0; victim <= MaxPlayers; victim++) {
                if (IsVictim(playerId, victim)) {
                    Console.WriteLine(FetchVictimDamageInfo(playerId, victim));
                }
            }
        }

        if (HasAttackers(playerId)) {
            Console.WriteLine($"===[ Attackers: Hits: {TotalHitsTaken(playerId)}, Damage: {TotalDamageTaken(playerId)} ]===");
            for (int attacker = 0; attacker <= MaxPlayers; attacker++) {
                if (IsVictim(attacker, playerId)) {
                    Console.WriteLine(FetchAttackerDamageInfo(attacker, playerId));
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
        string info = $" - {playerName[victim]}: {hitsGiven[attacker, victim]} hits, {damageGiven[attacker, victim]} damage";
        for (int hitGroup = 0; hitGroup < MaxHitGroups; hitGroup++) {
            if (hitboxGiven[attacker, victim, hitGroup] > 0) {
                info += $", {hitboxName[hitGroup]} {hitboxGiven[attacker, victim, hitGroup]}:{hitboxGivenDamage[attacker, victim, hitGroup]}";
            }
        }
        return info;
    }

    // Fetch detailed damage info for an attacker
    private string FetchAttackerDamageInfo(int attacker, int victim) {
        string info = $" - {playerName[attacker]}: {hitsTaken[victim, attacker]} hits, {damageTaken[victim, attacker]} damage";
        for (int hitGroup = 0; hitGroup < MaxHitGroups; hitGroup++) {
            if (hitboxTaken[victim, attacker, hitGroup] > 0) {
                info += $", {hitboxName[hitGroup]} {hitboxTaken[victim, attacker, hitGroup]}:{hitboxTakenDamage[victim, attacker, hitGroup]}";
            }
        }
        return info;
    }

    // Check if a player has inflicted damage on others
    private bool HasVictims(int playerId) {
        for (int victim = 0; victim <= MaxPlayers; victim++) {
            if (damageGiven[playerId, victim] > 0) return true;
        }
        return false;
    }

    // Check if a player has taken damage from others
    private bool HasAttackers(int playerId) {
        for (int attacker = 0; attacker <= MaxPlayers; attacker++) {
            if (damageTaken[playerId, attacker] > 0) return true;
        }
        return false;
    }

    // Check if a player is a victim of another player
    private bool IsVictim(int attacker, int victim) {
        return damageGiven[attacker, victim] > 0;
    }

    // Helper method to clear all damage-related data
    private void ClearDamageData() {
        Console.WriteLine("[DEBUG] Clearing damage data.");
        Array.Clear(damageGiven, 0, damageGiven.Length);
        Array.Clear(damageTaken, 0, damageTaken.Length);
        Array.Clear(hitsGiven, 0, hitsGiven.Length);
        Array.Clear(hitsTaken, 0, hitsTaken.Length);
        Array.Clear(hitboxGiven, 0, hitboxGiven.Length);
        Array.Clear(hitboxGivenDamage, 0, hitboxGivenDamage.Length);
        Array.Clear(hitboxTaken, 0, hitboxTaken.Length);
        Array.Clear(hitboxTakenDamage, 0, hitboxTakenDamage.Length);
        Array.Clear(killedPlayer, 0, killedPlayer.Length);
    }
}
