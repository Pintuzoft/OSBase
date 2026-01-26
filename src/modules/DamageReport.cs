using System;
using System.Collections.Generic;
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

    // Accept only classic hitgroups for hitbox stats (protects against schema/offset breakage).
    private const int MIN_HITGROUP = 0;
    private const int MAX_HITGROUP = 11;

    // Hard logging (rate-limited to avoid log spam storms)
    private DateTime _lastHurtLog = DateTime.MinValue;
    private const int HURT_LOG_INTERVAL_MS = 250;

    private readonly Dictionary<int, HashSet<int>> killedPlayer = new();
    private readonly Dictionary<int, Dictionary<int, int>> damageGiven = new();
    private readonly Dictionary<int, Dictionary<int, int>> damageTaken = new();
    private readonly Dictionary<int, Dictionary<int, int>> hitsGiven = new();
    private readonly Dictionary<int, Dictionary<int, int>> hitsTaken = new();

    private readonly Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitboxGiven = new();
    private readonly Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitboxTaken = new();
    private readonly Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitboxGivenDamage = new();
    private readonly Dictionary<int, Dictionary<int, Dictionary<int, int>>> hitboxTakenDamage = new();

    private readonly Dictionary<int, string> playerNames = new();
    private readonly HashSet<int> reportedPlayers = new();

    // 0..255 hitgroup labels; unknown -> U{n}
    private static readonly string[] HitgroupLabel = BuildHitgroupLabels();

    private static string[] BuildHitgroupLabels() {
        var a = new string[256];
        for (int i = 0; i < a.Length; i++) {
            a[i] = $"U{i}";
        }

        // Common Source/CS hitgroups
        a[0]  = "Generic";
        a[1]  = "Head";
        a[2]  = "Chest";
        a[3]  = "Stomach";
        a[4]  = "L-Arm";
        a[5]  = "R-Arm";
        a[6]  = "L-Leg";
        a[7]  = "R-Leg";
        a[8]  = "Neck";
        a[10] = "Gear";

        return a;
    }

    private static string HG(int hitgroup) {
        return HitgroupLabel[(byte)hitgroup];
    }

    private static bool IsValidHitgroup(int hitgroup) {
        return hitgroup >= MIN_HITGROUP && hitgroup <= MAX_HITGROUP;
    }

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
        if (osbase == null) {
            return;
        }

        osbase.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        osbase.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        osbase.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        osbase.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        osbase.RegisterEventHandler<EventPlayerConnect>(OnPlayerConnect);
        osbase.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnectEvent);
    }

    private HookResult OnPlayerHurt(EventPlayerHurt e, GameEventInfo _) {
        try {
            if (e.Attacker == null || e.Userid == null) {
                return HookResult.Continue;
            }

            if (e.DmgHealth <= 0) {
                return HookResult.Continue;
            }

            int attackerId = e.Attacker.UserId ?? ENVIRONMENT;
            int victimId = e.Userid.UserId ?? -1;
            if (victimId < 0) {
                return HookResult.Continue;
            }

            if (attackerId == victimId && e.Weapon == "world") {
                attackerId = ENVIRONMENT;
            }

            int damage = e.DmgHealth;
            int hitgroup = e.Hitgroup;

            // Hard log (rate-limited)
            var now = DateTime.UtcNow;
            if ((now - _lastHurtLog).TotalMilliseconds >= HURT_LOG_INTERVAL_MS) {
                _lastHurtLog = now;
                Console.WriteLine($"[DR] a={attackerId} v={victimId} dmgH={damage} dmgA={e.DmgArmor} hg={hitgroup}({HG(hitgroup)}) hp={e.Health} armor={e.Armor} weapon={e.Weapon}");
            }

            var dG = GetOrCreate(damageGiven, attackerId);
            var dT = GetOrCreate(damageTaken, victimId);
            var hG = GetOrCreate(hitsGiven, attackerId);
            var hT = GetOrCreate(hitsTaken, victimId);

            dG[victimId] = dG.GetValueOrDefault(victimId, 0) + damage;
            dT[attackerId] = dT.GetValueOrDefault(attackerId, 0) + damage;

            hG[victimId] = hG.GetValueOrDefault(victimId, 0) + 1;
            hT[attackerId] = hT.GetValueOrDefault(attackerId, 0) + 1;

            // Hitbox stats only if hitgroup is plausible (protects against schema mismatch)
            if (IsValidHitgroup(hitgroup)) {
                var hbG = GetOrCreateNested(hitboxGiven, attackerId, victimId);
                var hbT = GetOrCreateNested(hitboxTaken, victimId, attackerId);
                hbG[hitgroup] = hbG.GetValueOrDefault(hitgroup, 0) + 1;
                hbT[hitgroup] = hbT.GetValueOrDefault(hitgroup, 0) + 1;

                var hbGD = GetOrCreateNested(hitboxGivenDamage, attackerId, victimId);
                var hbTD = GetOrCreateNested(hitboxTakenDamage, victimId, attackerId);
                hbGD[hitgroup] = hbGD.GetValueOrDefault(hitgroup, 0) + damage;
                hbTD[hitgroup] = hbTD.GetValueOrDefault(hitgroup, 0) + damage;
            }

            return HookResult.Continue;
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception in OnPlayerHurt: {ex.Message}\n{ex.StackTrace}");
            return HookResult.Continue;
        }
    }

    private HookResult OnPlayerDeath(EventPlayerDeath e, GameEventInfo _) {
        int victimId = e.Userid?.UserId ?? -1;
        int attackerId = e.Attacker?.UserId ?? -1;

        if (attackerId >= 0 && victimId >= 0) {
            GetOrCreate(killedPlayer, attackerId).Add(victimId);
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
            if (IsReportablePlayer(p) && p!.UserId.HasValue) {
                ScheduleDamageReport(p.UserId.Value);
            }
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnect(EventPlayerConnect _, GameEventInfo __) {
        UpdatePlayerNames();
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnectEvent(EventPlayerDisconnect e, GameEventInfo _) {
        if (e.Userid?.UserId is int id) {
            OnPlayerDisconnect(id);
        }
        return HookResult.Continue;
    }

    private void UpdatePlayerNames() {
        try {
            foreach (var p in Utilities.GetPlayers()) {
                if (p == null || !p.UserId.HasValue) {
                    continue;
                }

                int id = p.UserId.Value;
                playerNames[id] = string.IsNullOrWhiteSpace(p.PlayerName) ? "Bot" : p.PlayerName;
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] OSBase[{ModuleName}] Exception in UpdatePlayerNames: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void ScheduleDamageReport(int userId) {
        if (osbase == null) {
            return;
        }
        if (userId < 0) {
            return;
        }

        if (!reportedPlayers.Add(userId)) {
            return;
        }

        osbase.AddTimer(DELAY_SECONDS, () => {
            try {
                DisplayDamageReport(userId);
            } finally {
                reportedPlayers.Remove(userId);
            }
        });
    }

    private void DisplayDamageReport(int userId) {
        var player = FindPlayerByUserId(userId);
        if (!IsReportablePlayer(player)) {
            return;
        }

        bool hasVictimData = damageGiven.TryGetValue(userId, out var victims) && victims.Count > 0;
        bool hasAttackerData = damageTaken.TryGetValue(userId, out var attackers) && attackers.Count > 0;

        if (!hasVictimData && !hasAttackerData) {
            return;
        }

        player!.PrintToChat("===[ Damage Report (hits:damage) ]===");

        if (hasVictimData && victims != null) {
            player.PrintToChat("Victims:");
            foreach (var v in victims) {
                int victimId = v.Key;
                int dmg = v.Value;

                int hits = 0;
                if (hitsGiven.TryGetValue(userId, out var hv)) {
                    hits = hv.GetValueOrDefault(victimId, 0);
                }

                string victimName = playerNames.GetValueOrDefault(victimId, "Unknown");
                string killedText = (killedPlayer.TryGetValue(userId, out var ks) && ks.Contains(victimId)) ? " (Killed)" : "";

                string hitInfo = BuildHitInfoGiven(userId, victimId, dmg);
                player.PrintToChat($" - {victimName}{killedText}: {hits} hits, {dmg} damage{hitInfo}");
            }
        }

        if (hasAttackerData && attackers != null) {
            player.PrintToChat("Attackers:");
            foreach (var a in attackers) {
                int attackerId = a.Key;
                int dmg = a.Value;

                int hits = 0;
                if (hitsTaken.TryGetValue(userId, out var ha)) {
                    hits = ha.GetValueOrDefault(attackerId, 0);
                }

                string attackerName = playerNames.GetValueOrDefault(attackerId, "Unknown");
                string killedByText = (killedPlayer.TryGetValue(attackerId, out var ks) && ks.Contains(userId)) ? " (Killed by)" : "";

                string hitInfo = BuildHitInfoTaken(userId, attackerId, dmg);
                player.PrintToChat($" - {attackerName}{killedByText}: {hits} hits, {dmg} damage{hitInfo}");
            }
        }
    }

    private string BuildHitInfoGiven(int attackerId, int victimId, int totalDamage) {
        if (!hitboxGiven.TryGetValue(attackerId, out var byVictim)) {
            return "";
        }
        if (!byVictim.TryGetValue(victimId, out var byGroup) || byGroup.Count == 0) {
            return "";
        }

        Dictionary<int, int>? dmgByGroup = null;
        if (hitboxGivenDamage.TryGetValue(attackerId, out var dmgByVictim)) {
            dmgByVictim.TryGetValue(victimId, out dmgByGroup);
        }

        int calc = 0;
        string s = " [";

        foreach (var hg in byGroup) {
            int hitgroup = hg.Key;
            int hitCount = hg.Value;
            int hgDmg = dmgByGroup?.GetValueOrDefault(hitgroup, 0) ?? 0;

            calc += hgDmg;
            s += $"{HG(hitgroup)}({(byte)hitgroup}) {hitCount}:{hgDmg}, ";
        }

        s = s.TrimEnd(' ', ',') + "]";

        if (totalDamage != calc) {
            s += $" [Inconsistent: {totalDamage} != {calc}]";
        }

        return s;
    }

    private string BuildHitInfoTaken(int victimId, int attackerId, int totalDamage) {
        if (!hitboxTaken.TryGetValue(victimId, out var byAttacker)) {
            return "";
        }
        if (!byAttacker.TryGetValue(attackerId, out var byGroup) || byGroup.Count == 0) {
            return "";
        }

        Dictionary<int, int>? dmgByGroup = null;
        if (hitboxTakenDamage.TryGetValue(victimId, out var dmgByVictim)) {
            dmgByVictim.TryGetValue(attackerId, out dmgByGroup);
        }

        int calc = 0;
        string s = " [";

        foreach (var hg in byGroup) {
            int hitgroup = hg.Key;
            int hitCount = hg.Value;
            int hgDmg = dmgByGroup?.GetValueOrDefault(hitgroup, 0) ?? 0;

            calc += hgDmg;
            s += $"{HG(hitgroup)}({(byte)hitgroup}) {hitCount}:{hgDmg}, ";
        }

        s = s.TrimEnd(' ', ',') + "]";

        if (totalDamage != calc) {
            s += $" [Inconsistent: {totalDamage} != {calc}]";
        }

        return s;
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

    private static bool IsReportablePlayer(CCSPlayerController? p) {
        return p != null && p.IsValid && !p.IsHLTV && p.UserId.HasValue;
    }

    private static CCSPlayerController? FindPlayerByUserId(int userId) {
        foreach (var p in Utilities.GetPlayers()) {
            if (p != null && p.IsValid && p.UserId.HasValue && p.UserId.Value == userId) {
                return p;
            }
        }
        return null;
    }

    private static Dictionary<int, int> GetOrCreate(Dictionary<int, Dictionary<int, int>> dict, int key) {
        if (!dict.TryGetValue(key, out var inner)) {
            inner = new Dictionary<int, int>();
            dict[key] = inner;
        }
        return inner;
    }

    private static HashSet<int> GetOrCreate(Dictionary<int, HashSet<int>> dict, int key) {
        if (!dict.TryGetValue(key, out var set)) {
            set = new HashSet<int>();
            dict[key] = set;
        }
        return set;
    }

    private static Dictionary<int, int> GetOrCreateNested(Dictionary<int, Dictionary<int, Dictionary<int, int>>> dict, int outerKey, int innerKey) {
        if (!dict.TryGetValue(outerKey, out var innerDict)) {
            innerDict = new Dictionary<int, Dictionary<int, int>>();
            dict[outerKey] = innerDict;
        }
        if (!innerDict.TryGetValue(innerKey, out var leaf)) {
            leaf = new Dictionary<int, int>();
            innerDict[innerKey] = leaf;
        }
        return leaf;
    }
}

}