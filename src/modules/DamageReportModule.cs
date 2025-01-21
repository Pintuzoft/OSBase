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

    private string[] playerName = new string[MaxPlayers + 1];

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
        osbase.RegisterEventHandler<EventPlayerConnect>(OnPlayerConnect);
        osbase.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    private HookResult OnPlayerHurt(EventPlayerHurt eventInfo, GameEventInfo gameEventInfo) {
        if (eventInfo.Attacker?.UserId == null || eventInfo.Userid?.UserId == null) {
            Console.WriteLine("[DEBUG] OnPlayerHurt: Invalid Attacker or Victim.");
            return HookResult.Continue;
        }

        int attacker = eventInfo.Attacker.UserId.Value;
        int victim = eventInfo.Userid.UserId.Value;
        int damage = eventInfo.DmgHealth;
        int hitGroup = eventInfo.Hitgroup;

        damageGiven[attacker, victim] += damage;
        hitsGiven[attacker, victim]++;
        damageTaken[victim, attacker] += damage;
        hitsTaken[victim, attacker]++;

        Console.WriteLine($"[DEBUG] OnPlayerHurt: Attacker {attacker} hit Victim {victim} for {damage} damage. TotalDamageGiven[{attacker}, {victim}] = {damageGiven[attacker, victim]}.");

        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
        if (eventInfo.Userid?.UserId == null) {
            Console.WriteLine("[DEBUG] OnPlayerDeath: Invalid Victim.");
            return HookResult.Continue;
        }

        int victim = eventInfo.Userid.UserId.Value;
        DisplayDamageReport(victim);

        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {
        ClearDamageData();
        UpdatePlayerNames();
        Console.WriteLine("[INFO] Round started. Damage data cleared.");
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnect(EventPlayerConnect eventInfo, GameEventInfo gameEventInfo) {
        UpdatePlayerNames();
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect eventInfo, GameEventInfo gameEventInfo) {
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
        if (playerId == 0) {
            Console.WriteLine("[DEBUG] Skipping damage report for Player 0 (world or invalid).");
            return;
        }

        var playersList = Utilities.GetPlayers();
        var player = playersList.Find(p => p.UserId.HasValue && p.UserId.Value == playerId);

        if (player == null) {
            Console.WriteLine($"[DEBUG] Player {playerId} not found.");
            return;
        }

        player.PrintToChat($"===[ Damage Report for {playerName[playerId]} ]===");

        if (HasVictims(playerId)) {
            player.PrintToChat($"===[ Victims - Total: [{TotalHitsGiven(playerId)}:{TotalDamageGiven(playerId)}] ]===");
            for (int victim = 1; victim <= MaxPlayers; victim++) {
                if (IsVictim(playerId, victim)) {
                    Console.WriteLine($"[DEBUG] Victim {victim} found for Player {playerId}. Hits: {hitsGiven[playerId, victim]}, Damage: {damageGiven[playerId, victim]}.");
                    player.PrintToChat(FormatVictimReport(playerId, victim));
                }
            }
        } else {
            Console.WriteLine($"[DEBUG] No victims found for Player {playerId}.");
        }

        if (HasAttackers(playerId)) {
            player.PrintToChat($"===[ Attackers - Total: [{TotalHitsTaken(playerId)}:{TotalDamageTaken(playerId)}] ]===");
            for (int attacker = 1; attacker <= MaxPlayers; attacker++) {
                if (IsVictim(attacker, playerId)) {
                    Console.WriteLine($"[DEBUG] Attacker {attacker} found for Player {playerId}. Hits: {hitsTaken[playerId, attacker]}, Damage: {damageTaken[playerId, attacker]}.");
                    player.PrintToChat(FormatAttackerReport(attacker, playerId));
                }
            }
        } else {
            Console.WriteLine($"[DEBUG] No attackers found for Player {playerId}.");
        }
    }

    private string FormatVictimReport(int attacker, int victim) {
        string report = $" - {playerName[victim]}";
        if (damageGiven[attacker, victim] > 0) {
            report += $" ({hitsGiven[attacker, victim]} hits, {damageGiven[attacker, victim]} damage)";
        }
        return report;
    }

    private string FormatAttackerReport(int attacker, int victim) {
        string report = $" - {playerName[attacker]}";
        if (damageTaken[victim, attacker] > 0) {
            report += $" ({hitsTaken[victim, attacker]} hits, {damageTaken[victim, attacker]} damage)";
        }
        return report;
    }

    private bool HasVictims(int playerId) => TotalDamageGiven(playerId) > 0;

    private bool HasAttackers(int playerId) => TotalDamageTaken(playerId) > 0;

    private int TotalDamageGiven(int playerId) {
        int total = 0;
        for (int victim = 1; victim <= MaxPlayers; victim++) {
            total += damageGiven[playerId, victim];
        }
        return total;
    }

    private int TotalDamageTaken(int playerId) {
        int total = 0;
        for (int attacker = 1; attacker <= MaxPlayers; attacker++) {
            total += damageTaken[playerId, attacker];
        }
        return total;
    }

    private int TotalHitsGiven(int playerId) {
        int total = 0;
        for (int victim = 1; victim <= MaxPlayers; victim++) {
            total += hitsGiven[playerId, victim];
        }
        return total;
    }

    private int TotalHitsTaken(int playerId) {
        int total = 0;
        for (int attacker = 1; attacker <= MaxPlayers; attacker++) {
            total += hitsTaken[playerId, attacker];
        }
        return total;
    }

    private bool IsVictim(int attacker, int victim) => damageGiven[attacker, victim] > 0;

    private void ClearDamageData() {
        Array.Clear(damageGiven, 0, damageGiven.Length);
        Array.Clear(hitsGiven, 0, hitsGiven.Length);
        Array.Clear(damageTaken, 0, damageTaken.Length);
        Array.Clear(hitsTaken, 0, hitsTaken.Length);
    }
}