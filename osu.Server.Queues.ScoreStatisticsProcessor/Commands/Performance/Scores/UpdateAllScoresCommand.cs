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
using osu.Server.Queues.ScoreStatisticsProcessor.Stores;

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
        public float? MinPP { get; set; }

        [Option(Description = "The maximum PP of a score to reprocess.", LongName = "max-pp", ShortName = "pu")]
        public float? MaxPP { get; set; }

        [Option(Description = "Optional where clause", Template = "--where")]
        public string Where { get; set; } = "1 = 1";

        /// <summary>
        /// Whether to push changed scores to the ES indexing queue.
        /// </summary>
        [Option(CommandOptionType.SingleOrNoValue, Template = "--run-indexing")]
        public bool RunIndexing { get; set; }

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

        private readonly ElasticQueuePusher elasticQueueProcessor = new ElasticQueuePusher();

        private readonly ConcurrentBag<ElasticQueuePusher.ElasticScoreItem> elasticItems = new ConcurrentBag<ElasticQueuePusher.ElasticScoreItem>();

        protected override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
        {
            // TODO: ruleset parameter is in base class but unused.

            using var db = await DatabaseAccess.GetConnectionAsync(cancellationToken);

            ulong currentScoreId = From;
            ulong? lastScoreId = await db.QuerySingleAsync<ulong>("SELECT MAX(id) FROM scores");

            ulong processedCount = 0;
            ulong changedPp = 0;
            double rate = 0;
            Stopwatch sw = new Stopwatch();

            if (Backwards)
            {
                currentScoreId = lastScoreId.Value;
                lastScoreId = From;
            }

            for (int i = 0; i < Threads; i++)
                connections.Enqueue(await DatabaseAccess.GetConnectionAsync(cancellationToken));

            Console.WriteLine(Backwards
                ? $"Processing all scores down from {currentScoreId}, ending at {lastScoreId}"
                : $"Processing all scores up to {lastScoreId}, starting from {currentScoreId}");

            while (!cancellationToken.IsCancellationRequested)
            {
                sw.Restart();

                handleInput();

                if (CheckSlaveLatency)
                {
                    using (var connection = await DatabaseAccess.GetConnectionAsync(cancellationToken))
                        await SlaveLatencyChecker.CheckSlaveLatency(connection, cancellationToken);

                    if (cancellationToken.IsCancellationRequested)
                        break;
                }

                string ppCondition = MinPP != null || MaxPP != null
                    ? $"AND `pp` BETWEEN {MinPP ?? 0} AND {MaxPP ?? 1048576}"
                    : string.Empty;

                var scores = (await db.QueryAsync<SoloScore>(
                    Backwards
                        ? $"SELECT * FROM scores WHERE `id` <= @CurrentScoreId AND `id` >= @LastScoreId {ppCondition} AND ranked = 1 AND preserve = 1 AND {Where} ORDER BY `id` DESC LIMIT @limit"
                        : $"SELECT * FROM scores WHERE `id` >= @CurrentScoreId AND `id` <= @LastScoreId {ppCondition} AND ranked = 1 AND preserve = 1 AND {Where} ORDER BY `id` LIMIT @limit",
                    new
                    {
                        CurrentScoreId = currentScoreId,
                        LastScoreId = lastScoreId,
                        limit = BatchSize
                    }, commandTimeout: 600)).ToList();

                if (scores.Count == 0)
                    break;

                elasticItems.Clear();

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

                            try
                            {
                                bool changed = await ScoreProcessor.ProcessScoreAsync(partition.Current, connection, transaction);

                                if (changed)
                                {
                                    Interlocked.Increment(ref changedPp);
                                    elasticItems.Add(new ElasticQueuePusher.ElasticScoreItem { ScoreId = (long?)partition.Current.id });
                                }
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine($"Failed to process score {partition.Current.id}: {e}");
                            }
                        }

                        await transaction.CommitAsync(cancellationToken);
                    }

                    connections.Enqueue(connection);
                }));

                if (cancellationToken.IsCancellationRequested)
                    return -1;

                if (RunIndexing && elasticItems.Count > 0)
                {
                    elasticQueueProcessor.PushToQueue(elasticItems.ToList());
                    Console.WriteLine($"Queued {elasticItems.Count} items for indexing");
                }

                Interlocked.Add(ref processedCount, (ulong)scores.Count);

                currentScoreId = scores.Last().id + 1;

                if (rate == 0)
                    rate = ((double)scores.Count / sw.ElapsedMilliseconds * 1000);
                else
                    rate = rate * 0.95 + 0.05 * ((double)scores.Count / sw.ElapsedMilliseconds * 1000);

                Console.WriteLine(BeatmapStore.GetCacheStats());
                Console.WriteLine($"processed up to: {currentScoreId} changed: {changedPp:N0} {(float)(currentScoreId - From) / (lastScoreId - From):P1} {rate:N0}/s");
            }

            return 0;
        }

        private void handleInput()
        {
            if (!Environment.UserInteractive || Console.IsInputRedirected)
                return;

            if (!Console.KeyAvailable)
                return;

            ConsoleKeyInfo key = Console.ReadKey(true);

            switch (key.Key)
            {
                case ConsoleKey.A:
                {
                    int before = BatchSize;
                    BatchSize = Math.Max(500, BatchSize - 500);
                    Console.WriteLine($"!! DECREASING BATCH SIZE {before} => {BatchSize}");
                    break;
                }

                case ConsoleKey.S:
                {
                    int before = BatchSize;
                    BatchSize += 500;
                    Console.WriteLine($"!! INCREASING BATCH SIZE {before} => {BatchSize}");
                    break;
                }

                case ConsoleKey.Z:
                {
                    int before = Threads;
                    Threads = Math.Max(1, Threads - 2);
                    Console.WriteLine($"!! DECREASING THREAD COUNT {before} => {Threads}");

                    for (int i = 0; i < 2; i++)
                    {
                        if (Threads > connections.Count)
                        {
                            connections.TryDequeue(out var connection);
                            connection!.Dispose();
                        }
                    }

                    break;
                }

                case ConsoleKey.X:
                {
                    int before = Threads;
                    Threads += 2;
                    Console.WriteLine($"!! INCREASING THREAD COUNT {before} => {Threads}");

                    for (int i = 0; i < 2; i++)
                        connections.Enqueue(DatabaseAccess.GetConnection());
                    break;
                }
            }
        }
    }
}
