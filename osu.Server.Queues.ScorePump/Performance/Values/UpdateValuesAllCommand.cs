// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor;

namespace osu.Server.Queues.ScorePump.Performance.Values
{
    [Command(Name = "all", Description = "Computes pp of all users.")]
    public class UpdateValuesAllCommand : PerformanceCommand
    {
        private const int max_users_per_query = 10000;

        [Option(Description = "Continue where a previously aborted 'all' run left off.")]
        public bool Continue { get; set; }

        [Option(CommandOptionType.SingleValue, Template = "-r|--ruleset", Description = "The ruleset to process score for.")]
        public int RulesetId { get; set; }

        protected override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
        {
            LegacyDatabaseHelper.RulesetDatabaseInfo databaseInfo = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId);

            long currentUserId;

            using (var db = Queue.GetDatabaseConnection())
            {
                if (Continue)
                    currentUserId = await Processor.GetCountAsync(databaseInfo.LastProcessedPpUserCount, db);
                else
                {
                    currentUserId = 0;
                    await Processor.SetCountAsync(databaseInfo.LastProcessedPpUserCount, 0, db);
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

            Console.WriteLine($"Processing all users with ID larger than {currentUserId}");
            Console.WriteLine($"Processed 0 of {totalCount}");

            int processedCount = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                uint[] users;

                using (var db = Queue.GetDatabaseConnection())
                {
                    users = (await db.QueryAsync<uint>($"SELECT `user_id` FROM {databaseInfo.UserStatsTable} WHERE `user_id` > @UserId ORDER BY `user_id` LIMIT @Limit", new
                    {
                        UserId = currentUserId,
                        Limit = max_users_per_query
                    })).ToArray();
                }

                if (users.Length == 0)
                    break;

                await ProcessPartitioned(users, async id =>
                {
                    using (var db = Queue.GetDatabaseConnection())
                        await Processor.ProcessUserScoresAsync(id, RulesetId, db);
                    Console.WriteLine($"Processed {Interlocked.Increment(ref processedCount)} of {totalCount}");
                }, cancellationToken);

                currentUserId = Math.Max(currentUserId, users.Max());

                using (var db = Queue.GetDatabaseConnection())
                    await Processor.SetCountAsync(databaseInfo.LastProcessedPpUserCount, currentUserId, db);
            }

            return 0;
        }
    }
}
