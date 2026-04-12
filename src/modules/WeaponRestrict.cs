using System;
using System.Linq;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;

namespace OSBase.Modules {

    public class WeaponRestrict : IModule {
        public string ModuleName => "weaponrestrict";
        public string ModuleNameNice => "WeaponRestrict";

        private OSBase? osbase;
        private Config? config;
        private GameStats? gameStats;

        private const int TEAM_T  = (int)CsTeam.Terrorist;
        private const int TEAM_CT = (int)CsTeam.CounterTerrorist;

        private const string configFile = "weaponrestrict.cfg";

        private const string WEAPON_AWP = "weapon_awp";
        private const string WEAPON_SCAR20 = "weapon_scar20";
        private const string WEAPON_G3SG1 = "weapon_g3sg1";

        private const int PRICE_AWP = 4750;
        private const int PRICE_AUTOSNIPER = 5000;

        private bool ignoreWarmup = true;

        private readonly List<(int MinPlayers, int Limit)> awpRules = new();
        private readonly List<(int MinPlayers, int Limit)> autosniperRules = new();

        private int currentRoundEffectiveTotal = 0;
        private int currentRoundAwpLimit = 0;
        private int currentRoundAutosniperLimit = 0;

        private readonly Dictionary<int, (string WeaponName, DateTime UntilUtc)> recentNotifications = new();

        public void Load(OSBase inOsbase, Config inConfig) {
            osbase = inOsbase;
            config = inConfig;
            gameStats = osbase.GetGameStats();

            if (osbase == null || config == null || gameStats == null) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] load failed (null deps).");
                return;
            }

