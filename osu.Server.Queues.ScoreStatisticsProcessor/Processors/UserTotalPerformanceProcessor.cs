// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Dapper;
using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    public class UserTotalPerformanceProcessor : IProcessor
    {
        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            IEnumerable<SoloScoreWithPerformance> items = conn.Query<SoloScoreWithPerformance>(
                @"SELECT
                    s.*,
                    p.pp AS `pp`
                FROM solo_scores s
                JOIN solo_scores_performance p 
                ON s.id = p.score_id
                WHERE s.user_id = @UserId", new
                {
                    UserId = userStats.user_id
                }, transaction);

            Build[] builds = conn.Query<Build>("SELECT * FROM osu_builds", transaction: transaction).ToArray();

            // Filter out any scores which do not contribute to total PP for the user.
            SoloScoreWithPerformance[] filteredItems =
                items
                    // Select scores with valid PP values.
                    .Where(i => i.pp != null)
                    // Select scores which have no build (have been imported from osu_scores_high tables),
                    // or a build which allows performance points to be awarded.
                    .Where(i => i.ScoreInfo.build_id == null || builds.Any(b => b.build_id == i.ScoreInfo.build_id && b.allow_performance))
                    // Group by beatmap ID.
                    .GroupBy(i => i.beatmap_id)
                    // Extract the maximum PP for each group.
                    .Select(g => g.OrderByDescending(i => i.pp).First())
                    // And order the maximums by decreasing value.
                    .OrderByDescending(i => i.pp)
                    .ToArray();

            // Build the diminishing sum
            double factor = 1;
            double totalPp = 0;
            double totalAccuracy = 0;

            foreach (var item in filteredItems)
            {
                Debug.Assert(item.pp != null);

                totalPp += item.pp.Value * factor;
                totalAccuracy += item.ScoreInfo.accuracy * factor;
                factor *= 0.95;
            }

            // This weird factor is to keep legacy compatibility with the diminishing bonus of 0.25 by 0.9994 each score.
            totalPp += (417.0 - 1.0 / 3.0) * (1.0 - Math.Pow(0.9994, filteredItems.Length));

            // We want our accuracy to be normalized.
            if (filteredItems.Length > 0)
            {
                // We want the percentage, not a factor in [0, 1], hence we divide 20 by 100.
                totalAccuracy *= 100.0 / (20 * (1 - Math.Pow(0.95, filteredItems.Length)));
            }

            userStats.rank_score = (float)totalPp;
            userStats.accuracy_new = (float)totalAccuracy;
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        [Serializable]
        private class SoloScoreWithPerformance : SoloScore
        {
            public double? pp { get; set; }
        }
    }
}
