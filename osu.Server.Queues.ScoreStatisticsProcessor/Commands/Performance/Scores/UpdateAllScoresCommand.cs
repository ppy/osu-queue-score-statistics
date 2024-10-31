// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using MySqlConnector;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Performance.Scores
{
    [Command(Name = "all", Description = "Computes pp of all scores from all users.")]
    public class UpdateAllScoresCommand : PerformanceCommand
    {
        [Option(Description = "The size of each batch, which is then distributed to threads.")]
        public int BatchSize { get; set; } = 1000;

        [Option(Description = "Process from the newest score backwards.", ShortName = "bb")]
        public bool Backwards { get; set; }

        [Option(Description = "Score ID to start processing from.")]
        public ulong From { get; set; }

        [Option(Description = "The minimum PP of a score to reprocess.", LongName = "min-pp", ShortName = "p1")]
        public float MinPP { get; set; } = 0;

        [Option(Description = "The maximum PP of a score to reprocess.", LongName = "max-pp", ShortName = "pu")]
        public float MaxPP { get; set; } = float.MaxValue;

        /// <summary>
        /// Whether to adjust processing rate based on slave latency. Defaults to <c>false</c>.
        /// </summary>
        [Option(CommandOptionType.SingleOrNoValue, Template = "--check-slave-latency")]
        public bool CheckSlaveLatency { get; set; }

        /// <summary>
        /// We have a lot of connection churn. Retrieving connections from the pool is expensive at this point.
        /// Managing our own connection pool doubles throughput.
        /// </summary>
        private readonly ConcurrentQueue<MySqlConnection> connections = new ConcurrentQueue<MySqlConnection>();

        protected override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
        {
            // TODO: ruleset parameter is in base class but unused.

            using var db = DatabaseAccess.GetConnection();

            ulong currentScoreId = From;
            ulong? lastScoreId = await db.QuerySingleAsync<ulong>("SELECT MAX(id) FROM scores");

            ulong processedCount = 0;
            ulong changedPp = 0;
            double rate = 0;
            Stopwatch sw = new Stopwatch();

            string sort = Backwards ? "DESC" : "ASC";

            for (int i = 0; i < Threads; i++)
                connections.Enqueue(DatabaseAccess.GetConnection());

            Console.WriteLine(Backwards
                ? $"Processing all scores down from {lastScoreId}, starting from {currentScoreId}"
                : $"Processing all scores up to {lastScoreId}, starting from {currentScoreId}");

            while (!cancellationToken.IsCancellationRequested)
            {
                sw.Restart();

                if (CheckSlaveLatency)
                {
                    using (var connection = DatabaseAccess.GetConnection())
                        await SlaveLatencyChecker.CheckSlaveLatency(connection, cancellationToken);

                    if (cancellationToken.IsCancellationRequested)
                        break;
                }

                var scores = (await db.QueryAsync<SoloScore>(
                    $"SELECT * FROM scores WHERE `id` > @ScoreId AND `id` <= @LastScoreId AND `pp` BETWEEN @minPP AND @maxPP ORDER BY `id` {sort} LIMIT @limit", new
                    {
                        ScoreId = currentScoreId,
                        LastScoreId = lastScoreId,
                        minPP = MinPP,
                        maxPP = MaxPP,
                        limit = BatchSize
                    })).ToList();

                if (scores.Count == 0)
                    break;

                await Task.WhenAll(Partitioner.Create(scores).GetPartitions(Threads).Select(async partition =>
                {
                    connections.TryDequeue(out var connection);

                    using (var transaction = await connection!.BeginTransactionAsync(IsolationLevel.ReadUncommitted, cancellationToken))
                    using (partition)
                    {
                        while (partition.MoveNext())
                        {
                            if (cancellationToken.IsCancellationRequested)
                                return;

                            bool changed = await ScoreProcessor.ProcessScoreAsync(partition.Current, connection, transaction);

                            if (changed)
                                Interlocked.Increment(ref changedPp);
                        }

                        await transaction.CommitAsync(cancellationToken);
                    }

                    connections.Enqueue(connection);
                }));

                if (cancellationToken.IsCancellationRequested)
                    return -1;

                Interlocked.Add(ref processedCount, (ulong)scores.Count);

                currentScoreId = scores.Last().id;

                if (rate == 0)
                    rate = ((double)scores.Count / sw.ElapsedMilliseconds * 1000);
                else
                    rate = rate * 0.95 + 0.05 * ((double)scores.Count / sw.ElapsedMilliseconds * 1000);

                Console.WriteLine(ScoreProcessor.BeatmapStore?.GetCacheStats());
                Console.WriteLine($"processed up to: {currentScoreId} changed: {changedPp:N0} {(float)(currentScoreId - From) / (lastScoreId - From):P1} {rate:N0}/s");
            }

            return 0;
        }
    }
}
