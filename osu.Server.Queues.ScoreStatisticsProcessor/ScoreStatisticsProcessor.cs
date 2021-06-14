// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Server.QueueProcessor;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    public class ScoreStatisticsProcessor : QueueProcessor<ScoreItem>
    {
        public ScoreStatisticsProcessor()
            : base(new QueueConfiguration { InputQueueName = "score-statistics" })
        {
        }

        protected override void ProcessResult(ScoreItem item)
        {
            using (var db = GetDatabaseConnection())
            {
                Console.WriteLine($"Processing score {item}");
                //db.Execute("UPDATE osu_user_stats SET playcount = playcount + 1 WHERE user_id = @user_id", item);
            }
        }
    }
}
