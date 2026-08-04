namespace OSBase.Helpers;

// Extracted 2026-08-04 (agent-chat #33): DamageReport.cs, EloRating.cs, and TeamBets.cs each
// carried their own byte-for-byte identical copy of this. They'd never actually drifted, but
// three independent copies meant "the live path" computing a season key was three live paths
// that happened to agree, not one -- exactly the kind of divergence risk OSWeb flagged while
// scoping the demo backfill (a parser has to reuse this exact logic, and "exact" only means
// something if there's one definition to reuse). Every table with season in its primary key
// (player_hit_stat, player_weapon_shots, player_round_stat, player_duel_stat,
// player_clutch_stat, player_multikill_stat, player_teambet_stat, player_duel_total,
// elo_points) depends on this being consistent across every writer.
public static class SeasonHelper {
    public static string CurrentSeason() {
        System.DateTime now = System.DateTime.UtcNow;
        int quarter = ((now.Month - 1) / 3) + 1;
        return $"{now.Year}Q{quarter}";
    }
}
