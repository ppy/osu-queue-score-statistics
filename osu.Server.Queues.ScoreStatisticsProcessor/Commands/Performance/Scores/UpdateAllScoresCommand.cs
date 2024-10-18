// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Performance.Scores
{
    [Command(Name = "all", Description = "Computes pp of all scores from all users.")]
    public class UpdateAllScoresCommand : PerformanceCommand
    {
        private const int max_scores_per_query = 5000;

        [Option(Description = "Score ID to start processing from.")]
        public ulong From { get; set; }

        protected override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
        {
            using var db = DatabaseAccess.GetConnection();

            ulong currentScoreId = From;
            ulong? totalCount = await db.QuerySingleAsync<ulong>("SELECT MAX(id) FROM scores");

            ulong processedCount = 0;
            double rate = 0;
            Stopwatch sw = new Stopwatch();

            var scoresQuery = db.Query<SoloScore>("SELECT * FROM scores WHERE `id` > @ScoreId ORDER BY `id`", new { ScoreId = currentScoreId }, buffered: false);
            using var scoresEnum = scoresQuery.GetEnumerator();

            Console.WriteLine($"Processing all {totalCount} scores starting from {currentScoreId}");

            Task<List<SoloScore>> nextScores = getNextScores();

            while (!cancellationToken.IsCancellationRequested)
            {
                sw.Restart();

                var scores = await nextScores;
                nextScores = getNextScores();

                if (scores.Count == 0)
                    break;

                await Task.WhenAll(Partitioner.Create(scores).GetPartitions(Threads).Select(async partition =>
                {
                    using (var connection = DatabaseAccess.GetConnection())
                    using (partition)
                    {
                        while (partition.MoveNext())
                        {
                            if (cancellationToken.IsCancellationRequested)
                                return;

                            await ScoreProcessor.ProcessScoreAsync(partition.Current, connection);
                        }
                    }
                }));

                Interlocked.Add(ref processedCount, (ulong)scores.Count);

                currentScoreId = scores.Last().id;

                double elapsed = sw.ElapsedMilliseconds;

                if (rate == 0)
                    rate = ((double)scores.Count / sw.ElapsedMilliseconds * 1000);
                else
                    rate = rate * 0.95 + 0.05 * ((double)scores.Count / sw.ElapsedMilliseconds * 1000);

                Console.WriteLine(ScoreProcessor.BeatmapStore?.GetCacheStats());
                Console.WriteLine($"id: {currentScoreId:N0} ({processedCount:N0} of {totalCount:N0} {(float)processedCount / totalCount:P1}) {rate:N0}/s");
            }

            return 0;

            Task<List<SoloScore>> getNextScores() => Task.Run(() =>
            {
                List<SoloScore> scores = new List<SoloScore>();

                scores.Clear();

                for (int i = 0; i < max_scores_per_query && scoresEnum.MoveNext(); i++)
                    scores.Add(scoresEnum.Current);

                return scores;
            }, cancellationToken);
        }
    }
}
