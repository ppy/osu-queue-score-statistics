// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Stores;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Computes the total performance points for users.
    /// </summary>
    public class UserTotalPerformanceProcessor : IProcessor
    {
        private BeatmapStore? beatmapStore;
        private BuildStore? buildStore;

        private static readonly bool process_user_totals = Environment.GetEnvironmentVariable("PROCESS_USER_TOTALS") == "1";

        // This processor needs to run after the score's PP value has been processed.
        public int Order => ScorePerformanceProcessor.ORDER + 1;

        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (!process_user_totals)
                return;

            var dbInfo = LegacyDatabaseHelper.GetRulesetSpecifics(score.RulesetID);

            int warnings = conn.QuerySingleOrDefault<int>($"SELECT `user_warnings` FROM {dbInfo.UsersTable} WHERE `user_id` = @UserId", new
            {
                UserId = userStats.user_id
            }, transaction);

            if (warnings > 0)
                return;

            UpdateUserStatsAsync(userStats, score.RulesetID, conn, transaction).Wait();
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
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
            beatmapStore ??= await BeatmapStore.CreateAsync(connection, transaction);
            buildStore ??= await BuildStore.CreateAsync(connection, transaction);

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
                if (s.ScoreInfo.BuildID == null)
                    return false;

                // Performance needs to be allowed for the build.
                return buildStore.GetBuild(s.ScoreInfo.BuildID.Value)?.allow_performance != true;
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
            double factor = 52.5;
            double totalPp = 0;
            double totalAccuracy = 0;
            double weightTotal = 0;

            foreach (var (item, i) in groupedItems.Select((value, i) => ( value, i )))
            {
                // This sum is an unbounded logarithmic style summation that uses factor and the index to weight each score.
                weight = (1 + Math.Pow(factor / (i + 1), 2)) / (i + 1 + Math.Pow(factor / (i + 1), 2));
                totalPp += item.pp!.Value * weight;
                totalAccuracy += item.ScoreInfo.Accuracy * weight;
                weightTotal += weight;
            }

            // We want our accuracy to be normalized.
            if (groupedItems.Length > 0)
            {
                // We want the percentage, not a factor in [0, 1], hence 100 in the numerator.
                totalAccuracy *= 100.0 / weightTotal;
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
