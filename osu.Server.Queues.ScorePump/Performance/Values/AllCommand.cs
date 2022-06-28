// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        private int? totalCount;
        private int processedCount;

        protected override async Task<int> Execute(CommandLineApplication app)
        {
            LegacyDatabaseHelper.RulesetDatabaseInfo databaseInfo = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId);

            long currentUserId;

            if (Continue)
                currentUserId = await Processor.GetCount(databaseInfo.LastProcessedPpUserCount);
            else
            {
                currentUserId = 0;
                await Processor.SetCount(databaseInfo.LastProcessedPpUserCount, 0);
            }

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

                await processUsers(users);

                currentUserId = Math.Max(currentUserId, users.Max());

                await Processor.SetCount(databaseInfo.LastProcessedPpUserCount, currentUserId);
            }

            return 0;
        }

        private async Task processUsers(IEnumerable<uint> userIds)
        {
            await Task.WhenAll(Partitioner
                               .Create(userIds)
                               .GetPartitions(Threads)
                               .AsParallel()
                               .Select(processPartition));

            async Task processPartition(IEnumerator<uint> partition)
            {
                using (partition)
                {
                    while (partition.MoveNext())
                    {
                        await Task.Yield();

                        await Processor.ProcessUser(partition.Current, RulesetId);

                        Console.WriteLine($"Processed {Interlocked.Increment(ref processedCount)} of {totalCount}");
                    }
                }
            }
        }
    }
}
