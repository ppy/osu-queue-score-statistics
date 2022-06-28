// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScorePump.Performance
{
    [Command("scores", Description = "Computes pp of specific scores.")]
    public class ScoresCommand : PerformanceCommand
    {
        [Required]
        [Argument(0, Description = "A space-separated list of scores to compute PP for.")]
        public ulong[] ScoreIds { get; set; } = null!;

        public async Task<int> OnExecuteAsync(CommandLineApplication app)
        {
            int processedCount = 0;

            await Task.WhenAll(Partitioner
                               .Create(ScoreIds)
                               .GetPartitions(Threads)
                               .AsParallel()
                               .Select(processPartition));

            async Task processPartition(IEnumerator<ulong> partition)
            {
                using (partition)
                {
                    while (partition.MoveNext())
                    {
                        await Task.Yield();

                        SoloScore? score;

                        using (var db = Queue.GetDatabaseConnection())
                        {
                            score = await db.QuerySingleOrDefaultAsync<SoloScore>($"SELECT * FROM {SoloScore.TABLE_NAME} WHERE `id` = @ScoreId", new
                            {
                                ScoreId = partition.Current
                            });
                        }

                        if (score == null)
                        {
                            await Console.Error.WriteLineAsync($"Could not find score ID {partition.Current}.");
                            continue;
                        }

                        await ProcessScore(score);

                        Console.WriteLine($"Processed {Interlocked.Increment(ref processedCount)} of {ScoreIds.Length}");
                    }
                }
            }

            return 0;
        }
    }
}
