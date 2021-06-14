// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Dapper;
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
                if (item.ruleset_id > 0)
                {
                    Console.WriteLine($"Item {item} is for an unsupported ruleset");
                    return;
                }

                if (item.processed_at != null)
                {
                    Console.WriteLine($"Item {item} already processed, rolling back before reapplying");

                    // if required, we can rollback any previous version of processing then reapply with the latest.
                    db.Execute("UPDATE osu_user_stats SET playcount = playcount - 1 WHERE user_id = @user_id", item);
                }

                Console.WriteLine($"Processing score {item}");

                db.Execute("UPDATE osu_user_stats SET playcount = playcount + 1 WHERE user_id = @user_id", item);
                db.Execute("UPDATE solo_scores SET processed_at = NOW() WHERE id = @id", item);
            }
        }
    }
}
