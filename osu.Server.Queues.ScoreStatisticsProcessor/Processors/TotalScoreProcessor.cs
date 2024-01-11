// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Increment total score for the user.
    /// </summary>
    [UsedImplicitly]
    public class TotalScoreProcessor : IProcessor
    {
        public bool RunOnFailedScores => true;

        public bool RunOnLegacyScores => false;

        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (previousVersion < 2)
                return;

            if (previousVersion < 8)
            {
                userStats.total_score -= (ulong)score.TotalScore;
                userStats.level = calculateLevel(userStats.total_score);
                return;
            }

            userStats.total_score -= (ulong)score.GetDisplayScore(ScoringMode.Classic);
            userStats.level = calculateLevel(userStats.total_score);
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            userStats.total_score += (ulong)score.GetDisplayScore(ScoringMode.Classic);
            userStats.level = calculateLevel(userStats.total_score);
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }

        private static float calculateLevel(ulong totalScore)
        {
            // Based on https://github.com/peppy/osu-stable-reference/blob/7519cafd1823f1879c0d9c991ba0e5c7fd3bfa02/osu!/Online/Drawable/User.cs#L383-L399
            ulong remainingScore = totalScore;
            float level = 0;

            while (remainingScore > 0)
            {
                // if we're exceeding available array entries, continue using the requirement for the highest level.
                ulong nextLevelRequirement = to_next_level[Math.Min(to_next_level.Length - 1, (int)Math.Round(level))];

                // always increment by at most one level, but include the fractional portion for the final level.
                level += Math.Min(1, (float)remainingScore / nextLevelRequirement);

                remainingScore -= nextLevelRequirement;
            }

            // were using level as a zero based index, but storage is one-based.
            return level + 1;
        }

        private static readonly ulong[] to_next_level =
        {
            30000, 100000, 210000, 360000, 550000, 780000, 1050000,
            1360000, 1710000, 2100000, 2530000, 3000000, 3510000, 4060000,
            4650000, 5280000, 5950000, 6660000, 7410000, 8200000, 9030000,
            9900000, 10810000, 11760000, 12750000, 13780000, 14850000,
            15960000, 17110000, 18300000, 19530000, 20800000, 22110000,
            23460000, 24850000, 26280000, 27750000, 29260000, 30810000,
            32400000, 34030000, 35700000, 37410000, 39160000, 40950000,
            42780000, 44650000, 46560000, 48510000, 50500000, 52530000,
            54600000, 56710000, 58860000, 61050000, 63280000, 65550000,
            67860000, 70210001, 72600001, 75030002, 77500003, 80010006,
            82560010, 85150019, 87780034, 90450061, 93160110, 95910198,
            98700357, 101530643, 104401157, 107312082, 110263748,
            113256747, 116292144, 119371859, 122499346, 125680824,
            128927482, 132259468, 135713043, 139353477, 143298259,
            147758866, 153115959, 160054726, 169808506, 184597311,
            208417160, 248460887, 317675597, 439366075, 655480935,
            1041527682, 1733419828, 2975801691, 5209033044, 9225761479,
            99999999999, 99999999999, 99999999999, 99999999999,
            99999999999, 99999999999, 99999999999, 99999999999,
            99999999999, 99999999999, 99999999999, 99999999999,
            99999999999, 99999999999, 99999999999, 99999999999,
            99999999999, 99999999999, 99999999999, 99999999999,
            99999999999, 99999999999, 99999999999, 99999999999
        };
    }
}
