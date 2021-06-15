// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Dapper;
using Dapper.Contrib.Extensions;
using MySqlConnector;
using osu.Server.QueueProcessor;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    public class ScoreStatisticsProcessor : QueueProcessor<ScoreItem>
    {
        public ScoreStatisticsProcessor()
            : base(new QueueConfiguration { InputQueueName = "score-statistics" })
        {
            SqlMapper.AddTypeHandler(new StatisticsTypeHandler());
        }

        protected override void ProcessResult(ScoreItem item)
        {
            try
            {
                using (var db = GetDatabaseConnection())
                using (var transaction = db.BeginTransaction())
                {
                    var score = item.Score;

                    var userStats = getUserStats(score, db, transaction);

                    if (item.processed_at != null)
                    {
                        Console.WriteLine($"Item {score} already processed, rolling back before reapplying");

                        // if required, we can rollback any previous version of processing then reapply with the latest.
                        userStats.playcount--;
                    }

                    userStats.playcount++;

                    updateUserStats(userStats, db, transaction);

                    // eventually this will (likely) not be a thing, as we will be reading directly from the queue and not worrying about a database store.
                    db.Execute("UPDATE solo_scores SET processed_at = NOW() WHERE id = @id", score, transaction);

                    transaction.Commit();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }

        private static UserStats getUserStats(SoloScore score, MySqlConnection db, MySqlTransaction transaction)
        {
            switch (score.ruleset_id)
            {
                default:
                    throw new ArgumentException($"Item {score} is for an unsupported ruleset {score.ruleset_id}");

                case 0:
                    return getUserStats<UserStatsOsu>(score, db, transaction);

                case 1:
                    return getUserStats<UserStatsTaiko>(score, db, transaction);

                case 2:
                    return getUserStats<UserStatsCatch>(score, db, transaction);

                case 3:
                    return getUserStats<UserStatsMania>(score, db, transaction);
            }
        }

        /// <summary>
        /// Update stats in database with the correct generic type, because dapper is stupid.
        /// </summary>
        private static void updateUserStats(UserStats stats, MySqlConnection db, MySqlTransaction transaction)
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

        private static T getUserStats<T>(SoloScore score, MySqlConnection db, MySqlTransaction transaction)
            where T : UserStats, new()
        {
            var dbInfo = LegacyDatabaseHelper.GetRulesetSpecifics(score.ruleset_id);

            // for simplicity, let's ensure the row already exists as a separate step.
            var userStats = db.QuerySingleOrDefault<T>($"SELECT * FROM {dbInfo.UserStatsTable} WHERE user_id = @user_id", score, transaction);

            if (userStats == null)
            {
                userStats = new T
                {
                    user_id = score.user_id
                };

                db.Insert(userStats, transaction);
            }

            return userStats;
        }
    }
}
