// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using MySqlConnector;
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

        // [ruleset_id, [rank_score, count_users_above]]
        private readonly ConcurrentDictionary<int, MemoryCache> rankScoreIndexPartitionCache =
            new ConcurrentDictionary<int, MemoryCache>();

        public void RevertFromUserStats(SoloScore score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
        }

        public void ApplyToUserStats(SoloScore score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            var dbInfo = LegacyDatabaseHelper.GetRulesetSpecifics(score.ruleset_id);

            int warnings = conn.QuerySingleOrDefault<int>($"SELECT `user_warnings` FROM {dbInfo.UsersTable} WHERE `user_id` = @UserId", new
            {
                UserId = userStats.user_id
            }, transaction);

            if (warnings > 0)
                return;

            UpdateUserStatsAsync(userStats, score.ruleset_id, conn, transaction).Wait();
        }

        public void ApplyGlobal(SoloScore score, MySqlConnection conn)
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
        /// <param name="updateIndex">Whether to update the rank index / history / user highest rank statistics.</param>
        public async Task UpdateUserStatsAsync(UserStats userStats, int rulesetId, MySqlConnection connection, MySqlTransaction? transaction = null, bool updateIndex = true)
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

            (userStats.rank_score, userStats.accuracy_new) = UserTotalPerformanceAggregateHelper.CalculateUserTotalPerformanceAggregates(userStats.user_id, scores);

            if (updateIndex)
                await updateGlobalRank(userStats, connection, transaction, dbInfo);
        }

        private async Task updateGlobalRank(UserStats userStats, MySqlConnection connection, MySqlTransaction? transaction, LegacyDatabaseHelper.RulesetDatabaseInfo dbInfo)
        {
            // User's current global rank.
            userStats.rank_score_index = await getUserRankScoreIndex(userStats, connection, transaction, dbInfo);

            // User's historical best rank (ever).
            int userHistoricalHighestRank = await connection.QuerySingleOrDefaultAsync<int?>("SELECT `rank` FROM `osu_user_performance_rank_highest` WHERE `user_id` = @userId AND `mode` = @mode",
                new
                {
                    userId = userStats.user_id,
                    mode = dbInfo.RulesetId
                }, transaction) ?? 0;

            if (userHistoricalHighestRank == 0 || userHistoricalHighestRank > userStats.rank_score_index)
            {
                await connection.ExecuteAsync("REPLACE INTO `osu_user_performance_rank_highest` (`user_id`, `mode`, `rank`) VALUES (@userId, @mode, @rank)",
                    new
                    {
                        userId = userStats.user_id,
                        mode = dbInfo.RulesetId,
                        rank = userStats.rank_score_index
                    }, transaction);
            }

            // Update user's 90-day rolling rank history for today.
            // TODO: This doesn't need to be updated here. the osu-web graph always uses `rank_score_index` for the current day.
            // TODO: code should be moved/used in the future for daily processing purposes (once migrated from web-10).

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

        private async Task<int> getUserRankScoreIndex(UserStats userStats, MySqlConnection connection, MySqlTransaction? transaction, LegacyDatabaseHelper.RulesetDatabaseInfo dbInfo)
        {
            var rulesetCache = rankScoreIndexPartitionCache.GetOrAdd(dbInfo.RulesetId, _ => new MemoryCache(new MemoryCacheOptions()));

            const int partition_size = 100;

            int partitionCutoff = ((int)(userStats.rank_score / partition_size) + 1) * partition_size;

            // This query is indexed as much as it can be, but for COUNT(*) across millions of rows it can still be slow.
            // Cache the majority lookup by partitioning over `pp` reduces the per-query lookup overhead.
            int partitionCount = await rulesetCache.GetOrCreateAsync(partitionCutoff, async entry =>
            {
                int cutoff = await connection.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {dbInfo.UserStatsTable} WHERE rank_score > @partitionCutoff",
                    new { partitionCutoff }, transaction: transaction);

                // Expire faster for results with lower user counts. Queries will be faster for lower counts so this isn't a huge concern.
                int expiryMinutes = Math.Clamp(cutoff / 100, 1, 120);

                entry.SetAbsoluteExpiration(TimeSpan.FromMinutes(expiryMinutes));

                return cutoff;
            });

            int remainingCount = await connection.QuerySingleAsync<int>(
                $"SELECT COUNT(*) FROM {dbInfo.UserStatsTable} WHERE rank_score > @rankScoreCutoff AND rank_score <= @partitionCutoff AND user_id != @userId",
                new
                {
                    userId = userStats.user_id,
                    rankScoreCutoff = userStats.rank_score,
                    partitionCutoff
                }, transaction: transaction);

            return partitionCount + remainingCount + 1;
        }
    }
}
