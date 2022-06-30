// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using Dapper;
using Dapper.Contrib.Extensions;
using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor
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
        public static UserStats? GetUserStats(SoloScoreInfo score, MySqlConnection db, MySqlTransaction? transaction = null)
            => GetUserStats(score.user_id, score.ruleset_id, db, transaction);

        /// <summary>
        /// Retrieve user stats for a user based for a given ruleset.
        /// Creates a new entry if the user does not yet have one.
        /// </summary>
        /// <param name="userId">The user to retrieve the stats for.</param>
        /// <param name="rulesetId">The ruleset to retrieve the stats for.</param>
        /// <param name="db">The database connection.</param>
        /// <param name="transaction">The database transaction, if one exists.</param>
        /// <returns>The retrieved user stats. Null if the ruleset or user was not valid.</returns>
        public static UserStats? GetUserStats(int userId, int rulesetId, MySqlConnection db, MySqlTransaction? transaction = null)
        {
            switch (rulesetId)
            {
                default:
                    Console.WriteLine($"Unsupported ruleset: {rulesetId}");
                    return null;

                case 0:
                    return getUserStats<UserStatsOsu>(userId, rulesetId, db, transaction);

                case 1:
                    return getUserStats<UserStatsTaiko>(userId, rulesetId, db, transaction);

                case 2:
                    return getUserStats<UserStatsCatch>(userId, rulesetId, db, transaction);

                case 3:
                    return getUserStats<UserStatsMania>(userId, rulesetId, db, transaction);
            }
        }

        private static T getUserStats<T>(int userId, int rulesetId, MySqlConnection db, MySqlTransaction? transaction = null)
            where T : UserStats, new()
        {
            var dbInfo = LegacyDatabaseHelper.GetRulesetSpecifics(rulesetId);

            // for simplicity, let's ensure the row already exists as a separate step.
            var userStats = db.QuerySingleOrDefault<T>($"SELECT * FROM {dbInfo.UserStatsTable} WHERE user_id = @UserId FOR UPDATE", new
            {
                UserId = userId
            }, transaction);

            if (userStats == null)
            {
                userStats = new T
                {
                    user_id = userId,
                    country_acronym = db.QueryFirstOrDefault<string>("SELECT country_acronym FROM phpbb_users WHERE user_id = @UserId", new
                    {
                        UserId = userId
                    }, transaction) ?? "XX",
                };

                db.Insert(userStats, transaction);
            }

            return userStats;
        }

        /// <summary>
        /// Retrieve user stats for a user based on a score context.
        /// Creates a new entry if the user does not yet have one.
        /// </summary>
        /// <param name="score">The score to use for the user and ruleset lookup.</param>
        /// <param name="db">The database connection.</param>
        /// <param name="transaction">The database transaction, if one exists.</param>
        /// <returns>The retrieved user stats. Null if the ruleset or user was not valid.</returns>
        public static Task<UserStats?> GetUserStatsAsync(SoloScoreInfo score, MySqlConnection db, MySqlTransaction? transaction = null)
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
        public static async Task<UserStats?> GetUserStatsAsync(int userId, int rulesetId, MySqlConnection db, MySqlTransaction? transaction = null)
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

        private static async Task<T> getUserStatsAsync<T>(int userId, int rulesetId, MySqlConnection db, MySqlTransaction? transaction = null)
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
        public static void UpdateUserStats(UserStats stats, MySqlConnection db, MySqlTransaction? transaction = null)
        {
            switch (stats)
            {
                case UserStatsOsu userStatsOsu:
                    db.Update(userStatsOsu, transaction);
                    break;

                case UserStatsTaiko userStatsTaiko:
                    db.Update(userStatsTaiko, transaction);
                    break;

                case UserStatsCatch userStatsCatch:
                    db.Update(userStatsCatch, transaction);
                    break;

                case UserStatsMania userStatsMania:
                    db.Update(userStatsMania, transaction);
                    break;
            }
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
    }
}
