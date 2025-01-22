using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;

namespace OSBase.Modules;

public class DamageReportModule : IModule {
    public string ModuleName => "DamageReportModule";
    private OSBase? osbase;

    private const int MaxPlayers = 64;
    private const int MaxHitGroups = 10;

    private int[,] damageGiven = new int[MaxPlayers + 1, MaxPlayers + 1];
    private int[,] hitsGiven = new int[MaxPlayers + 1, MaxPlayers + 1];
    private int[,,] hitboxGiven = new int[MaxPlayers + 1, MaxPlayers + 1, MaxHitGroups + 1];
    private int[,,] hitboxGivenDamage = new int[MaxPlayers + 1, MaxPlayers + 1, MaxHitGroups + 1];

    private int[,] damageTaken = new int[MaxPlayers + 1, MaxPlayers + 1];
    private int[,] hitsTaken = new int[MaxPlayers + 1, MaxPlayers + 1];
    private int[,,] hitboxTaken = new int[MaxPlayers + 1, MaxPlayers + 1, MaxHitGroups + 1];
    private int[,,] hitboxTakenDamage = new int[MaxPlayers + 1, MaxPlayers + 1, MaxHitGroups + 1];
    private int[,] killedPlayer = new int[MaxPlayers + 1, MaxPlayers + 1];

    private string[] playerName = new string[MaxPlayers + 1];

    private readonly string[] hitboxName = {
        "Body", "Head", "Chest", "Stomach", "L-Arm", "R-Arm", "L-Leg", "R-Leg", "Neck", "Unknown", "Gear"
    };

