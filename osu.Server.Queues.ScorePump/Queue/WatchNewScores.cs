// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using Dapper;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScorePump.Queue
{
    /// <summary>
    /// Keep in mind that this command is intended to be a temporary stand-in until osu-web
    /// pushes to the score processing queue directly.
    /// </summary>
    [Command("watch", Description = "Watch for new scores and queue as they arrive.")]
    public class WatchNewScores : QueueCommand
    {
        [Option("--start_id")]
        public ulong? StartId { get; set; }

        private const int count_per_run = 100;

        [UsedImplicitly]
        private ulong lastId;

        public int OnExecute(CancellationToken cancellationToken)
        {
            if (StartId.HasValue)
                lastId = StartId.Value - 1;
            else
            {
                using (var db = Queue.GetDatabaseConnection())
                    lastId = db.QuerySingleOrDefault<ulong?>($"SELECT MAX($score_id) FROM {ProcessHistory.TABLE_NAME}") ?? 0;
            }

            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                using (var db = Queue.GetDatabaseConnection())
                {
                    var scores = db.Query<SoloScore>($"SELECT * FROM {SoloScore.TABLE_NAME} WHERE id > @lastId LIMIT @count_per_run", new
                    {
                        lastId,
                        count_per_run
                    });

                    int processed = 0;

                    foreach (var score in scores)
                    {
                        lastId = score.id;

                        if (cancellationToken.IsCancellationRequested)
                            break;

                        // skip weird rulesets for now.
                        if (score.ruleset_id > 3)
                            continue;

                        // attach any previous processing information
                        // this should never be the case, and should probably be removed eventually.
                        var history = db.QuerySingleOrDefault<ProcessHistory>($"SELECT * FROM {ProcessHistory.TABLE_NAME} WHERE score_id = @id", new { score.id });

                        Console.WriteLine($"Pumping {score}");
                        Queue.PushToQueue(new ScoreItem(score, history));

                        processed++;
                    }

                    // sleep whenever we are not at our peak processing rate.
                    if (processed < count_per_run)
                        Thread.Sleep(200);
                }
            }

            return 0;
        }
    }
}
