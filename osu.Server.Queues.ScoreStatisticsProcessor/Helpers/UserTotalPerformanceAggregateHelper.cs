// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public static class UserTotalPerformanceAggregateHelper
    {
        public static (float totalPp, float totalAccuracy) CalculateUserTotalPerformanceAggregates(List<SoloScore> scores)
        {
            SoloScore[] groupedScores = scores
                                        // Group by beatmap ID.
                                        .GroupBy(i => i.beatmap_id)
                                        // Extract the maximum PP for each beatmap.
                                        .Select(g => g.OrderByDescending(i => i.pp).First())
                                        // And order the beatmaps by decreasing value.
                                        .OrderByDescending(i => i.pp)
                                        .ToArray();

            // Build the diminishing sum
            double factor = 1;
            double totalPp = 0;
            double totalAccuracy = 0;

            foreach (var score in groupedScores)
            {
                totalPp += score.pp!.Value * factor;
                totalAccuracy += score.accuracy * factor;
                factor *= 0.95;
            }

            // This weird factor is to keep legacy compatibility with the diminishing bonus of 0.25 by 0.9994 each score.
            // Of note, this is using de-duped scores which may be below 1,000 depending on how the user plays.
            totalPp += (417.0 - 1.0 / 3.0) * (1.0 - Math.Pow(0.995, scores.Count));

            // We want our accuracy to be normalized.
            if (groupedScores.Length > 0)
            {
                // We want the percentage, not a factor in [0, 1], hence we divide 20 by 100.
                totalAccuracy *= 100.0 / (20 * (1 - Math.Pow(0.95, groupedScores.Length)));
            }

            return ((float)totalPp, (float)totalAccuracy);
        }
    }
}
