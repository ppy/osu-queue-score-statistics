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
    public class AllCommand : PerformanceCommand
    {
        private const int max_users_per_query = 10000;

        [Option(Description = "Continue where a previously aborted 'all' run left off.")]
        public bool Continue { get; set; }

        [Option(CommandOptionType.SingleValue, Template = "-r|--ruleset", Description = "The ruleset to process score for.")]
        public int RulesetId { get; set; }

        protected override async Task<int> ExecuteAsync(CommandLineApplication app)
        {
            LegacyDatabaseHelper.RulesetDatabaseInfo databaseInfo = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId);

            long currentUserId;

            if (Continue)
                currentUserId = await Processor.GetCountAsync(databaseInfo.LastProcessedPpUserCount);
            else
            {
                currentUserId = 0;
                await Processor.SetCountAsync(databaseInfo.LastProcessedPpUserCount, 0);
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

            while (true)
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
                    await Processor.ProcessUserAsync(id, RulesetId);
                    Console.WriteLine($"Processed {Interlocked.Increment(ref processedCount)} of {totalCount}");
                });

                currentUserId = Math.Max(currentUserId, users.Max());

                await Processor.SetCountAsync(databaseInfo.LastProcessedPpUserCount, currentUserId);
            }

            return 0;
        }
    }
}