            config.RegisterGlobalConfigValue($"{ModuleName}", "1");
            var enabled = config.GetGlobalConfigValue($"{ModuleName}", "0");
            if (enabled != "1") {
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] disabled in config.");
                return;
            }

            LoadConfig();

            try {
                osbase.RegisterEventHandler<EventRoundPrestart>(OnRoundPrestart, HookMode.Post);
                osbase.RegisterEventHandler<EventItemPurchase>(OnItemPurchase, HookMode.Post);
                osbase.RegisterEventHandler<EventItemPickup>(OnItemPickup, HookMode.Post);
                Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] loaded.");
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] OSBase[{ModuleName}] registering: {ex.Message}");
            }
        }

        private void LoadConfig() {
            string defaultContent =
                "// WeaponRestrict config\n" +
                "weaponrestrict_ignore_warmup 1\n" +
                "\n" +
                "// Format: weaponrestrict_awp_rule <minplayers> <limit_per_team>\n" +
                "weaponrestrict_awp_rule 0 0\n" +
                "weaponrestrict_awp_rule 10 1\n" +
                "weaponrestrict_awp_rule 18 2\n" +
                "\n" +
                "// Format: weaponrestrict_autosniper_rule <minplayers> <limit_per_team>\n" +
                "weaponrestrict_autosniper_rule 0 0\n" +
                "weaponrestrict_autosniper_rule 20 1\n";

            config?.CreateCustomConfig(configFile, defaultContent);

            awpRules.Clear();
            autosniperRules.Clear();
            ignoreWarmup = true;

            var lines = config?.FetchCustomConfig(configFile) ?? new List<string>();
            foreach (var raw in lines) {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("//")) {
                    continue;
                }

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) {
                    continue;
                }

                if (parts[0].Equals("weaponrestrict_ignore_warmup", StringComparison.OrdinalIgnoreCase)) {
                    ignoreWarmup = parts[1] == "1";
                    continue;
                }

                if (parts[0].Equals("weaponrestrict_awp_rule", StringComparison.OrdinalIgnoreCase)) {
                    if (parts.Length >= 3 &&
                        int.TryParse(parts[1], out int minPlayers) &&
                        int.TryParse(parts[2], out int limit)) {
                        awpRules.Add((minPlayers, limit));
                    }
                    continue;
                }

                if (parts[0].Equals("weaponrestrict_autosniper_rule", StringComparison.OrdinalIgnoreCase)) {
                    if (parts.Length >= 3 &&
                        int.TryParse(parts[1], out int minPlayers) &&
                        int.TryParse(parts[2], out int limit)) {
                        autosniperRules.Add((minPlayers, limit));
                    }
                    continue;
                }
            }

            awpRules.Sort((a, b) => a.MinPlayers.CompareTo(b.MinPlayers));
            autosniperRules.Sort((a, b) => a.MinPlayers.CompareTo(b.MinPlayers));

            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] config loaded. ignoreWarmup={ignoreWarmup}");
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] AWP rules: {FormatRules(awpRules)}");
            Console.WriteLine($"[DEBUG] OSBase[{ModuleName}] Autosniper rules: {FormatRules(autosniperRules)}");
        }

        [GameEventHandler(HookMode.Post)]
        private HookResult OnRoundPrestart(EventRoundPrestart ev, GameEventInfo info) {
            if (gameStats == null) {
                return HookResult.Continue;
            }

            if (ignoreWarmup && gameStats.IsWarmup) {
                return HookResult.Continue;
            }

            LockRoundLimits();
            EnforceTeamLimits(TEAM_T);
            EnforceTeamLimits(TEAM_CT);

            return HookResult.Continue;
        }

        [GameEventHandler(HookMode.Post)]
        private HookResult OnItemPurchase(EventItemPurchase ev, GameEventInfo info) {
            if (gameStats == null) {
                return HookResult.Continue;
            }

            if (ignoreWarmup && gameStats.IsWarmup) {
                return HookResult.Continue;
            }

            var player = ev.Userid;
            if (player == null || !player.IsValid || !player.UserId.HasValue || player.IsHLTV) {
                return HookResult.Continue;
            }

            string weaponName = NormalizeWeaponName(ev.Weapon);
            if (!IsRestrictedWeaponName(weaponName)) {
                return HookResult.Continue;
            }

            if (!IsPurchaseAllowed(player, weaponName)) {
                int refund = GetWeaponPrice(weaponName);
                RefundPlayer(player, refund);
                NotifyRestrictedWeapon(player, weaponName, true);

                Console.WriteLine(
                    $"[DEBUG] OSBase[{ModuleName}] Purchase refunded: " +
                    $"player={player.PlayerName} uid={player.UserId.Value} weapon={weaponName} refund={refund} " +
                    $"effective_total={currentRoundEffectiveTotal} awp_limit={currentRoundAwpLimit} " +
                    $"autosniper_limit={currentRoundAutosniperLimit}"
                );
            }

            return HookResult.Continue;
        }

        [GameEventHandler(HookMode.Post)]
        private HookResult OnItemPickup(EventItemPickup ev, GameEventInfo info) {
            if (gameStats == null) {
                return HookResult.Continue;
            }

            if (ignoreWarmup && gameStats.IsWarmup) {
                return HookResult.Continue;
            }

            var player = ev.Userid;
            if (player == null || !player.IsValid || !player.UserId.HasValue || player.IsHLTV) {
                return HookResult.Continue;
            }

            string weaponName = NormalizeWeaponName(ev.Item);
            if (!IsRestrictedWeaponName(weaponName)) {
                return HookResult.Continue;
            }

            if (!IsPickupAllowed(player, weaponName)) {
                bool removed = RemoveRestrictedWeapon(player, weaponName);

                if (removed) {
                    NotifyRestrictedWeapon(player, weaponName, false);
                }

                Console.WriteLine(
                    $"[DEBUG] OSBase[{ModuleName}] Pickup blocked: " +
                    $"player={player.PlayerName} uid={player.UserId.Value} weapon={weaponName} removed={removed}"
                );
            }

            return HookResult.Continue;
        }

        private void LockRoundLimits() {
            if (gameStats == null) {
                return;
            }

            gameStats.SyncTeamsNow();

            int tCount = gameStats.getTeam(TEAM_T).numPlayers();
            int ctCount = gameStats.getTeam(TEAM_CT).numPlayers();

            currentRoundEffectiveTotal = Math.Min(tCount, ctCount) * 2;
            currentRoundAwpLimit = GetLimitForCount(awpRules, currentRoundEffectiveTotal);
            currentRoundAutosniperLimit = GetLimitForCount(autosniperRules, currentRoundEffectiveTotal);

            Console.WriteLine(
                $"[DEBUG] OSBase[{ModuleName}] Freeze lock: " +
                $"T={tCount} CT={ctCount} effective_total={currentRoundEffectiveTotal} " +
                $"awp_limit={currentRoundAwpLimit} autosniper_limit={currentRoundAutosniperLimit}"
            );
        }

        private void EnforceTeamLimits(int team) {
            var players = GetEligibleTeamPlayers(team);
            if (players.Count == 0) {
                return;
            }

            EnforceSingleWeaponLimit(team, players, WEAPON_AWP, currentRoundAwpLimit);
            EnforceAutosniperLimit(team, players, currentRoundAutosniperLimit);
        }

        private void EnforceSingleWeaponLimit(int team, List<CCSPlayerController> players, string designerName, int limit) {
            var owners = players
                .Where(p => HasWeapon(p, designerName))
                .OrderByDescending(GetPrioritySkill)
                .ThenByDescending(GetPriorityKills)
                .ThenBy(GetPriorityUserId)
                .ToList();

            if (owners.Count <= limit) {
                return;
            }

            var strip = owners.Skip(limit).ToList();

            Console.WriteLine(
                $"[DEBUG] OSBase[{ModuleName}] Freeze sweep {TeamName(team)} {designerName}: " +
                $"owners={owners.Count} limit={limit} strip={strip.Count}"
            );

            foreach (var player in strip) {
                bool removed = player.RemoveItemByDesignerName(designerName);
                Console.WriteLine(
                    $"[DEBUG] OSBase[{ModuleName}] Strip {designerName} from {player.PlayerName} " +
                    $"uid={GetPriorityUserId(player)} removed={removed}"
                );
            }
        }

        private void EnforceAutosniperLimit(int team, List<CCSPlayerController> players, int limit) {
            var owners = players
                .Where(HasAnyAutosniper)
                .OrderByDescending(GetPrioritySkill)
                .ThenByDescending(GetPriorityKills)
                .ThenBy(GetPriorityUserId)
                .ToList();

            if (owners.Count <= limit) {
                return;
            }

            var strip = owners.Skip(limit).ToList();

            Console.WriteLine(
                $"[DEBUG] OSBase[{ModuleName}] Freeze sweep {TeamName(team)} autosniper: " +
                $"owners={owners.Count} limit={limit} strip={strip.Count}"
            );

            foreach (var player in strip) {
                bool removedScar = player.RemoveItemByDesignerName(WEAPON_SCAR20);
                bool removedG3 = player.RemoveItemByDesignerName(WEAPON_G3SG1);

                Console.WriteLine(
                    $"[DEBUG] OSBase[{ModuleName}] Strip autosniper from {player.PlayerName} " +
                    $"uid={GetPriorityUserId(player)} scar20={removedScar} g3sg1={removedG3}"
                );
            }
        }

        private bool IsPurchaseAllowed(CCSPlayerController player, string weaponName) {
            int userId = player.UserId!.Value;
            int team = player.TeamNum;

            if (weaponName == WEAPON_AWP) {
                int currentOwners = CountTeamWeaponOwnersExcludingUser(team, userId, WEAPON_AWP);
                return currentOwners < currentRoundAwpLimit;
            }

            if (IsAutosniperWeaponName(weaponName)) {
                int currentOwners = CountTeamAutosniperOwnersExcludingUser(team, userId);
                return currentOwners < currentRoundAutosniperLimit;
            }

            return true;
        }

        private bool IsPickupAllowed(CCSPlayerController player, string weaponName) {
            int userId = player.UserId!.Value;
            int team = player.TeamNum;

            if (weaponName == WEAPON_AWP) {
                int currentOwners = CountTeamWeaponOwnersExcludingUser(team, userId, WEAPON_AWP);
                return currentOwners < currentRoundAwpLimit;
            }

            if (IsAutosniperWeaponName(weaponName)) {
                int currentOwners = CountTeamAutosniperOwnersExcludingUser(team, userId);
                return currentOwners < currentRoundAutosniperLimit;
            }

            return true;
        }

        private int CountTeamWeaponOwnersExcludingUser(int team, int excludedUserId, string designerName) {
            return GetEligibleTeamPlayers(team)
                .Where(p => p.UserId!.Value != excludedUserId)
                .Count(p => HasWeapon(p, designerName));
        }

        private int CountTeamAutosniperOwnersExcludingUser(int team, int excludedUserId) {
            return GetEligibleTeamPlayers(team)
                .Where(p => p.UserId!.Value != excludedUserId)
                .Count(HasAnyAutosniper);
        }

        private List<CCSPlayerController> GetEligibleTeamPlayers(int team) {
            return Utilities.GetPlayers()
                .Where(p =>
                    p != null &&
                    p.IsValid &&
                    p.UserId.HasValue &&
                    !p.IsHLTV &&
                    p.TeamNum == team)
                .ToList();
        }

        private bool HasAnyAutosniper(CCSPlayerController player) {
            return HasWeapon(player, WEAPON_SCAR20) || HasWeapon(player, WEAPON_G3SG1);
        }

        private bool HasWeapon(CCSPlayerController player, string designerName) {
            if (player == null || !player.IsValid) {
                return false;
            }

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) {
                return false;
            }

            foreach (var weaponHandle in pawn.WeaponServices.MyWeapons) {
                var weapon = weaponHandle.Value;
                if (weapon == null || !weapon.IsValid) {
                    continue;
                }

                if (string.Equals(weapon.DesignerName, designerName, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }

            return false;
        }

        private bool RemoveRestrictedWeapon(CCSPlayerController player, string weaponName) {
            if (weaponName == WEAPON_AWP) {
                return player.RemoveItemByDesignerName(WEAPON_AWP);
            }

            if (weaponName == WEAPON_SCAR20) {
                return player.RemoveItemByDesignerName(WEAPON_SCAR20);
            }

            if (weaponName == WEAPON_G3SG1) {
                return player.RemoveItemByDesignerName(WEAPON_G3SG1);
            }

            return false;
        }

        private int GetWeaponPrice(string weaponName) {
            return weaponName switch {
                WEAPON_AWP => PRICE_AWP,
                WEAPON_SCAR20 => PRICE_AUTOSNIPER,
                WEAPON_G3SG1 => PRICE_AUTOSNIPER,
                _ => 0
            };
        }

        private void RefundPlayer(CCSPlayerController player, int amount) {
            if (amount <= 0 || player == null || !player.IsValid) {
                return;
            }

            if (player.InGameMoneyServices == null) {
                return;
            }

            player.InGameMoneyServices.Account += amount;
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
        }

        private void NotifyRestrictedWeapon(CCSPlayerController player, string weaponName, bool refunded) {
            if (player == null || !player.IsValid || !player.UserId.HasValue) {
                return;
            }

            int userId = player.UserId.Value;
            var now = DateTime.UtcNow;

            if (recentNotifications.TryGetValue(userId, out var recent)) {
                if (recent.WeaponName == weaponName && recent.UntilUtc > now) {
                    return;
                }
            }

            recentNotifications[userId] = (weaponName, now.AddMilliseconds(1200));

            string pretty = PrettyWeaponName(weaponName);
            string message = refunded
                ? $" \x08[WeaponRestrict]\x01 {pretty} is not allowed this round. Money refunded."
                : $" \x08[WeaponRestrict]\x01 {pretty} is not allowed this round.";

            player.PrintToChat(message);
        }

        private string PrettyWeaponName(string weaponName) {
            return weaponName switch {
                WEAPON_AWP => "AWP",
                WEAPON_SCAR20 => "SCAR-20",
                WEAPON_G3SG1 => "G3SG1",
                _ => weaponName
            };
        }

        private float GetPrioritySkill(CCSPlayerController player) {
            return TeamBalancer.Current?.GetEffectiveSkillForPriority(player) ?? 0f;
        }

        private int GetPriorityKills(CCSPlayerController player) {
            if (gameStats == null || player == null || !player.UserId.HasValue) {
                return 0;
            }

            return gameStats.GetPlayerStats(player.UserId.Value).kills;
        }

        private int GetPriorityUserId(CCSPlayerController player) {
            if (player == null || !player.UserId.HasValue) {
                return int.MaxValue;
            }

            return player.UserId.Value;
        }

        private int GetLimitForCount(List<(int MinPlayers, int Limit)> rules, int count) {
            int limit = 0;

            foreach (var rule in rules) {
                if (count >= rule.MinPlayers) {
                    limit = rule.Limit;
                }
            }

            return limit;
        }

        private bool IsRestrictedWeaponName(string weaponName) {
            return weaponName == WEAPON_AWP || IsAutosniperWeaponName(weaponName);
        }

        private bool IsAutosniperWeaponName(string weaponName) {
            return weaponName == WEAPON_SCAR20 || weaponName == WEAPON_G3SG1;
        }

        private string NormalizeWeaponName(string weaponName) {
            if (string.IsNullOrWhiteSpace(weaponName)) {
                return string.Empty;
            }

            string normalized = weaponName.Trim().ToLowerInvariant();
            if (!normalized.StartsWith("weapon_", StringComparison.Ordinal)) {
                normalized = "weapon_" + normalized;
            }

            return normalized;
        }

        private string FormatRules(List<(int MinPlayers, int Limit)> rules) {
            if (rules.Count == 0) {
                return "(none)";
            }

            return string.Join(", ", rules.Select(r => $"{r.MinPlayers}->{r.Limit}"));
        }

        private string TeamName(int team) {
            return team == TEAM_T ? "T" : team == TEAM_CT ? "CT" : "SPEC";
        }

        public int GetCurrentRoundEffectiveTotal() {
            return currentRoundEffectiveTotal;
        }

        public int GetCurrentRoundAwpLimit() {
            return currentRoundAwpLimit;
        }

        public int GetCurrentRoundAutosniperLimit() {
            return currentRoundAutosniperLimit;
        }
    }
}