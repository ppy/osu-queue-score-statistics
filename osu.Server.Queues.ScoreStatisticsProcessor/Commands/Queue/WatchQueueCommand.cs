// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Queue
{
    [Command("watch", Description = "Begins processing of the queue.")]
    public class WatchQueueCommand
    {
        private readonly ScoreStatisticsQueueProcessor queue = new ScoreStatisticsQueueProcessor();

        public Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            queue.Run(cancellationToken);
            return Task.FromResult(0);
        }
    }
}