    public void Load(OSBase inOsbase, ConfigModule inConfig) {
        osbase = inOsbase;

        // Initialize player names
        for (int i = 0; i <= MaxPlayers; i++) {
            playerName[i] = "Unknown";
        }

        // Register event handlers
        osbase.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        osbase.RegisterEventHandler<EventPlayerConnect>(OnPlayerConnect);

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    private HookResult OnPlayerHurt(EventPlayerHurt eventInfo, GameEventInfo gameEventInfo) {
        if (eventInfo.Attacker?.UserId == null) {
            Console.WriteLine("[ERROR] Attacker UserId is null.");
            return HookResult.Continue;
        }
        int attacker = eventInfo.Attacker.UserId.Value; // Assuming EventPlayerHurt has an Attacker property
        int victim = eventInfo.Userid?.UserId ?? -1;    // Assuming EventPlayerHurt has a Victim property
        if (victim == -1) {
            Console.WriteLine("[ERROR] Victim UserId is null.");
            return HookResult.Continue;
        }
        int damage = eventInfo.DmgHealth;    // Assuming EventPlayerHurt has a Damage property
        int hitgroup = eventInfo.Hitgroup; // Assuming EventPlayerHurt has a HitGroup property

        // Update damage and hitgroup data
        damageGiven[attacker, victim] += damage;
        damageTaken[victim, attacker] += damage;
        hitboxGiven[attacker, victim, hitgroup]++;
        hitboxGivenDamage[attacker, victim, hitgroup] += damage;
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
        if (eventInfo.Attacker?.UserId == null || eventInfo.Userid?.UserId == null) {
            return HookResult.Continue;
        }

        int attacker = eventInfo.Attacker.UserId.Value;
        int victim = eventInfo.Userid.UserId.Value;

        // Mark that the attacker killed the victim
        killedPlayer[attacker, victim] = 1;

        Console.WriteLine($"[DEBUG] Player {attacker} killed Player {victim}.");
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {
        ClearDamageData();
        UpdatePlayerNames();
        Console.WriteLine("[INFO] Round started. Damage data cleared.");
        return HookResult.Continue;
    }
    private HookResult OnRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
        Console.WriteLine("[DEBUG] Round ended. Generating damage reports.");

        for (int playerId = 0; playerId <= MaxPlayers; playerId++) {
            if (!string.IsNullOrEmpty(playerName[playerId]) && playerName[playerId] != "Disconnected") {
                DisplayDamageReport(playerId);
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerConnect(EventPlayerConnect eventInfo, GameEventInfo gameEventInfo) {
        UpdatePlayerNames();
        return HookResult.Continue;
    }

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

    private void DisplayDamageReport(int playerId) {
        Console.WriteLine($"===[ Damage Report for {playerName[playerId]} ]===");

        // Victims Section
        if (HasVictims(playerId)) {
            Console.WriteLine($"===[ Victims - Total: [{TotalHitsGiven(playerId)}:{TotalDamageGiven(playerId)}] (hits:damage) ]===");
            for (int victim = 0; victim <= MaxPlayers; victim++) {
                if (string.IsNullOrEmpty(playerName[playerId]) || playerName[playerId] == "Unknown") {
                    continue; // Skip inactive or unknown players
                }
                if (IsVictim(playerId, victim) && !string.IsNullOrEmpty(playerName[victim]) && playerName[victim] != "Unknown") {
                    string victimInfo = FetchVictimDamageInfo(playerId, victim);
                    Console.WriteLine(victimInfo);
                }
            }
        }

        // Attackers Section
        if (HasAttackers(playerId)) {
            Console.WriteLine($"===[ Attackers - Total: [{TotalHitsTaken(playerId)}:{TotalDamageTaken(playerId)}] (hits:damage) ]===");
            for (int attacker = 0; attacker <= MaxPlayers; attacker++) {
                if (string.IsNullOrEmpty(playerName[playerId]) || playerName[playerId] == "Unknown") {
                   continue; // Skip inactive or unknown players
                }
                if (IsVictim(attacker, playerId) && !string.IsNullOrEmpty(playerName[attacker]) && playerName[attacker] != "Unknown") {
                    string attackerInfo = FetchAttackerDamageInfo(attacker, playerId);
                    Console.WriteLine(attackerInfo);
                }
            }
        }
    }

    private void GenerateDamageReports() {
        Console.WriteLine("[DEBUG] Generating damage reports...");

        // Iterate over only active players
        for (int playerId = 0; playerId <= MaxPlayers; playerId++) {
            if (string.IsNullOrEmpty(playerName[playerId]) || playerName[playerId] == "Unknown") {
                continue; // Skip inactive or unknown players
            }

            // Check if the player has meaningful data
            bool hasVictims = HasVictims(playerId);
            bool hasAttackers = HasAttackers(playerId);

            if (!hasVictims && !hasAttackers) {
                Console.WriteLine($"[DEBUG] Skipping report for Player {playerId} ({playerName[playerId]}): No data.");
                continue;
            }

            // Generate and display the damage report
            DisplayDamageReport(playerId);
        }
    }

    private string FetchVictimDamageInfo(int attacker, int victim) {
        string info = $" - {playerName[victim]}";

        if (killedPlayer[attacker, victim] == 1) {
            info += " (Killed)";
        }

        info += $": {hitsGiven[attacker, victim]} hits, {damageGiven[attacker, victim]} dmg ->";

        for (int hitGroup = 0; hitGroup < MaxHitGroups; hitGroup++) {
            if (hitboxGiven[attacker, victim, hitGroup] > 0) {
                info += $" {hitboxName[hitGroup]} {hitboxGiven[attacker, victim, hitGroup]}:{hitboxGivenDamage[attacker, victim, hitGroup]}";              
            }
        }

        return info;
    }

    private string FetchAttackerDamageInfo(int attacker, int victim) {
        string info = $" - {playerName[attacker]}";

        if (killedPlayer[attacker, victim] == 1) {
            info += " (Killed)";
        }

        info += $": {hitsGiven[attacker, victim]} hits, {damageGiven[attacker, victim]} dmg ->";

        bool hasHitgroupData = false;
        for (int hitgroup = 0; hitgroup < MaxHitGroups; hitgroup++) {
            if (hitboxGiven[attacker, victim, hitgroup] > 0) {
                if (!hasHitgroupData) {
                    hasHitgroupData = true;
                    info += $" {hitboxName[hitgroup]} {hitboxGiven[attacker, victim, hitgroup]}:{hitboxGivenDamage[attacker, victim, hitgroup]}";
                } else {
                    info += $", {hitboxName[hitgroup]} {hitboxGiven[attacker, victim, hitgroup]}:{hitboxGivenDamage[attacker, victim, hitgroup]}";
                }
            }
        }

        if (!hasHitgroupData) {
            info += " No specific hitgroups recorded.";
        }

        return info;
    }

    private string FormatVictimReport(int attacker, int victim) {
        string report = $" - {playerName[victim]}";

        if (damageGiven[attacker, victim] > 0) {
            report += $" ({hitsGiven[attacker, victim]} hits, {damageGiven[attacker, victim]} damage)";
        }

        bool first = true;
        int totalHits = 0;
        int totalDamage = 0;

        for (int hitGroup = 0; hitGroup <= MaxHitGroups; hitGroup++) {
            if (hitboxGiven[attacker, victim, hitGroup] > 0) {
                int hits = hitboxGiven[attacker, victim, hitGroup];
                int damage = hitboxGivenDamage[attacker, victim, hitGroup];
                totalHits += hits;
                totalDamage += damage;

                string hitDetails = $"{hitboxName[hitGroup]} {hits}:{damage}";
                report += first ? $" - {hitDetails}" : $", {hitDetails}";
                first = false;
            }
        }

        // Debug totals
        Console.WriteLine($"[DEBUG] Victim {victim} <- Attacker {attacker}: Total Hits {totalHits}, Total Damage {totalDamage}.");

        return report;
    }

    private string FormatAttackerReport(int attacker, int victim) {
        string report = $" - {playerName[attacker]}";

        if (damageTaken[victim, attacker] > 0) {
            report += $" ({hitsTaken[victim, attacker]} hits, {damageTaken[victim, attacker]} damage)";
        }

        bool first = true;
        int totalHits = 0;
        int totalDamage = 0;

        for (int hitGroup = 0; hitGroup <= MaxHitGroups; hitGroup++) {
            if (hitboxTaken[victim, attacker, hitGroup] > 0) {
                int hits = hitboxTaken[victim, attacker, hitGroup];
                int damage = hitboxTakenDamage[victim, attacker, hitGroup];
                totalHits += hits;
                totalDamage += damage;

                string hitDetails = $"{hitboxName[hitGroup]} {hits}:{damage}";
                report += first ? $" - {hitDetails}" : $", {hitDetails}";
                first = false;
            }
        }

        // Debug totals
        Console.WriteLine($"[DEBUG] Attacker {attacker} -> Victim {victim}: Total Hits {totalHits}, Total Damage {totalDamage}.");

        return report;
    }

    private bool HasVictims(int playerId) {
        for (int victim = 0; victim < 4; victim++) { // Adjust to loop over first 4 players
            if (damageGiven[playerId, victim] > 0) {
                Console.WriteLine($"[DEBUG] HasVictims: Player {playerId} has Victim {victim} with Damage {damageGiven[playerId, victim]}.");
                return true;
            }
        }
        Console.WriteLine($"[DEBUG] HasVictims: Player {playerId} has no victims.");
        return false;
    }

    private bool HasAttackers(int playerId) {
        for (int attacker = 0; attacker < 4; attacker++) { // Adjust to loop over first 4 players
            if (damageTaken[playerId, attacker] > 0) {
                Console.WriteLine($"[DEBUG] HasAttackers: Player {playerId} was attacked by {attacker} with Damage {damageTaken[playerId, attacker]}.");
                return true;
            }
        }
        Console.WriteLine($"[DEBUG] HasAttackers: Player {playerId} has no attackers.");
        return false;
    }

    private int TotalHitsTaken(int playerId) {
        int total = 0;
        for (int attacker = 1; attacker <= MaxPlayers; attacker++) {
            total += hitsTaken[playerId, attacker];
        }
        return total;
    }

    private int TotalDamageGiven(int playerId) {
        int total = 0;
        for (int victim = 0; victim < 4; victim++) { // Limit to first 4 players for debugging
            if (damageGiven[playerId, victim] > 0) {
                total += damageGiven[playerId, victim];
                Console.WriteLine($"[DEBUG] Adding damageGiven[{playerId}, {victim}] = {damageGiven[playerId, victim]} to TotalDamageGiven[{playerId}].");
            }
        }
        Console.WriteLine($"[DEBUG] TotalDamageGiven[{playerId}] = {total}");
        return total;
    }

    private int TotalDamageTaken(int playerId) {
        int total = 0;
        for (int attacker = 0; attacker < 4; attacker++) { // Limit to first 4 players for debugging
            if (damageTaken[playerId, attacker] > 0) {
                total += damageTaken[playerId, attacker];
                Console.WriteLine($"[DEBUG] Adding damageTaken[{playerId}, {attacker}] = {damageTaken[playerId, attacker]} to TotalDamageTaken[{playerId}].");
            }
        }
        Console.WriteLine($"[DEBUG] TotalDamageTaken[{playerId}] = {total}");
        return total;
    }

    private int TotalHitsGiven(int playerId) {
        int total = 0;
        for (int victim = 1; victim <= MaxPlayers; victim++) {
            if (hitsGiven[playerId, victim] > 0) {
                total += hitsGiven[playerId, victim];
            }
        }
        Console.WriteLine($"[DEBUG] TotalHitsGiven[{playerId}] = {total}");
        return total;
    }
    private bool IsVictim(int attacker, int victim) {
        bool result = damageGiven[attacker, victim] > 0;
        Console.WriteLine($"[DEBUG] IsVictim({attacker}, {victim}): damageGiven[{attacker}, {victim}] = {damageGiven[attacker, victim]}, Result = {result}");
        return result;
    }

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
        Array.Clear(killedPlayer, 0, killedPlayer.Length); // Clear killedPlayer
    }
}