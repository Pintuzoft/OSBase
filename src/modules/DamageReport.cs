using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Events;

namespace OSBase.Modules {

public class DamageReport : IModule {
    public string ModuleName => "damagereport";

    private OSBase? osbase;
    private Config? config;

    private const int ENVIRONMENT = -1;
    private const float DELAY_SECONDS = 3.0f;

    // 0..10 (klassiska). Allt annat -> Uxx(xx)
    private readonly string[] hitboxName = {
        "Generic", "Head", "Chest", "Stomach", "L-Arm", "R-Arm", "L-Leg", "R-Leg", "Neck", "U9", "Gear"
    };

    private readonly Dictionary<int, HashSet<int>> killedPlayer = new Dictionary<int, HashSet<int>>();

    private readonly Dictionary<int, Dictionary<int, int>> damageGiven = new Dictionary<int, Dictionary<int, int>>();
    private readonly Dictionary<int, Dictionary<int, int>> damageTaken = new Dictionary<int, Dictionary<int, int>>();
    private readonly Dictionary<int, Dictionary<int, int>> hitsGiven = new Dictionary<int, Dictionary<int, int>>();
    private readonly Dictionary<int, Dictionary<int, int>> hitsTaken = new Dictionary<int, Dictionary<int, int>>();

    private readonly Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitboxGiven = new Dictionary<int, Dictionary<int, Dictionary<int, int>>>();
    private readonly Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitboxTaken = new Dictionary<int, Dictionary<int, Dictionary<int, int>>>();
    private readonly Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitboxGivenDamage = new Dictionary<int, Dictionary<int, Dictionary<int, int>>>();
    private readonly Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitboxTakenDamage = new Dictionary<int, Dictionary<int, Dictionary<int, int>>>();

    private readonly Dictionary<int, string> playerNames = new Dictionary<int, string>();
    private readonly HashSet<int> reportedPlayers = new HashSet<int>();

    public void Load(OSBase inOsbase, Config inConfig) {
        osbase = inOsbase;
        config = inConfig;

        config.RegisterGlobalConfigValue($"{ModuleName}", "1");

        if (osbase == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] osbase is null. {ModuleName} failed to load.");
            return;
        }

