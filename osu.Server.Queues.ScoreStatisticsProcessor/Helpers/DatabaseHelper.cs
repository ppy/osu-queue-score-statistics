// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using Dapper;
using Dapper.Contrib.Extensions;
using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public static class DatabaseHelper
    {
        /// <summary>
        /// Retrieve user stats for a user based on a score context.
        /// Creates a new entry if the user does not yet have one.
        /// </summary>
        /// <param name="score">The score to use for the user and ruleset lookup.</param>
        /// <param name="db">The database connection.</param>
        /// <param name="transaction">The database transaction, if one exists.</param>
        /// <returns>The retrieved user stats. Null if the ruleset or user was not valid.</returns>
        public static Task<UserStats?> GetUserStatsAsync(SoloScore score, MySqlConnection db, MySqlTransaction? transaction = null)
            => GetUserStatsAsync(score.user_id, score.ruleset_id, db, transaction);

        /// <summary>
        /// Retrieve user stats for a user based for a given ruleset.
        /// Creates a new entry if the user does not yet have one.
        /// </summary>
        /// <param name="userId">The user to retrieve the stats for.</param>
        /// <param name="rulesetId">The ruleset to retrieve the stats for.</param>
        /// <param name="db">The database connection.</param>
        /// <param name="transaction">The database transaction, if one exists.</param>
        /// <returns>The retrieved user stats. Null if the ruleset or user was not valid.</returns>
        public static async Task<UserStats?> GetUserStatsAsync(uint userId, int rulesetId, MySqlConnection db, MySqlTransaction? transaction = null)
        {
            switch (rulesetId)
            {
                default:
                    Console.WriteLine($"Unsupported ruleset: {rulesetId}");
                    return null;

                case 0:
                    return await getUserStatsAsync<UserStatsOsu>(userId, rulesetId, db, transaction);

                case 1:
                    return await getUserStatsAsync<UserStatsTaiko>(userId, rulesetId, db, transaction);

                case 2:
                    return await getUserStatsAsync<UserStatsCatch>(userId, rulesetId, db, transaction);

                case 3:
                    return await getUserStatsAsync<UserStatsMania>(userId, rulesetId, db, transaction);
            }
        }

        private static async Task<T> getUserStatsAsync<T>(uint userId, int rulesetId, MySqlConnection db, MySqlTransaction? transaction = null)
            where T : UserStats, new()
        {
            var dbInfo = LegacyDatabaseHelper.GetRulesetSpecifics(rulesetId);

            // for simplicity, let's ensure the row already exists as a separate step.
            var userStats = await db.QuerySingleOrDefaultAsync<T>($"SELECT * FROM {dbInfo.UserStatsTable} WHERE user_id = @UserId FOR UPDATE", new
            {
                UserId = userId
            }, transaction);

            if (userStats == null)
            {
                userStats = new T
                {
                    user_id = userId,
                    country_acronym = await db.QueryFirstOrDefaultAsync<string>("SELECT country_acronym FROM phpbb_users WHERE user_id = @UserId", new
                    {
                        UserId = userId
                    }, transaction) ?? "XX",
                };

                await db.InsertAsync(userStats, transaction);
            }

            return userStats;
        }

        /// <summary>
        /// Update stats in database with the correct generic type, because dapper is stupid.
        /// </summary>
        public static async Task UpdateUserStatsAsync(UserStats stats, MySqlConnection db, MySqlTransaction? transaction = null)
        {
            switch (stats)
            {
                case UserStatsOsu userStatsOsu:
                    await db.UpdateAsync(userStatsOsu, transaction);
                    break;

                case UserStatsTaiko userStatsTaiko:
                    await db.UpdateAsync(userStatsTaiko, transaction);
                    break;

                case UserStatsCatch userStatsCatch:
                    await db.UpdateAsync(userStatsCatch, transaction);
                    break;

                case UserStatsMania userStatsMania:
                    await db.UpdateAsync(userStatsMania, transaction);
                    break;
            }
        }

        /// <summary>
        /// Sets a count in the database.
        /// </summary>
        /// <param name="key">The count's key.</param>
        /// <param name="value">The count's value.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        public static async Task SetCountAsync(string key, long value, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            await connection.ExecuteAsync("INSERT INTO `osu_counts` (`name`,`count`) VALUES (@Name, @Count) "
                                          + "ON DUPLICATE KEY UPDATE `count` = VALUES(`count`)", new
            {
                Name = key,
                Count = value
            }, transaction: transaction);
        }

        /// <summary>
        /// Retrieves a count value from the database.
        /// </summary>
        /// <param name="key">The count's key.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        /// <returns>The count for the provided key.</returns>
        /// <exception cref="InvalidOperationException">If the key wasn't found in the database.</exception>
        public static async Task<long> GetCountAsync(string key, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            long? res = await connection.QuerySingleOrDefaultAsync<long?>("SELECT `count` FROM `osu_counts` WHERE `name` = @Name", new
            {
                Name = key
            }, transaction: transaction);

            if (res == null)
                throw new InvalidOperationException($"Unable to retrieve count '{key}'.");

            return res.Value;
        }

        /// <summary>
        /// Given a <paramref name="score"/>, returns the best score set by the same <see cref="SoloScoreInfo.User"/>,
        /// on the same <see cref="SoloScoreInfo.Beatmap"/>, with the same <see cref="SoloScoreInfo.RulesetID"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The returned score can be the same score as the supplied <paramref name="score"/>,
        /// which means that the supplied <paramref name="score"/> is the user's best.
        /// </para>
        /// <para>
        /// This method also supports fetching the second-best, third-best score etc.,
        /// by supplying a non-zero value for the <paramref name="offset"/> parameter.
        /// </para>
        /// <para>
        /// The "best" score is defined as the score with the biggest standardised total score.
        /// If two or more scores have equal standardised total score, the newest one wins.
        /// </para>
        /// </remarks>
        /// <param name="score">The score from which to retrieve user, beatmap, and ruleset info for lookup.</param>
        /// <param name="conn">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">The current transaction, if applicable.</param>
        /// <param name="offset">How many records to skip in the sort order.</param>
        public static SoloScore? GetUserBestScoreFor(SoloScore score, MySqlConnection conn, MySqlTransaction? transaction = null, int offset = 0)
        {
            var result = conn.QueryFirstOrDefault<SoloScore?>(
                "SELECT * FROM scores WHERE `user_id` = @user_id "
                + "AND `beatmap_id` = @beatmap_id "
                + "AND `ruleset_id` = @ruleset_id "
                // preserve is not flagged on a newly arriving score until it has been completely processed (see logic in `ScoreStatisticsQueueProcessor.cs`)
                // usages in processors will generally pass in an unprocessed score, but still expect it to be included in the results of this query as if it was processed.
                // therefore we need to make an exception here to ensure it's included.
                + "AND (`preserve` = 1 OR `id` = @new_score_id) "
                + "AND `ranked` = 1 "
                + "ORDER BY total_score DESC, `id` DESC "
                + "LIMIT @offset, 1", new
                {
                    user_id = score.user_id,
                    beatmap_id = score.beatmap_id,
                    ruleset_id = score.ruleset_id,
                    new_score_id = score.id,
                    offset = offset,
                }, transaction);

            // since the beatmaps for the two scores are the same by definition of the method,
            // we can just copy them across without a refetch.
            if (result != null)
                result.beatmap = score.beatmap;

            return result;
        }
    }
}
