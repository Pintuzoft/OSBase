using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using OSBase.Modules;

namespace OSBase.Helpers {
    // Two independent skill signals, selected by TeamBalancer's balancer_skill_source config
    // (gamestats / elo / shadow -- see ELO-MODULE.md). Both paths stay in this file for as
    // long as the phased cutover needs them: GameStats.calcSkill()/skill_log is not being
    // retired on the same day EloRating starts scoring -- skill_log keeps writing regardless
    // (SaveIfEligible in GameStats.cs is self-contained, triggered by its own round/map-end
    // hooks, not by anything reading calcSkill() -- verified, not assumed), and the
    // form-curve/site history has to keep a continuous, unbroken series through the
    // transition. The GameStats path below is the original blend, unchanged.
    public static class SkillResolver {
        // ----- GameStats path (original, default at release) -----

        private const float OUTLIER_PCT = 0.50f;
        private const float OUTLIER_ABS = 4000f;
        private const float LATE_ABS_CLAMP = 2500f;
        private const float LATE_PCT_CLAMP = 0.35f;

        private const int PROV_MIN = 5000;
        private const int PROV_MAX = 7000;

        public static float GetEffectiveSkillForPlayer(GameStats? gs, CCSPlayerController? player) {
            if (gs == null || player == null || !player.IsValid || !player.UserId.HasValue) {
                return 0f;
            }

            return GetEffectiveSkill(gs, player.UserId.Value);
        }

        public static float GetEffectiveSkill(GameStats? gs, int userId) {
            if (gs == null || userId <= 0) {
                return 0f;
            }

            var ps = gs.GetPlayerStats(userId);
            return GetEffectiveSkill(gs, userId, ps);
        }

        public static float GetEffectiveSkill(GameStats? gs, int userId, PlayerStats? ps) {
            if (gs == null || userId <= 0 || ps == null) {
                return 0f;
            }

            if (gs.roundNumber == 0) {
                return GetWarmupSignal(gs, userId, ps);
            }

            int round = gs.roundNumber;
            int playerRounds = ps.rounds;

            float baseline = GetBaselineSkill(gs, userId, ps);
            float live = ps.calcSkill();

            if (playerRounds <= 0 || round <= 0) {
                return baseline;
            }

            float diff = Math.Abs(live - baseline);
            float maxDelta1 = OUTLIER_ABS;
            float maxDelta2 = OUTLIER_PCT * Math.Max(1f, baseline);
            float maxDelta = Math.Max(maxDelta1, maxDelta2);

            if (diff > maxDelta) {
                float delta = live - baseline;
                delta = Math.Clamp(delta, -maxDelta, maxDelta);
                live = baseline + delta;
            }

            if (round >= 16) {
                float lateBand = Math.Max(LATE_ABS_CLAMP, LATE_PCT_CLAMP * Math.Max(1f, baseline));
                float upper = baseline + lateBand;
                float lower = baseline - lateBand;
                live = Math.Clamp(live, lower, upper);
            }

            const float LIVE_PER_ROUND = 0.15f;
            const float MAX_LIVE_WEIGHT = 0.80f;

            float wPlayer = Math.Clamp(playerRounds * LIVE_PER_ROUND, 0f, MAX_LIVE_WEIGHT);

            float wGlobal;
            if (round <= 2) {
                wGlobal = 0.00f;
            } else if (round <= 4) {
                wGlobal = 0.40f;
            } else if (round <= 10) {
                wGlobal = 0.60f;
            } else {
                wGlobal = 0.80f;
            }

            float wLive = MathF.Min(wPlayer, wGlobal);
            wLive = MathF.Min(wLive, 0.80f);

            return baseline * (1f - wLive) + live * wLive;
        }

        public static float GetWarmupSignal(GameStats? gs, int userId, PlayerStats? ps) {
            if (gs == null || userId <= 0 || ps == null) {
                return 0f;
            }

            return GetBaselineSkill(gs, userId, ps);
        }

        public static float GetBaselineSkill(GameStats? gs, int userId, PlayerStats? ps) {
            if (gs == null || userId <= 0 || ps == null) {
                return 0f;
            }

            float s90 = gs.GetCached90dByUserId(userId);
            if (s90 > 0f) {
                return s90;
            }

            return GetProvisionalSkill(ps);
        }

        public static float GetProvisionalSkill(PlayerStats? ps) {
            if (ps == null) {
                return PROV_MIN;
            }

            string key = string.IsNullOrEmpty(ps.steamid) ? (ps.name ?? "unknown") : ps.steamid;
            int span = PROV_MAX - PROV_MIN + 1;
            int val = PROV_MIN + Math.Abs(StableHash(key)) % span;
            return val;
        }

