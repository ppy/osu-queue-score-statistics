// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor;

namespace osu.Server.Queues.ScorePump.Performance.Scores
{
    [Command(Name = "all", Description = "Computes pp of all scores from all users.")]
    public class UpdateAllScores : PerformanceCommand
    {
        private const int max_users_per_query = 10000;

        [Option(Description = "Continue where a previously aborted 'all' run left off.")]
        public bool Continue { get; set; }

        protected override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
        {
            LegacyDatabaseHelper.RulesetDatabaseInfo databaseInfo = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId);

            long currentUserId;

            using (var db = Queue.GetDatabaseConnection())
            {
                if (Continue)
                    currentUserId = await DatabaseHelper.GetCountAsync(databaseInfo.LastProcessedPpUserCount, db);
                else
                {
                    currentUserId = 0;
                    await DatabaseHelper.SetCountAsync(databaseInfo.LastProcessedPpUserCount, 0, db);
                }
            }

            int? totalCount;

            using (var db = Queue.GetDatabaseConnection())
            {
                totalCount = await db.QuerySingleAsync<int?>($"SELECT COUNT(`user_id`) FROM {databaseInfo.UserStatsTable} WHERE `user_id` >= @UserId", new
                {
                    UserId = currentUserId
                });

                if (totalCount == null)
                    throw new InvalidOperationException("Could not find user ID count.");
            }

            Console.WriteLine($"Processing all users starting from UserID {currentUserId}");

            int processedCount = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                int[] userIds;

                using (var db = Queue.GetDatabaseConnection())
                {
                    userIds = (await db.QueryAsync<int>($"SELECT `user_id` FROM {databaseInfo.UserStatsTable} WHERE `user_id` > @UserId ORDER BY `user_id` LIMIT @Limit", new
                    {
                        UserId = currentUserId,
                        Limit = max_users_per_query
                    })).ToArray();
                }

                if (userIds.Length == 0)
                    break;

                await ProcessPartitioned(userIds, async userId =>
                {
                    using (var db = Queue.GetDatabaseConnection())
                        await ScoreProcessor.ProcessUserScoresAsync(userId, RulesetId, db);
                    Console.WriteLine($"Processed {Interlocked.Increment(ref processedCount)} of {totalCount}");
                }, cancellationToken);

                currentUserId = userIds.Max();

                using (var db = Queue.GetDatabaseConnection())
                    await DatabaseHelper.SetCountAsync(databaseInfo.LastProcessedPpUserCount, currentUserId, db);
            }

            return 0;
        }
    }
}
