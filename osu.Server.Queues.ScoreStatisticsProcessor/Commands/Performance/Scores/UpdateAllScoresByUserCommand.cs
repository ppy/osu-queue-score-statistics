// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
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
        private const int max_users_per_query = 1000;

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

            ulong? totalUsers;
            ulong totalScores = 0;
            double rate = 0;
            Stopwatch sw = new Stopwatch();

            Console.WriteLine("Fetching users all users...");

            using (var db = DatabaseAccess.GetConnection())
            {
                totalUsers = await db.QuerySingleAsync<ulong?>($"SELECT COUNT(`user_id`) FROM {databaseInfo.UserStatsTable} WHERE `user_id` >= @UserId", new
                {
                    UserId = currentUserId
                }, commandTimeout: 300000);

                if (totalUsers == null)
                    throw new InvalidOperationException("Could not find user ID count.");
            }

            Console.WriteLine($"Processing all {totalUsers:N0} users starting from UserID {currentUserId:N0}");

            int processedUsers = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                sw.Restart();

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
                    using (var transaction = await db.BeginTransactionAsync(cancellationToken))
                    {
                        Interlocked.Add(ref totalScores, (ulong)await ScoreProcessor.ProcessUserScoresAsync(userId, RulesetId, db, transaction, cancellationToken));
                        await transaction.CommitAsync(cancellationToken);
                    }
                }, cancellationToken);

                currentUserId = userIds.Max();
                processedUsers += userIds.Length;

                if (rate == 0)
                    rate = ((double)userIds.Length / sw.ElapsedMilliseconds * 1000);
                else
                    rate = rate * 0.95 + 0.05 * ((double)userIds.Length / sw.ElapsedMilliseconds * 1000);

                Console.WriteLine(ScoreProcessor.BeatmapStore?.GetCacheStats());
                Console.WriteLine($"id: {currentUserId:N0} changed scores: {totalScores:N0} ({processedUsers:N0} of {totalUsers:N0} {(float)processedUsers / totalUsers:P1}) {rate:N0}/s");

                using (var db = DatabaseAccess.GetConnection())
                    await DatabaseHelper.SetCountAsync(databaseInfo.LastProcessedPpUserCount, currentUserId, db);
            }

            return 0;
        }
    }
}
