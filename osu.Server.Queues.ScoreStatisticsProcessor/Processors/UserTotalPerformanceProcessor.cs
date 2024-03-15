// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Computes the total performance points for users.
    /// </summary>
    public class UserTotalPerformanceProcessor : IProcessor
    {
        // This processor needs to run after the score's PP value has been processed.
        public int Order => ScorePerformanceProcessor.ORDER + 1;

        public bool RunOnFailedScores => false;

        public bool RunOnLegacyScores => true;

        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
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
            var dbInfo = LegacyDatabaseHelper.GetRulesetSpecifics(rulesetId);

            List<SoloScore> scores = (await connection.QueryAsync<SoloScore>(
                "SELECT beatmap_id, pp, accuracy FROM scores WHERE "
                + "`user_id` = @UserId AND "
                + "`ruleset_id` = @RulesetId AND "
                + "`pp` IS NOT NULL AND "
                + "`preserve` = 1 AND "
                + "`ranked` = 1 "
                + "ORDER BY pp DESC LIMIT 1000", new
                {
                    UserId = userStats.user_id,
                    RulesetId = rulesetId
                }, transaction: transaction)).ToList();

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

            userStats.rank_score = (float)totalPp;
            await updateGlobalRank(userStats, connection, transaction, dbInfo);
            userStats.accuracy_new = (float)totalAccuracy;
        }

        private static async Task updateGlobalRank(UserStats userStats, MySqlConnection connection, MySqlTransaction? transaction, LegacyDatabaseHelper.RulesetDatabaseInfo dbInfo)
        {
            // User's current global rank.
            userStats.rank_score_index = (await connection.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {dbInfo.UserStatsTable} WHERE rank_score > {userStats.rank_score}", transaction: transaction)) + 1;

            // User's historical best rank (ever).
            int userHistoricalHighestRank = await connection.QuerySingleOrDefaultAsync<int?>($"SELECT `rank` FROM `osu_user_performance_rank_highest` WHERE `user_id` = @userId AND `mode` = @mode",
                new
                {
                    userId = userStats.user_id,
                    mode = dbInfo.RulesetId
                }, transaction) ?? 0;

            if (userHistoricalHighestRank == 0 || userHistoricalHighestRank > userStats.rank_score_index)
            {
                await connection.ExecuteAsync($"REPLACE INTO `osu_user_performance_rank_highest` (`user_id`, `mode`, `rank`) VALUES (@userId, @mode, @rank)",
                    new
                    {
                        userId = userStats.user_id,
                        mode = dbInfo.RulesetId,
                        rank = userStats.rank_score_index
                    }, transaction);
            }

            // User's 90-day rolling rank history.
            int todaysRankColumn = await connection.QuerySingleOrDefaultAsync<int?>(@"SELECT `count` FROM `osu_counts` WHERE `name` = @todaysRankColumn", new
            {
                dbInfo.TodaysRankColumn
            }, transaction) ?? 0;

            await connection.ExecuteAsync(
                $"INSERT INTO `osu_user_performance_rank` (`user_id`, `mode`, `r{todaysRankColumn}`) VALUES (@userId, @mode, @rank) "
                + $"ON DUPLICATE KEY UPDATE `r{todaysRankColumn}` = @rank",
                new
                {
                    userId = userStats.user_id,
                    mode = dbInfo.RulesetId,
                    rank = userStats.rank_score_index
                }, transaction);
        }
    }
}