        private static int StableHash(string s) {
            unchecked {
                int h = 23;
                for (int i = 0; i < s.Length; i++) {
                    h = h * 31 + s[i];
                }

                return h == 0 ? 1 : h;
            }
        }

        // ----- Elo path (new, opt-in via balancer_skill_source elo/shadow) -----
        //
        // Feeds from EloRating.rating (part one of the two-part system -- never points, see
        // ELO-MODULE.md/STATS-MODULE.md asks 4/19/20). No baseline/live blend here -- Elo has
        // no equivalent of GameStats' continuous in-round recompute; EloRating.liveRating
        // already IS a single, continuously-updated value at every moment, so there's no
        // "baseline vs live" question left to answer.

        public static float GetEffectiveSkillForPlayer(EloRating? elo, CCSPlayerController? player, float rosterMedian, int minRatedMatches) {
            if (player == null || !player.IsValid) {
                return rosterMedian;
            }

            return GetEffectiveSkill(elo, player.SteamID, rosterMedian, minRatedMatches);
        }

        public static float GetEffectiveSkill(EloRating? elo, int userId, float rosterMedian, int minRatedMatches) {
            var player = Utilities.GetPlayerFromUserid(userId);
            if (player == null || !player.IsValid) {
                return rosterMedian;
            }

            return GetEffectiveSkill(elo, player.SteamID, rosterMedian, minRatedMatches);
        }

        // Below minRatedMatches duels, a player's own rating is too noisy to trust for team
        // balancing -- treated as the roster median instead, same as a player with no rating
        // at all. Matches OSWeb's BalancedDraft: an unranked/barely-rated player is exactly
        // average for this roster, so they neither drag a team down nor lift it up. A flat
        // "bad player" default would stack every newcomer onto the same side.
        public static float GetEffectiveSkill(EloRating? elo, ulong steamId64, float rosterMedian, int minRatedMatches) {
            if (elo == null || steamId64 == 0) {
                return rosterMedian;
            }

            if (!elo.TryGetRating(steamId64, out int rating, out int matches) || matches < minRatedMatches) {
                return rosterMedian;
            }

            return rating;
        }

        // Computed ONCE per balance pass by the caller (cheap either way at CS2 server
        // headcounts, but there's no reason to rescan the roster for every player's lookup).
        // Ranked players only (matches >= minRatedMatches) -- an unranked player shouldn't be
        // able to drag the median toward the fallback that unranked players themselves fall
        // back to.
        public static float ComputeRosterMedian(EloRating? elo, IEnumerable<int> userIds, int minRatedMatches, float fallback) {
            var ratings = new List<int>();

            if (elo != null) {
                foreach (int userId in userIds) {
                    var player = Utilities.GetPlayerFromUserid(userId);
                    if (player == null || !player.IsValid) {
                        continue;
                    }

                    if (elo.TryGetRating(player.SteamID, out int rating, out int matches) && matches >= minRatedMatches) {
                        ratings.Add(rating);
                    }
                }
            }

            if (ratings.Count == 0) {
                return fallback;
            }

            ratings.Sort();
            int mid = ratings.Count / 2;
            return ratings.Count % 2 == 0 ? (ratings[mid - 1] + ratings[mid]) / 2f : ratings[mid];
        }

        // Spread (max-min) of the same ranked-only pool the median above uses -- lets swap
        // thresholds scale to whatever range this rating system actually produces instead of
        // hardcoding a number tuned for GameStats' old ~4000-11000 skill scale. Floored so a
        // near-uniform roster (everyone within a few points of each other, which is also
        // exactly when there's the least to fix) doesn't collapse every threshold to zero.
        public static float ComputeRosterSpread(EloRating? elo, IEnumerable<int> userIds, int minRatedMatches, float minSpread) {
            var ratings = new List<int>();

            if (elo != null) {
                foreach (int userId in userIds) {
                    var player = Utilities.GetPlayerFromUserid(userId);
                    if (player == null || !player.IsValid) {
                        continue;
                    }

                    if (elo.TryGetRating(player.SteamID, out int rating, out int matches) && matches >= minRatedMatches) {
                        ratings.Add(rating);
                    }
                }
            }

            if (ratings.Count < 2) {
                return minSpread;
            }

            ratings.Sort();
            float spread = ratings[^1] - ratings[0];
            return Math.Max(spread, minSpread);
        }
    }
}
