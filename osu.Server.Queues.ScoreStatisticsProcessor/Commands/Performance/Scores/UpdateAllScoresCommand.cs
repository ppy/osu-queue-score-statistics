// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Performance.Scores
{
    [Command(Name = "all", Description = "Computes pp of all scores from all users.")]
    public class UpdateAllScoresCommand : PerformanceCommand
    {
        private const int max_scores_per_query = 5000;

        [Option(Description = "Process from the newest score backwards.")]
        public bool Backwards { get; set; }

        [Option(Description = "Score ID to start processing from.")]
        public ulong From { get; set; }

        /// <summary>
        /// Whether to adjust processing rate based on slave latency. Defaults to <c>false</c>.
        /// </summary>
        [Option(CommandOptionType.SingleOrNoValue, Template = "--check-slave-latency")]
        public bool CheckSlaveLatency { get; set; }

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

            var scoresQuery = db.Query<SoloScore>($"SELECT * FROM scores WHERE `id` > @ScoreId AND `id` <= @LastScoreId ORDER BY `id` {sort}", new
            {
                ScoreId = currentScoreId,
                LastScoreId = lastScoreId,
            }, buffered: false);

            using var scoresEnum = scoresQuery.GetEnumerator();

            Console.WriteLine(Backwards
                ? $"Processing all scores down from {lastScoreId}, starting from {currentScoreId}"
                : $"Processing all scores up to {lastScoreId}, starting from {currentScoreId}");

            Task<List<SoloScore>> nextScores = getNextScores();

            while (!cancellationToken.IsCancellationRequested)
            {
                if (CheckSlaveLatency)
                {
                    using (var connection = DatabaseAccess.GetConnection())
                        await SlaveLatencyChecker.CheckSlaveLatency(connection, cancellationToken);
                }

                sw.Restart();

                var scores = await nextScores;
                nextScores = getNextScores();

                if (scores.Count == 0)
                    break;

                await Task.WhenAll(Partitioner.Create(scores).GetPartitions(Threads).Select(async partition =>
                {
                    using (var connection = DatabaseAccess.GetConnection())
                    using (var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadUncommitted, cancellationToken))
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
                Console.WriteLine($"processed up to: {currentScoreId:N0} changed: {changedPp:N0} {(float)processedCount / (lastScoreId - From):P1} {rate:N0}/s");
            }

            return 0;

            Task<List<SoloScore>> getNextScores() => Task.Run(() =>
            {
                List<SoloScore> scores = new List<SoloScore>(max_scores_per_query);

                for (int i = 0; i < max_scores_per_query && scoresEnum.MoveNext(); i++)
                    scores.Add(scoresEnum.Current);

                return scores;
            }, cancellationToken);
        }
    }
}
