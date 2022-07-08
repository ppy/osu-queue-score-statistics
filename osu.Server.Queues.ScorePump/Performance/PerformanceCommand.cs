// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScorePump.Queue;
using osu.Server.Queues.ScoreStatisticsProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Processors;

namespace osu.Server.Queues.ScorePump.Performance
{
    public abstract class PerformanceCommand : QueueCommand
    {
        protected ScorePerformanceProcessor ScoreProcessor { get; private set; } = null!;
        protected UserTotalPerformanceProcessor TotalProcessor { get; private set; } = null!;

        [Option(CommandOptionType.SingleValue, Template = "-r|--ruleset", Description = "The ruleset to process score for.")]
        public int RulesetId { get; set; }

        [Option(Description = "Number of threads to use.")]
        public int Threads { get; set; } = 1;

        public virtual async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            ScoreProcessor = new ScorePerformanceProcessor();
            TotalProcessor = new UserTotalPerformanceProcessor();
            return await ExecuteAsync(cancellationToken);
        }

        protected abstract Task<int> ExecuteAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Parses a comma-separated list of IDs from a given input string.
        /// </summary>
        /// <param name="input">The input string.</param>
        /// <returns>The IDs.</returns>
        protected static int[] ParseIntIds(string input) => input.Split(',').Select(int.Parse).ToArray();

        /// <summary>
        /// Parses a comma-separated list of IDs from a given input string.
        /// </summary>
        /// <param name="input">The input string.</param>
        /// <returns>The IDs.</returns>
        protected static ulong[] ParseLongIds(string input) => input.Split(',').Select(ulong.Parse).ToArray();

        protected async Task ProcessUserTotals(int[] userIds, CancellationToken cancellationToken)
        {
            if (userIds.Length == 0)
            {
                Console.WriteLine("No matching users to process!");
                return;
            }

            Console.WriteLine($"Processing user totals for {userIds.Length} users");

            int processedCount = 0;

            await ProcessPartitioned(userIds, async userId =>
            {
                using (var db = Queue.GetDatabaseConnection())
                {
                    var userStats = await DatabaseHelper.GetUserStatsAsync(userId, RulesetId, db);

                    // Only process users with an existing rank_score.
                    if (userStats!.rank_score == 0)
                        return;

                    await TotalProcessor.UpdateUserStatsAsync(userStats, RulesetId, db);
                    await DatabaseHelper.UpdateUserStatsAsync(userStats, db);
                }

                Console.WriteLine($"Processed {Interlocked.Increment(ref processedCount)} of {userIds.Length}");
            }, cancellationToken);
        }

        protected async Task ProcessUserScores(int[] userIds, CancellationToken cancellationToken)
        {
            if (userIds.Length == 0)
            {
                Console.WriteLine("No matching users to process!");
                return;
            }

            Console.WriteLine($"Processing user scores for {userIds.Length} users");

            int processedCount = 0;

            await ProcessPartitioned(userIds, async userId =>
            {
                using (var db = Queue.GetDatabaseConnection())
                    await ScoreProcessor.ProcessUserScoresAsync(userId, RulesetId, db);
                Console.WriteLine($"Processed {Interlocked.Increment(ref processedCount)} of {userIds.Length}");
            }, cancellationToken);
        }

        protected async Task ProcessPartitioned<T>(IEnumerable<T> values, Func<T, Task> processFunc, CancellationToken cancellationToken)
        {
            await Task.WhenAll(Partitioner
                               .Create(values)
                               .GetPartitions(Threads)
                               .AsParallel()
                               .Select(processPartition));

            async Task processPartition(IEnumerator<T> partition)
            {
                using (partition)
                {
                    while (partition.MoveNext())
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        await processFunc(partition.Current);
                    }
                }
            }
        }
    }
}
