using System;
using CounterStrikeSharp.API.Core;
using OSBase.Modules;

namespace OSBase.Helpers {
    public static class SkillResolver {
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
    }
}