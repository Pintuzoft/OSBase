using System;
using System.IO;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Events;
using System.Diagnostics.Tracing;
using System.Reflection;

namespace OSBase.Modules;

using System.IO;


public class DamageReportModule : IModule {
    public string ModuleName => "DamageReportModule";
    private OSBase? osbase;
    private ConfigModule? config;

    private const int MaxPlayers = 64;
    private const int MaxHitGroups = 8;

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

        InitializeHitboxNames();
        InitializePlayerNames();

        // Register event handlers
        osbase.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);

        Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
    }

    private void InitializeHitboxNames() {
        hitboxName[0]  = "Body";
        hitboxName[1]  = "Head";
        hitboxName[2]  = "Chest";
        hitboxName[3]  = "Stomach";
        hitboxName[4]  = "L-arm";
        hitboxName[5]  = "R-arm";
        hitboxName[6]  = "L-leg";
        hitboxName[7]  = "R-leg";
        hitboxName[8]  = "Neck";
        hitboxName[9]  = "Unknown(9)";
        hitboxName[10] = "Gear";
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
        string hitbox = hitboxName.Length > hitGroup && hitGroup >= 0 ? hitboxName[hitGroup] : "Unknown";
        Server.PrintToChatAll($"[DEBUG] You hit {playerName[victim]} in the {hitbox} for {damage} damage.");

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

        Console.WriteLine($"[INFO] OSBase[{ModuleName}]: Player {victim} killed by {attacker}.");
        return HookResult.Continue;
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
