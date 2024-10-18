// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Performance.Scores
{
    [Command(Name = "all-users", Description = "Computes pp of all scores from all users.")]
    public class UpdateAllScoresByUserCommand : PerformanceCommand
    {
        private const int max_users_per_query = 10000;

        [Option(Description = "Continue where a previously aborted 'all' run left off.")]
        public bool Continue { get; set; }

        protected override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
        {
            LegacyDatabaseHelper.RulesetDatabaseInfo databaseInfo = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId);

            long currentUserId;

            using (var db = DatabaseAccess.GetConnection())
            {
                if (Continue)
                    currentUserId = await DatabaseHelper.GetCountAsync(databaseInfo.LastProcessedPpUserCount, db);
                else
                {
                    currentUserId = 0;
                    await DatabaseHelper.SetCountAsync(databaseInfo.LastProcessedPpUserCount, 0, db);
                }
            }

            ulong? totalUsers = 0;
            ulong totalScores = 0;

            using (var db = DatabaseAccess.GetConnection())
            {
                totalUsers = await db.QuerySingleAsync<ulong?>($"SELECT COUNT(`user_id`) FROM {databaseInfo.UserStatsTable} WHERE `user_id` >= @UserId", new
                {
                    UserId = currentUserId
                });

                if (totalUsers == null)
                    throw new InvalidOperationException("Could not find user ID count.");
            }

            Console.WriteLine($"Processing all users starting from UserID {currentUserId}");

            int processedUsers = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                uint[] userIds;

                using (var db = DatabaseAccess.GetConnection())
                {
                    userIds = (await db.QueryAsync<uint>($"SELECT `user_id` FROM {databaseInfo.UserStatsTable} WHERE `user_id` > @UserId ORDER BY `user_id` LIMIT @Limit", new
                    {
                        UserId = currentUserId,
                        Limit = max_users_per_query
                    })).ToArray();
                }

                if (userIds.Length == 0)
                    break;

                await ProcessPartitioned(userIds, async userId =>
                {
                    using (var db = DatabaseAccess.GetConnection())
                        Interlocked.Add(ref totalScores, (ulong)(await ScoreProcessor.ProcessUserScoresAsync(userId, RulesetId, db, cancellationToken: cancellationToken)));

                    if (Interlocked.Increment(ref processedUsers) % 1000 == 0)
                        Console.WriteLine($"Processed {processedUsers:N0} of {totalUsers:N0} ({totalScores:N0} scores)");
                }, cancellationToken);

                currentUserId = userIds.Max();

                using (var db = DatabaseAccess.GetConnection())
                    await DatabaseHelper.SetCountAsync(databaseInfo.LastProcessedPpUserCount, currentUserId, db);
            }

            return 0;
        }
    }
}
