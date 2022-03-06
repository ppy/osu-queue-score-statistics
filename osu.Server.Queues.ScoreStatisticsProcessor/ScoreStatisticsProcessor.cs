// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dapper;
using Dapper.Contrib.Extensions;
using MySqlConnector;
using osu.Game.Rulesets;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    public class ScoreStatisticsProcessor : QueueProcessor<ScoreItem>
    {
        /// <summary>
        /// version 1: basic playcount
        /// version 2: total score, hit statistics, beatmap playcount, monthly playcount, max combo
        /// version 3: fixed incorrect revert condition for beatmap/monthly playcount
        /// version 4: uses SoloScore"V2" (moving all content to json data block)
        /// version 5: added performance processor
        /// version 6: added play time processor
        /// </summary>
        public const int VERSION = 6;

        public static readonly List<Ruleset> AVAILABLE_RULESETS = getRulesets();

        private readonly List<IProcessor> processors = new List<IProcessor>();

        public ScoreStatisticsProcessor()
            : base(new QueueConfiguration { InputQueueName = "score-statistics" })
        {
            DapperExtensions.InstallDateTimeOffsetMapper();

            // add each processor automagically.
            foreach (var t in typeof(ScoreStatisticsProcessor).Assembly.GetTypes().Where(t => !t.IsInterface && typeof(IProcessor).IsAssignableFrom(t)))
            {
                if (Activator.CreateInstance(t) is IProcessor processor)
                    processors.Add(processor);
            }
        }

        protected override void ProcessResult(ScoreItem item)
        {
            if (item.ProcessHistory?.processed_version == VERSION)
            {
                DogStatsd.Increment("total_skipped");
                return;
            }

            try
            {
                using (var conn = GetDatabaseConnection())
                {
                    var scoreRow = item.Score;
                    var score = scoreRow.ScoreInfo;

                    using (var transaction = conn.BeginTransaction())
                    {
                        var userStats = GetUserStats(score, conn, transaction);

                        if (userStats == null)
                            // ruleset could be invalid
                            // TODO: add check in client and server to not submit unsupported rulesets
                            return;

                        // if required, we can rollback any previous version of processing then reapply with the latest.
                        if (item.ProcessHistory != null)
                        {
                            DogStatsd.Increment("total_upgraded");
                            byte version = item.ProcessHistory.processed_version;

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
        /// <returns>The retrieved user stats. Null if the ruleset or user was not valid.</returns>
        public static UserStats? GetUserStats(SoloScoreInfo score, MySqlConnection db, MySqlTransaction? transaction = null)
        {
            switch (score.ruleset_id)
            {
                default:
                    Console.WriteLine($"Item {score} is for an unsupported ruleset {score.ruleset_id}");
                    return null;

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

        private static T getUserStats<T>(SoloScoreInfo score, MySqlConnection db, MySqlTransaction? transaction = null)
            where T : UserStats, new()
        {
            var dbInfo = LegacyDatabaseHelper.GetRulesetSpecifics(score.ruleset_id);

            // for simplicity, let's ensure the row already exists as a separate step.
            var userStats = db.QuerySingleOrDefault<T>($"SELECT * FROM {dbInfo.UserStatsTable} WHERE user_id = @user_id FOR UPDATE", score, transaction);

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

        private static List<Ruleset> getRulesets()
        {
            const string ruleset_library_prefix = "osu.Game.Rulesets";

            var rulesetsToProcess = new List<Ruleset>();

            foreach (string file in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, $"{ruleset_library_prefix}.*.dll"))
            {
                try
                {
                    var assembly = Assembly.LoadFrom(file);
                    Type type = assembly.GetTypes().First(t => t.IsPublic && t.IsSubclassOf(typeof(Ruleset)));
                    rulesetsToProcess.Add((Ruleset)Activator.CreateInstance(type)!);
                }
                catch
                {
                    throw new Exception($"Failed to load ruleset ({file})");
                }
            }

            return rulesetsToProcess;
        }
    }
}
