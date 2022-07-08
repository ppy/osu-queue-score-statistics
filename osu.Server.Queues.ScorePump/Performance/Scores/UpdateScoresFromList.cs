// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump.Performance.Scores
{
    [Command("list", Description = "Computes pp of specific scores.")]
    public class UpdateScoresFromList : PerformanceCommand
    {
        [UsedImplicitly]
        [Required]
        [Argument(0, Description = "A comma-separated list of scores to compute PP for.")]
        public string ScoresString { get; set; } = string.Empty;

        protected override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
        {
            ulong[] scoreIds = ParseLongIds(ScoresString);

            Console.WriteLine($"Processing {scoreIds.Length} provided scores");

            int processedCount = 0;

            await ProcessPartitioned(scoreIds, async id =>
            {
                using (var db = Queue.GetDatabaseConnection())
                    await ScoreProcessor.ProcessScoreAsync(id, db);
                Console.WriteLine($"Processed {Interlocked.Increment(ref processedCount)} of {scoreIds.Length}");
            }, cancellationToken);

            return 0;
        }
    }
}
