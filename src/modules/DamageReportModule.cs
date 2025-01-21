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
    private int[,] damageTaken = new int[MaxPlayers + 1, MaxPlayers + 1];
    private int[,] hitsTaken = new int[MaxPlayers + 1, MaxPlayers + 1];
    private string[] playerName = new string[MaxPlayers + 1];

    public void Load(OSBase inOsbase, ConfigModule inConfig) {
        osbase = inOsbase;

        for (int i = 0; i <= MaxPlayers; i++) {
            playerName[i] = "Unknown";
        }

        osbase.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);

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

        damageGiven[attacker, victim] += damage;
        hitsGiven[attacker, victim]++;
        damageTaken[victim, attacker] += damage;
        hitsTaken[victim, attacker]++;

        Console.WriteLine($"[DEBUG] OnPlayerHurt: Attacker {attacker} hit Victim {victim} for {damage} damage.");
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
        if (eventInfo.Userid?.UserId == null) {
            Console.WriteLine("[DEBUG] OnPlayerDeath: Invalid Victim.");
            return HookResult.Continue;
        }

        int victim = eventInfo.Userid.UserId.Value;
        Console.WriteLine($"[DEBUG] OnPlayerDeath: Showing damage report for Victim {victim}.");
        DisplayDamageReport(victim);
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {
        Console.WriteLine("[INFO] Round started. Clearing damage data.");
        ClearDamageData();
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
        Console.WriteLine("[INFO] Round ended. Displaying damage reports.");
        DisplayAllDamageReports();
        return HookResult.Continue;
    }

    private void DisplayDamageReport(int playerId) {
        Console.WriteLine($"[DEBUG] Displaying damage report for Player {playerId}.");

        var playersList = Utilities.GetPlayers();
        var player = playersList.Find(p => p.UserId.HasValue && p.UserId.Value == playerId);

        if (player == null) {
            Console.WriteLine($"[DEBUG] Player {playerId} not found.");
            return;
        }

        player.PrintToChat($"===[ Damage Report for {playerName[playerId]} ]===");

        for (int victim = 1; victim <= MaxPlayers; victim++) {
            if (damageGiven[playerId, victim] > 0) {
                player.PrintToChat($"To {playerName[victim]}: {hitsGiven[playerId, victim]} hits, {damageGiven[playerId, victim]} damage.");
                Console.WriteLine($"[DEBUG] DamageGiven: Player {playerId} -> Victim {victim}: {damageGiven[playerId, victim]} damage.");
            }
        }

        for (int attacker = 1; attacker <= MaxPlayers; attacker++) {
            if (damageTaken[playerId, attacker] > 0) {
                player.PrintToChat($"From {playerName[attacker]}: {hitsTaken[playerId, attacker]} hits, {damageTaken[playerId, attacker]} damage.");
                Console.WriteLine($"[DEBUG] DamageTaken: Player {playerId} <- Attacker {attacker}: {damageTaken[playerId, attacker]} damage.");
            }
        }
    }

    private void DisplayAllDamageReports() {
        var playersList = Utilities.GetPlayers();
        foreach (var player in playersList) {
            if (player.UserId.HasValue) {
                DisplayDamageReport(player.UserId.Value);
            }
        }
    }

    private void ClearDamageData() {
        Array.Clear(damageGiven, 0, damageGiven.Length);
        Array.Clear(hitsGiven, 0, hitsGiven.Length);
        Array.Clear(damageTaken, 0, damageTaken.Length);
        Array.Clear(hitsTaken, 0, hitsTaken.Length);
        Console.WriteLine("[DEBUG] Damage data cleared.");
    }
}