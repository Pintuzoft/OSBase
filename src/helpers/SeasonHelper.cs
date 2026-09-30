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

    // "2026Q4" -> "2026Q3", "2026Q1" -> "2025Q4". Null for anything that isn't a season key.
    // Used by EloRating's balancing read (osbase-order-2026Q4.md section 1: the LAN balancer
    // reads last quarter's final rating until the player clears the provisional gate).
    public static string? PreviousSeason(string season) {
        if (!TryParse(season, out int year, out int quarter)) {
            return null;
        }

        return quarter == 1 ? $"{year - 1}Q4" : $"{year}Q{quarter - 1}";
    }

    public static bool TryParse(string season, out int year, out int quarter) {
        year = 0;
        quarter = 0;
        int qIdx = season?.IndexOf('Q') ?? -1;
        if (qIdx <= 0 || !int.TryParse(season!.AsSpan(0, qIdx), out year) || !int.TryParse(season.AsSpan(qIdx + 1), out quarter)) {
            return false;
        }

        return quarter >= 1 && quarter <= 4;
    }

    // UTC calendar range of a season, inclusive start, exclusive end.
    public static (System.DateTime Start, System.DateTime End) Range(string season) {
        if (!TryParse(season, out int year, out int quarter)) {
            System.DateTime now = System.DateTime.UtcNow.Date;
            return (now, now);
        }

        var start = new System.DateTime(year, ((quarter - 1) * 3) + 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
        return (start, start.AddMonths(3));
    }
}
