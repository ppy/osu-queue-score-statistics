// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Queue
{
    [Command("pump-test", Description = "Pumps empty test scores to the queue")]
    public class PumpTestDataCommand
    {
        private readonly ScoreStatisticsQueueProcessor queue = new ScoreStatisticsQueueProcessor();

        public Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // TODO: push meaningful scores.
                var scoreItem = new ScoreItem(new SoloScore());
                Console.WriteLine($"Pumping {scoreItem}");

                queue.PushToQueue(scoreItem);
                Thread.Sleep(200);
            }

            return Task.FromResult(0);
        }
    }
}
