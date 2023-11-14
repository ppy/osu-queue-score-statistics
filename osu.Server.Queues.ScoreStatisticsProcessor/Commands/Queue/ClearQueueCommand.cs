// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Queue
{
    [Command("clear", Description = "Completely empties the processing queue")]
    public class ClearQueueCommand
    {
        private readonly ScoreStatisticsQueueProcessor queue = new ScoreStatisticsQueueProcessor();

        public Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            queue.ClearQueue();
            Console.WriteLine("Queue has been cleared!");
            return Task.FromResult(0);
        }
    }
}
