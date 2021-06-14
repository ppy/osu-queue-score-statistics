// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor;

namespace osu.Server.Queues.ScorePump
{
    [Command("all", "Pumps test scores")]
    public class PumpAllScores : ScorePump
    {
        public int OnExecute()
        {
            using (var db = Queue.GetDatabaseConnection())
            while (true)
            {
                var scoreItem = new ScoreItem();
                Console.WriteLine($"Pumping {scoreItem}");

                Queue.PushToQueue(scoreItem);
                Thread.Sleep(200);
            }
        }
    }
}
