// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
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
            var items = conn.Query<(int beatmapId, double? pp, double accuracy)>(
                @"SELECT
                    s.beatmap_id,
                    p.pp,
                    s.data->'$.accuracy'
                FROM solo_scores s
                JOIN solo_scores_performance p 
                ON s.id = p.score_id
                WHERE s.user_id = @UserId", new
                {
                    UserId = userStats.user_id
                }, transaction);

            var orderedItems =
                items.Where(i => i.pp != null)
                     .GroupBy(i => i.beatmapId)
                     .Select(g =>
                     {
                         var (_, maxItemPp, maxItemAccuracy) = g.OrderByDescending(i => i.pp).First();
                         return new
                         {
                             BeatmapId = g.Key,
                             Pp = maxItemPp!.Value,
                             Accuracy = maxItemAccuracy
                         };
                     })
                     .OrderByDescending(i => i.Pp)
                     .ToArray();

            // Diminishing sum.
            double factor = 1;
            double totalPp = 0;
            double totalAccuracy = 0;

            foreach (var item in orderedItems)
            {
                totalPp += item.Pp * factor;
                totalAccuracy += item.Accuracy * factor;
                factor *= 0.95;
            }

            // This weird factor is to keep legacy compatibility with the diminishing bonus of 0.25 by 0.9994 each score
            totalPp += (417.0 - 1.0 / 3.0) * (1.0 - Math.Pow(0.9994, orderedItems.Length));

            // We want our accuracy to be normalized.
            if (orderedItems.Length > 0)
                // We want the percentage, not a factor in [0, 1], hence we divide 20 by 100
                totalAccuracy *= 100.0 / (20 * (1 - Math.Pow(0.95, orderedItems.Length)));

            userStats.rank_score = (float)totalPp;
            userStats.accuracy_new = (float)totalAccuracy;
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }
    }
}
