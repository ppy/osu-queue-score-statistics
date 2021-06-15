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
            using (var transaction = db.BeginTransaction())
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
                    db.Execute("UPDATE osu_user_stats SET playcount = playcount - 1 WHERE user_id = @user_id", item, transaction);
                }

                db.Execute("INSERT INTO osu.osu_user_stats "
                           + "(user_id, count300, count100, count50, countMiss, accuracy_total, accuracy_count, accuracy, playcount, ranked_score, total_score, x_rank_count, xh_rank_count, s_rank_count, sh_rank_count, a_rank_count, `rank`, level, replay_popularity, fail_count, exit_count, max_combo, country_acronym, rank_score, rank_score_index, accuracy_new, last_update, last_played, total_seconds_played) "
                           + "VALUES (@user_id, DEFAULT, DEFAULT, DEFAULT, DEFAULT, 0, 0, 0, 1, 0, 0, 0, DEFAULT, 0, DEFAULT, 0, 0, 0, DEFAULT, DEFAULT, DEFAULT, DEFAULT, DEFAULT, 0, 0, 0, DEFAULT, DEFAULT, DEFAULT) "
                           + "ON DUPLICATE KEY UPDATE playcount = playcount + 1", item, transaction);

                // eventually this will (likely) not be a thing, as we will be reading directly from the queue and not worrying about a database store.
                db.Execute("UPDATE solo_scores SET processed_at = NOW() WHERE id = @id", item, transaction);

                transaction.Commit();
            }
        }
    }
}
