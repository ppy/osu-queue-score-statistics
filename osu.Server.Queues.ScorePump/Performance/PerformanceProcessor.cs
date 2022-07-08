// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Stores;
using Beatmap = osu.Server.Queues.ScoreStatisticsProcessor.Models.Beatmap;

namespace osu.Server.Queues.ScorePump.Performance
{
    /// <summary>
    /// A helper class which can be used to compute scores' raw PP values and users' total PP values.
    /// </summary>
    public class PerformanceProcessor
    {
        private readonly BeatmapStore beatmapStore;
        private readonly IReadOnlyDictionary<int, bool> builds;

        private PerformanceProcessor(BeatmapStore beatmapStore, IEnumerable<KeyValuePair<int, bool>> builds)
        {
            this.beatmapStore = beatmapStore;
            this.builds = new Dictionary<int, bool>(builds);
        }

        /// <summary>
        /// Creates a new <see cref="PerformanceProcessor"/>.
        /// </summary>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        /// <returns>The created <see cref="PerformanceProcessor"/>.</returns>
        public static async Task<PerformanceProcessor> CreateAsync(MySqlConnection? connection, MySqlTransaction? transaction = null)
        {
            var dbBuilds = await connection.QueryAsync<Build>($"SELECT * FROM {Build.TABLE_NAME}", transaction: transaction);

            return new PerformanceProcessor
            (
                await BeatmapStore.CreateAsync(connection, transaction),
                dbBuilds.Select(b => new KeyValuePair<int, bool>(b.build_id, b.allow_performance))
            );
        }

        /// <summary>
        /// Updates a user's stats with their total PP/accuracy.
        /// </summary>
        /// <remarks>
        /// This does not insert the new stats values into the database.
        /// </remarks>
        /// <param name="rulesetId">The ruleset for which to update the total PP.</param>
        /// <param name="userStats">An existing <see cref="UserStats"/> object to update with.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        public async Task UpdateUserStatsAsync(UserStats userStats, int rulesetId, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            List<SoloScoreWithPerformance> scores = (await connection.QueryAsync<SoloScoreWithPerformance>(
                $"SELECT `s`.*, `p`.`pp` FROM {SoloScore.TABLE_NAME} `s` "
                + $"JOIN {SoloScorePerformance.TABLE_NAME} `p` ON `s`.`id` = `p`.`score_id` "
                + "WHERE `s`.`user_id` = @UserId "
                + "AND `s`.`ruleset_id` = @RulesetId", new
                {
                    UserId = userStats.user_id,
                    RulesetId = rulesetId
                }, transaction: transaction)).ToList();

            Dictionary<int, Beatmap?> beatmaps = new Dictionary<int, Beatmap?>();

            foreach (var score in scores)
            {
                if (beatmaps.ContainsKey(score.beatmap_id))
                    continue;

                beatmaps[score.beatmap_id] = await beatmapStore.GetBeatmapAsync(score.beatmap_id, connection, transaction);
            }

            // Filter out invalid scores.
            scores.RemoveAll(s =>
            {
                // Score must have a valid pp.
                if (s.pp == null)
                    return true;

                // Beatmap must exist.
                if (!beatmaps.TryGetValue(s.beatmap_id, out var beatmap) || beatmap == null)
                    return true;

                // Given beatmap needs to be allowed to give performance.
                if (!beatmapStore.IsBeatmapValidForPerformance(beatmap, s.ruleset_id))
                    return true;

                // Scores with no build were imported from the legacy high scores tables and are always valid.
                if (s.ScoreInfo.build_id == null)
                    return false;

                // Performance needs to be allowed for the build.
                return !builds[s.ScoreInfo.build_id.Value];
            });

            SoloScoreWithPerformance[] groupedItems = scores
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

            foreach (var item in groupedItems)
            {
                totalPp += item.pp!.Value * factor;
                totalAccuracy += item.ScoreInfo.accuracy * factor;
                factor *= 0.95;
            }

            // This weird factor is to keep legacy compatibility with the diminishing bonus of 0.25 by 0.9994 each score.
            totalPp += (417.0 - 1.0 / 3.0) * (1.0 - Math.Pow(0.9994, groupedItems.Length));

            // We want our accuracy to be normalized.
            if (groupedItems.Length > 0)
            {
                // We want the percentage, not a factor in [0, 1], hence we divide 20 by 100.
                totalAccuracy *= 100.0 / (20 * (1 - Math.Pow(0.95, groupedItems.Length)));
            }

            userStats.rank_score = (float)totalPp;
            userStats.accuracy_new = (float)totalAccuracy;
        }

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        [Serializable]
        private class SoloScoreWithPerformance : SoloScore
        {
            public double? pp { get; set; }
        }
    }
}
