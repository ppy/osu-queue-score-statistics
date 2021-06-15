// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScorePump
{
    [Command("test", Description = "Pumps test scores")]
    public class PumpTestDataCommand : ScorePump
    {
        public int OnExecute(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // TODO: push meaningful scores.
                var scoreItem = new ScoreItem();
                Console.WriteLine($"Pumping {scoreItem}");

                Queue.PushToQueue(scoreItem);
                Thread.Sleep(200);
            }

            return 0;
        }
    }
}
