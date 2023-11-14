// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Queue
{
    [Command("watch", Description = "Begins processing of the queue.")]
    public class WatchQueueCommand
    {
        /// <summary>
        /// A comma-separated list of processors to disable.
        /// </summary>
        [Option("--disable", Description = "A comma-separated list of processors to disable.")]
        public string DisabledProcessors { get; set; } = string.Empty;

        public Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            ScoreStatisticsQueueProcessor queue = new ScoreStatisticsQueueProcessor(DisabledProcessors.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

            queue.Run(cancellationToken);

            return Task.FromResult(0);
        }
    }
}
