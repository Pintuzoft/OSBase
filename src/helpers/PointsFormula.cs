using System;
using System.Collections.Generic;

namespace OSBase.Helpers;

// The 2026Q4 points formula, a line-for-line port of OSWeb's PointsFormula.php (the
// reference implementation, quoted in osbase-order-2026Q4.md appendix A). The site owns the
// values (points_formula table, synced into our schema) and this class; OSBase computes the
// same numbers at kill time. Keep the two in lockstep -- acceptance check 2 in the order
// recomputes every elo_kill_event row with the PHP class and expects an exact match.
//
// DEFAULTS only cover a name missing from the table (the table is supposed to carry every
// row) and the case where the table can't be read at all -- EloRating logs loudly for both.
public sealed class PointsFormula {
    public static readonly IReadOnlyDictionary<string, double> Defaults = new Dictionary<string, double> {
        ["START_POINTS"] = 1000.0,
        ["FLOOR"] = 1.0,
        ["EVEN"] = 2.0,
        ["TOP"] = 25.0,
        ["EXP"] = 1.5,
        ["W"] = 0.2,
        ["HS_BONUS"] = 1.0,
        ["WARMUP_KILLS"] = 50.0,
        ["DEATH_SHARE"] = 0.5,
        ["MAX_KILL"] = 0.0,
        ["BONUS_PLANT"] = 2.0,
        ["BONUS_DEFUSE"] = 2.0,
        ["BONUS_BOMB_PICKUP"] = 1.0,
        ["BONUS_BOMB_DROP"] = 1.0,
        ["BONUS_ROUND_WIN"] = 1.0,
        ["BONUS_ASSIST"] = 1.0,
    };

    private readonly Dictionary<string, double> v;

    // Unknown names are dropped, missing names take the default -- same as the PHP constructor.
    public PointsFormula(IReadOnlyDictionary<string, double>? values = null) {
        v = new Dictionary<string, double>(Defaults);
        if (values == null) {
            return;
        }

        foreach (var kv in values) {
            if (v.ContainsKey(kv.Key)) {
                v[kv.Key] = kv.Value;
            }
        }
    }

    public double Value(string name) => v[name];

    public IReadOnlyDictionary<string, double> Values => v;

    public double StartPoints => v["START_POINTS"];
    public double Even => v["EVEN"];
    public int WarmupKills => (int)v["WARMUP_KILLS"];

    // Placement base: what the distance on the board is worth before rating, weapon and
    // headshot. g = (attacker place - victim place) / board size, clamped to [-1, 1].
    public double Base(int attackerPlace, int victimPlace, int boardSize) {
        double g = boardSize > 0 ? (attackerPlace - victimPlace) / (double)boardSize : 0.0;
        g = Math.Max(-1.0, Math.Min(1.0, g));
        double even = v["EVEN"];

        return g > 0
            ? even + (v["TOP"] - even) * Math.Pow(g, v["EXP"])
            : even + (even - v["FLOOR"]) * g;
    }

    // What the attacker earns. surprise = 1 - expected (rating's own number). killsBefore =
    // the attacker's kills this season before this one; under WARMUP_KILLS it's flat EVEN.
    public decimal KillPoints(int attackerPlace, int victimPlace, int boardSize, double surprise,
                              double weaponWeight, bool headshot, int killsBefore) {
        if (killsBefore < v["WARMUP_KILLS"]) {
            return Round2(v["EVEN"]);
        }

        double kill = Base(attackerPlace, victimPlace, boardSize)
            * (1 + v["W"] * (2 * surprise - 1))
            * weaponWeight;

        if (v["MAX_KILL"] > 0) {
            kill = Math.Min(kill, v["MAX_KILL"]);
        }

        return Round2(kill + (headshot ? v["HS_BONUS"] : 0.0));
    }

    // What the victim loses, as a positive number (the ledger stores it negative). Placement
    // base only -- the attacker's rating term, weapon and headshot are theirs, not the
    // victim's fault. Clipped to the victim's balance, never below 0.
    public decimal DeathLoss(int attackerPlace, int victimPlace, int boardSize,
                             int attackerKillsBefore, int victimKillsBefore, decimal victimBalance) {
        if (victimKillsBefore < v["WARMUP_KILLS"]) {
            return 0m;
        }

        double b = attackerKillsBefore < v["WARMUP_KILLS"]
            ? v["EVEN"]
            : Base(attackerPlace, victimPlace, boardSize);

        double loss = Math.Max(0.0, Math.Min(v["DEATH_SHARE"] * b, (double)victimBalance));
        return Round2(loss);
    }

    // PHP round() is half-away-from-zero, same as MidpointRounding.AwayFromZero.
    private static decimal Round2(double value) {
        return Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero);
    }
}
