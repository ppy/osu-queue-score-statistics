// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Dapper;
using Dapper.Contrib.Extensions;
using MySqlConnector;
using osu.Game.Rulesets.Scoring;
using osu.Server.QueueProcessor;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    public class ScoreStatisticsProcessor : QueueProcessor<ScoreItem>
    {
        public const int VERSION = 2;

        public ScoreStatisticsProcessor()
            : base(new QueueConfiguration { InputQueueName = "score-statistics" })
        {
            SqlMapper.AddTypeHandler(new StatisticsTypeHandler());
            DapperExtensions.InstallDateTimeOffsetMapper();
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

                    if (item.ProcessHistory != null)
                    {
                        Console.WriteLine($"Item {score} already processed, rolling back before reapplying");

                        // if required, we can rollback any previous version of processing then reapply with the latest.
                        byte version = item.ProcessHistory.processed_version;

                        if (version >= 1)
                            userStats.playcount--;

                        if (version >= 2)
                            adjustStatisticsFromScore(score, userStats, true);
                    }

                    userStats.playcount++;

                    adjustStatisticsFromScore(score, userStats);

                    updateUserStats(userStats, db, transaction);

                    updateHistoryEntry(item, db, transaction);

                    transaction.Commit();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }

        private static void adjustStatisticsFromScore(SoloScore score, UserStats userStats, bool revert = false)
        {
            int multiplier = revert ? -1 : 1;

            foreach (var (result, count) in score.statistics)
            {
                switch (result)
                {
                    case HitResult.Miss:
                        userStats.countMiss += multiplier * count;
                        break;

                    case HitResult.Meh:
                        userStats.count50 += multiplier * count;
                        break;

                    case HitResult.Ok:
                    case HitResult.Good:
                        userStats.count100 += multiplier * count;
                        break;

                    case HitResult.Great:
                    case HitResult.Perfect:
                        userStats.count300 += multiplier * count;
                        break;
                }
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
