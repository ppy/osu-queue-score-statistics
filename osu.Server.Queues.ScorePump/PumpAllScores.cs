// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor;

namespace osu.Server.Queues.ScorePump
{
    [Command("all", Description = "Pumps all completed scores")]
    public class PumpAllScores : ScorePump
    {
        public int OnExecute(CancellationToken cancellationToken)
        {
            using (var db = Queue.GetDatabaseConnection())
            {
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM solo_scores";

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (cancellationToken.IsCancellationRequested)
                                break;

                            // TODO: handle failed / retry cases.

                            var score = new ScoreItem
                            {
                                score_id = reader.GetInt64("id"),
                                user_id = reader.GetInt64("user_id"),
                                beatmap_id = reader.GetInt64("beatmap_id"),
                            };

                            Console.WriteLine($"Pumping {score}");
                            Queue.PushToQueue(score);
                        }
                    }
                }
            }

            return 0;
        }
    }
}