        if (config == null) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] config is null. {ModuleName} failed to load.");
            return;
        }

        if (config.GetGlobalConfigValue($"{ModuleName}", "0") == "1") {
            LoadEventHandlers();
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded successfully!");
        } else {
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] {ModuleName} is disabled in the global configuration.");
        }
    }

    private void LoadEventHandlers() {
        if (osbase == null) return;

        osbase.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        osbase.RegisterEventHandler<EventPlayerConnect>(OnPlayerConnect);
        osbase.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnectEvent);
    }

    private HookResult OnPlayerHurt(EventPlayerHurt e, GameEventInfo _) {
        try {
            if (e.Userid == null || e.Attacker == null) return HookResult.Continue;
            if (!e.Userid.UserId.HasValue) return HookResult.Continue;
            if (e.DmgHealth <= 0) return HookResult.Continue;

            int victim = e.Userid.UserId.Value;
            int attacker = e.Attacker.UserId ?? ENVIRONMENT;

            if (attacker == victim && e.Weapon == "world") {
                attacker = ENVIRONMENT;
            }

            int damage = e.DmgHealth;

            // Stabil hitgroup: prefer wrapper if sane, else read byte via reflection.
            int hitgroup = ReadHitgroupByteCompat(e); // 0..255

            Ensure2(damageGiven, attacker);
            Ensure2(damageTaken, victim);
            Ensure2(hitsGiven, attacker);
            Ensure2(hitsTaken, victim);

            Ensure3(hitboxGiven, attacker, victim);
            Ensure3(hitboxTaken, victim, attacker);
            Ensure3(hitboxGivenDamage, attacker, victim);
            Ensure3(hitboxTakenDamage, victim, attacker);

            damageGiven[attacker][victim] = damageGiven[attacker].GetValueOrDefault(victim, 0) + damage;
            damageTaken[victim][attacker] = damageTaken[victim].GetValueOrDefault(attacker, 0) + damage;

            hitsGiven[attacker][victim] = hitsGiven[attacker].GetValueOrDefault(victim, 0) + 1;
            hitsTaken[victim][attacker] = hitsTaken[victim].GetValueOrDefault(attacker, 0) + 1;

            hitboxGiven[attacker][victim][hitgroup] = hitboxGiven[attacker][victim].GetValueOrDefault(hitgroup, 0) + 1;
            hitboxTaken[victim][attacker][hitgroup] = hitboxTaken[victim][attacker].GetValueOrDefault(hitgroup, 0) + 1;

            hitboxGivenDamage[attacker][victim][hitgroup] = hitboxGivenDamage[attacker][victim].GetValueOrDefault(hitgroup, 0) + damage;
            hitboxTakenDamage[victim][attacker][hitgroup] = hitboxTakenDamage[victim][attacker].GetValueOrDefault(hitgroup, 0) + damage;

            return HookResult.Continue;
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception in OnPlayerHurt: {ex}");
            return HookResult.Continue;
        }
    }

    private HookResult OnPlayerDeath(EventPlayerDeath e, GameEventInfo _) {
        int victimId = e.Userid?.UserId ?? -1;
        int attackerId = e.Attacker?.UserId ?? -1;

        if (attackerId >= 0 && victimId >= 0) {
            EnsureKillSet(attackerId).Add(victimId);
        }

        if (victimId >= 0) {
            ScheduleDamageReport(victimId);
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart _, GameEventInfo __) {
        ClearDamageData();
        UpdatePlayerNames();
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd _, GameEventInfo __) {
        foreach (var p in Utilities.GetPlayers()) {
            if (p == null || !p.IsValid || p.IsHLTV || !p.UserId.HasValue) continue;
            ScheduleDamageReport(p.UserId.Value);
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnect(EventPlayerConnect _, GameEventInfo __) {
        UpdatePlayerNames();
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnectEvent(EventPlayerDisconnect e, GameEventInfo _) {
        if (e.Userid?.UserId != null) {
            OnPlayerDisconnect(e.Userid.UserId.Value);
        }
        return HookResult.Continue;
    }

    private void UpdatePlayerNames() {
        try {
            foreach (var p in Utilities.GetPlayers()) {
                if (p == null || !p.UserId.HasValue) continue;
                int id = p.UserId.Value;
                playerNames[id] = string.IsNullOrEmpty(p.PlayerName) ? "Bot" : p.PlayerName;
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception in UpdatePlayerNames: {ex}");
        }
    }

    private void ScheduleDamageReport(int userId) {
        if (osbase == null) return;
        if (!reportedPlayers.Add(userId)) return;

        osbase.AddTimer(DELAY_SECONDS, () => {
            try {
                CCSPlayerController? p = FindPlayerByUserId(userId);
                if (p == null || !p.IsValid || p.IsHLTV || !p.UserId.HasValue) return;
                DisplayDamageReport(p);
            } finally {
                reportedPlayers.Remove(userId);
            }
        });
    }

    private CCSPlayerController? FindPlayerByUserId(int userId) {
        foreach (var p in Utilities.GetPlayers()) {
            if (p == null || !p.IsValid || !p.UserId.HasValue) continue;
            if (p.UserId.Value == userId) return p;
        }
        return null;
    }

    private void DisplayDamageReport(CCSPlayerController player) {
        if (player == null || !player.IsValid || !player.UserId.HasValue) return;

        int playerId = player.UserId.Value;

        bool hasVictimData = damageGiven.ContainsKey(playerId) && damageGiven[playerId].Count > 0;
        bool hasAttackerData = damageTaken.ContainsKey(playerId) && damageTaken[playerId].Count > 0;

        if (!hasVictimData && !hasAttackerData) return;

        player.PrintToChat("===[ Damage Report (hits:damage) ]===");

        if (hasVictimData) {
            player.PrintToChat("Victims:");
            foreach (var v in damageGiven[playerId]) {
                int victimId = v.Key;
                int dmg = v.Value;
                int hits = hitsGiven[playerId].GetValueOrDefault(victimId, 0);

                string victimName = playerNames.GetValueOrDefault(victimId, "Unknown");
                string killedText = (killedPlayer.ContainsKey(playerId) && killedPlayer[playerId].Contains(victimId)) ? " (Killed)" : "";

                string hitInfo = BuildHitInfo(hitboxGiven, hitboxGivenDamage, playerId, victimId, dmg);
                player.PrintToChat($"- {victimName}{killedText}: {hits} hits, {dmg} damage{hitInfo}");
            }
        }

        if (hasAttackerData) {
            player.PrintToChat("Attackers:");
            foreach (var a in damageTaken[playerId]) {
                int attackerId = a.Key;
                int dmg = a.Value;
                int hits = hitsTaken[playerId].GetValueOrDefault(attackerId, 0);

                string attackerName = playerNames.GetValueOrDefault(attackerId, "Unknown");
                string killedByText = (killedPlayer.ContainsKey(attackerId) && killedPlayer[attackerId].Contains(playerId)) ? " (Killed by)" : "";

                string hitInfo = BuildHitInfo(hitboxTaken, hitboxTakenDamage, playerId, attackerId, dmg);
                player.PrintToChat($"- {attackerName}{killedByText}: {hits} hits, {dmg} damage{hitInfo}");
            }
        }
    }

    private string BuildHitInfo(Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitCounts, Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitDamages, int a, int b, int totalDamage) {
        if (!hitCounts.ContainsKey(a)) return "";
        if (!hitCounts[a].ContainsKey(b)) return "";

        int calc = 0;
        var parts = new List<string>();

        foreach (var kv in hitCounts[a][b]) {
            int hg = kv.Key;
            int count = kv.Value;

            int dmg = 0;
            if (hitDamages.ContainsKey(a) && hitDamages[a].ContainsKey(b)) {
                dmg = hitDamages[a][b].GetValueOrDefault(hg, 0);
            }

            calc += dmg;
            parts.Add($"{GetHitgroupLabel(hg)} {count}:{dmg}");
        }

        string s = " [" + string.Join(", ", parts) + "]";
        if (calc != totalDamage) s += $" [Inconsistent: {totalDamage} != {calc}]";

        return s;
    }

    private string GetHitgroupLabel(int hgByte) {
        if (hgByte >= 0 && hgByte < hitboxName.Length) {
            return hitboxName[hgByte];
        }
        return $"U{hgByte}({hgByte})";
    }

    private void ClearDamageData() {
        damageGiven.Clear();
        damageTaken.Clear();
        hitsGiven.Clear();
        hitsTaken.Clear();
        killedPlayer.Clear();
        reportedPlayers.Clear();

        hitboxGiven.Clear();
        hitboxTaken.Clear();
        hitboxGivenDamage.Clear();
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
        hitboxGivenDamage.Remove(playerId);
        hitboxTakenDamage.Remove(playerId);

        killedPlayer.Remove(playerId);
        playerNames.Remove(playerId);
        reportedPlayers.Remove(playerId);
    }

    private HashSet<int> EnsureKillSet(int attackerId) {
        if (!killedPlayer.ContainsKey(attackerId)) {
            killedPlayer[attackerId] = new HashSet<int>();
        }
        return killedPlayer[attackerId];
    }

    private static void Ensure2(Dictionary<int, Dictionary<int, int>> map, int a) {
        if (!map.ContainsKey(a)) {
            map[a] = new Dictionary<int, int>();
        }
    }

    private static void Ensure3(Dictionary<int, Dictionary<int, Dictionary<int, int>>> map, int a, int b) {
        if (!map.ContainsKey(a)) {
            map[a] = new Dictionary<int, Dictionary<int, int>>();
        }
        if (!map[a].ContainsKey(b)) {
            map[a][b] = new Dictionary<int, int>();
        }
    }

    private static int ReadHitgroupByteCompat(EventPlayerHurt e) {
        int hg;
        try {
            hg = e.Hitgroup;
        } catch {
            hg = 0;
        }

        // If sane, accept.
        if (hg >= 0 && hg <= 255) return hg;

        // Otherwise read byte via protected GameEvent.Get<byte>("hitgroup") using reflection.
        return TryGetGameEventByte(e, "hitgroup");
    }

    private static byte TryGetGameEventByte(GameEvent ev, string key) {
        try {
            MethodInfo? mi = typeof(GameEvent).GetMethod("Get", BindingFlags.Instance | BindingFlags.NonPublic);
            if (mi == null) return 0;

            MethodInfo g = mi.MakeGenericMethod(typeof(byte));
            object? val = g.Invoke(ev, new object[] { key });

            if (val is byte b) return b;
            if (val != null) return Convert.ToByte(val, CultureInfo.InvariantCulture);

            return 0;
        } catch {
            return 0;
        }
    }
}

}