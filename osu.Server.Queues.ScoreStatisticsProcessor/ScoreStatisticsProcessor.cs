// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Dapper.Contrib.Extensions;
using MySqlConnector;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    public class ScoreStatisticsProcessor : QueueProcessor<ScoreItem>
    {
        public const int VERSION = 2;

        private readonly List<IProcessor> processors = new List<IProcessor>();

        public ScoreStatisticsProcessor()
            : base(new QueueConfiguration { InputQueueName = "score-statistics" })
        {
            SqlMapper.AddTypeHandler(new StatisticsTypeHandler());
            DapperExtensions.InstallDateTimeOffsetMapper();

            // add each processor automagically.
            foreach (var t in typeof(ScoreStatisticsProcessor).Assembly.GetTypes().Where(t => !t.IsInterface && typeof(IProcessor).IsAssignableFrom(t)))
                processors.Add(Activator.CreateInstance(t) as IProcessor);
        }

        protected override void ProcessResult(ScoreItem item)
        {
            try
            {
                using (var conn = GetDatabaseConnection())
                {
                    var score = item.Score;

                    // TODO: don't count scores which don't have an ended_at value set.
                    // this should only be done once osu! is updated to actually report retries and failures.
                    // also needs consideration in the score pump.

                    using (var transaction = conn.BeginTransaction())
                    {
                        var userStats = GetUserStats(score, conn, transaction);

                        // if required, we can rollback any previous version of processing then reapply with the latest.
                        if (item.ProcessHistory != null)
                        {
                            byte version = item.ProcessHistory.processed_version;

                            Console.WriteLine($"Item {score} already processed (v{version}), rolling back before reapplying");

                            foreach (var p in processors)
                                p.RevertFromUserStats(score, userStats, version, conn, transaction);
                        }

                        foreach (var p in processors)
                            p.ApplyToUserStats(score, userStats, conn, transaction);

                        updateUserStats(userStats, conn, transaction);
                        updateHistoryEntry(item, conn, transaction);

                        transaction.Commit();
                    }

                    foreach (var p in processors)
                        p.ApplyGlobal(score, conn);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
                throw;
            }
        }

        private static void updateHistoryEntry(ScoreItem item, MySqlConnection db, MySqlTransaction transaction)
        {
            bool hadHistory = item.ProcessHistory != null;

            item.MarkProcessed();

            if (hadHistory)
                db.Update(item.ProcessHistory, transaction);
            else
                db.Insert(item.ProcessHistory, transaction);
        }

        /// <summary>
        /// Retrieve user stats for a user based on a score context.
        /// Creates a new entry if the user does not yet have one.
        /// </summary>
        /// <param name="score">The score to use for the user and ruleset lookup.</param>
        /// <param name="db">The database connection.</param>
        /// <param name="transaction">The database transaction, if one exists.</param>
        /// <returns>The retrieved user stats.</returns>
        /// <exception cref="ArgumentException"></exception>
        public static UserStats GetUserStats(SoloScore score, MySqlConnection db, MySqlTransaction transaction = null)
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

        private static T getUserStats<T>(SoloScore score, MySqlConnection db, MySqlTransaction transaction = null)
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
    }
}
