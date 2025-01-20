using System;
using System.IO;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Events;
using System.Diagnostics.Tracing;
using System.Reflection;

namespace OSBase.Modules;

public class DamageReportModule : IModule {
    public string ModuleName => "DamageReportModule";
    private OSBase? osbase;
    private ConfigModule? config;

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
    private int[] showReportAgain = new int[MaxPlayers + 1];

    private string[] playerName = new string[MaxPlayers + 1];
    private string[] hitboxName = new string[MaxHitGroups + 1];

    public void Load(OSBase inOsbase, ConfigModule inConfig) {
        osbase = inOsbase;
        config = inConfig;

        config.RegisterGlobalConfigValue("showdamagereport", "1");

        InitializePlayerNames();

        // Register event handlers
        osbase.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    private string hitGroupToString(int hitGroup) {
        switch (hitGroup) {
            case 0:
                return "Body";
            case 1:
                return "Head";
            case 2:
                return "Chest";
            case 3:
                return "Stomach";
            case 4:
                return "L-Arm";
            case 5:
                return "R-Arm";
            case 6:
                return "L-Leg";
            case 7:
                return "R-Leg";
            case 8:
                return "Neck";
            case 10:
                return "Gear";
            default:
                return "Unknown";
        }
    }

    private void InitializePlayerNames() {
        for (int i = 0; i <= MaxPlayers; i++) {
            playerName[i] = "World";
        }
    }

    private HookResult OnRoundStart(EventRoundStart eventInfo, GameEventInfo gameEventInfo) {
        ClearAllDamageData();
        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Round started, damage data cleared.");
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd eventInfo, GameEventInfo gameEventInfo) {
        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Round ended. Displaying damage reports...");
        DisplayDamageReports();
        return HookResult.Continue;
    }

    private void DisplayDamageReports() {
        List<CCSPlayerController> playersList = Utilities.GetPlayers(); // Get all players
        foreach (var player in playersList) {
            if (player.UserId.HasValue) {
                int playerId = player.UserId.Value;
                if (damageGiven[playerId, 0] > 0 || damageTaken[playerId, 0] > 0) {
                    player.PrintToChat($"===[ Damage Report for {playerName[playerId]} ]===");

                    // Show damage given
                    for (int victim = 1; victim <= MaxPlayers; victim++) {
                        if (damageGiven[playerId, victim] > 0) {
                            player.PrintToChat($"To {playerName[victim]}: {hitsGiven[playerId, victim]} hits, {damageGiven[playerId, victim]} damage.");
                        }
                    }

                    // Show damage taken
                    for (int attacker = 1; attacker <= MaxPlayers; attacker++) {
                        if (damageTaken[playerId, attacker] > 0) {
                            player.PrintToChat($"From {playerName[attacker]}: {hitsTaken[playerId, attacker]} hits, {damageTaken[playerId, attacker]} damage.");
                        }
                    }
                }
            }
        }
    }

    private HookResult OnPlayerHurt(EventPlayerHurt eventInfo, GameEventInfo gameEventInfo) {
        if (eventInfo.Attacker?.UserId == null) {
            return HookResult.Continue;
        }
        if (eventInfo.Userid?.UserId == null) {
            return HookResult.Continue;
        }
        int victim = eventInfo.Userid.UserId.Value;
        int attacker = eventInfo.Attacker.UserId.Value;
        int damage = eventInfo.DmgHealth;
        int hitGroup = eventInfo.Hitgroup;

        damageGiven[attacker, victim] += damage;
        hitsGiven[attacker, victim]++;
        hitboxGiven[attacker, victim, hitGroup]++;
        hitboxGivenDamage[attacker, victim, hitGroup] += damage;

        damageTaken[victim, attacker] += damage;
        hitsTaken[victim, attacker]++;
        hitboxTaken[victim, attacker, hitGroup]++;
        hitboxTakenDamage[victim, attacker, hitGroup] += damage;

        // Debug: Print hitbox hit information
        string hitbox = hitboxName.Length > hitGroup && hitGroup >= 0 ? hitGroupToString(hitGroup) : "Unknown";
        var attackerPlayer = Utilities.GetPlayers().Find(p => p.UserId.HasValue && p.UserId.Value == attacker);
        attackerPlayer?.PrintToChat($"[DEBUG] You hit {playerName[victim]} in the {hitbox} for {damage} damage.");

        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath eventInfo, GameEventInfo gameEventInfo) {
        if (eventInfo.Attacker?.UserId == null) {
            return HookResult.Continue;
        }
        if (eventInfo.Userid?.UserId == null) {
            return HookResult.Continue;
        }
        int victim = eventInfo.Userid.UserId.Value;
        int attacker = eventInfo.Attacker.UserId.Value;
        killedPlayer[attacker, victim] = 1;

        // Show damage report for the killed player
        DisplayDamageReportsForPlayer(victim);

        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Player {victim} killed by {attacker}.");
        return HookResult.Continue;
    }

    private void DisplayDamageReportsForPlayer(int playerId) {
        var player = Utilities.GetPlayers().Find(p => p.UserId.HasValue && p.UserId.Value == playerId);
        if (player != null && (damageGiven[playerId, 0] > 0 || damageTaken[playerId, 0] > 0)) {
            player.PrintToChat($"===[ Damage Report for {playerName[playerId]} ]===");

            // Show damage given
            for (int victim = 1; victim <= MaxPlayers; victim++) {
                if (damageGiven[playerId, victim] > 0) {
                    player.PrintToChat($"To {playerName[victim]}: {hitsGiven[playerId, victim]} hits, {damageGiven[playerId, victim]} damage.");
                }
            }

            // Show damage taken
            for (int attacker = 1; attacker <= MaxPlayers; attacker++) {
                if (damageTaken[playerId, attacker] > 0) {
                    player.PrintToChat($"From {playerName[attacker]}: {hitsTaken[playerId, attacker]} hits, {damageTaken[playerId, attacker]} damage.");
                }
            }
        }
    }

    private void ClearAllDamageData() {
        Array.Clear(damageGiven, 0, damageGiven.Length);
        Array.Clear(hitsGiven, 0, hitsGiven.Length);
        Array.Clear(hitboxGiven, 0, hitboxGiven.Length);
        Array.Clear(hitboxGivenDamage, 0, hitboxGivenDamage.Length);
        Array.Clear(damageTaken, 0, damageTaken.Length);
        Array.Clear(hitsTaken, 0, hitsTaken.Length);
        Array.Clear(hitboxTaken, 0, hitboxTaken.Length);
        Array.Clear(hitboxTakenDamage, 0, hitboxTakenDamage.Length);
        Array.Clear(killedPlayer, 0, killedPlayer.Length);
        Array.Clear(showReportAgain, 0, showReportAgain.Length);
    }
}
