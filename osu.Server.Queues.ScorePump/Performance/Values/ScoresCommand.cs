// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump.Performance.Values
{
    [Command("scores", Description = "Computes pp of specific scores.")]
    public class ScoresCommand : PerformanceCommand
    {
        [UsedImplicitly]
        [Required]
        [Argument(0, Description = "A space-separated list of scores to compute PP for.")]
        public ulong[] ScoreIds { get; set; } = null!;

        protected override async Task<int> ExecuteAsync(CommandLineApplication app)
        {
            Console.WriteLine($"Processed 0 of {ScoreIds.Length}");

            int processedCount = 0;

            await ProcessPartitioned(ScoreIds, async id =>
            {
                await Processor.ProcessScoreAsync(id);
                Console.WriteLine($"Processed {Interlocked.Increment(ref processedCount)} of {ScoreIds.Length}");
            });

            return 0;
        }
    }
}
