// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScorePump.Queue
{
    /// <summary>
    /// This command is obsolete now that osu-web pushes directly to the queue.
    /// </summary>
    [Command("watch", Description = "Watch for new scores being inserted into `solo_scores` and queue for processing as they arrive.")]
    public class WatchNewScoresCommand : BaseCommand
    {
        [Option("--start_id")]
        public ulong? StartId { get; set; }

        private const int count_per_run = 100;

        [UsedImplicitly]
        private ulong lastId;

        public Task<int> OnExecuteAsync(CancellationToken cancellationToken)
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
                    var scores = db.Query<SoloScore>($"SELECT * FROM {SoloScore.TABLE_NAME} WHERE id > @lastId ORDER BY id LIMIT @count_per_run", new
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

            return Task.FromResult(0);
        }
    }
}
