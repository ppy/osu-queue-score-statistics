// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor;

namespace osu.Server.Queues.ScorePump
{
    [Command("all", Description = "Pumps all completed scores")]
    public class PumpAllScores : ScorePump
    {
        [Option("--start_id")]
        public long StartId { get; set; }

        public int OnExecute(CancellationToken cancellationToken)
        {
            using (var db = Queue.GetDatabaseConnection())
            {
                var scores = db.Query<ScoreItem>("SELECT * FROM solo_scores WHERE id > @StartId", new { startId = StartId }, buffered: false);

                foreach (var score in scores)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    Console.WriteLine($"Pumping {score}");
                    Queue.PushToQueue(score);
                }
            }

            return 0;
        }
    }
}
