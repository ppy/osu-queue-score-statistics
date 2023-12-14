// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Queue
{
    /// <summary>
    /// Deletes already-imported high scores from the solo_scores table.
    /// </summary>
    /// <remarks>
    /// Sometimes we need to delete already imported scores to fix an issue (or remove them for good).
    /// This command handles doing that correctly, including de-indexing, deleting related entities, etc.
    ///
    /// If running this on a large batch, it is recommended to stop the import process first, and then re-run
    /// it from the earliest deleted `score_id`. This will ensure correctness of ordering.
    ///
    /// For complete correctness, all scores from the earliest deletion to the latest score should be deleted.
    /// If not, scores will be out of order chronologically in their solo_score.id space.
    ///
    /// Generally this isn't a huge issue, and the chance of it being seen in tiebreaker comparisons is as close to
    /// zero as it gets. But it does mean that we can no longer make the assertion that legacy imports are chronologically imported.
    /// I'm not sure how important this is in the first place, because they are never going to be perfectly chronological next to
    /// non-imported (lazer-first) scores anyway...
    /// </remarks>
    [Command("delete-high-scores", Description = "Deletes already-imported high scores from the solo_scores table.")]
    public class DeleteImportedHighScoresCommand
    {
        /// <summary>
        /// The high score ID to start deleting imported high scores from.
        /// </summary>
        [Argument(0)]
        public ulong StartId { get; set; }

        private ElasticQueuePusher? elasticQueueProcessor;

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            ulong lastId = StartId;
            int deleted = 0;

            Console.WriteLine();
            Console.WriteLine($"Deleting from solo_scores starting from {lastId}");

            elasticQueueProcessor = new ElasticQueuePusher();
            Console.WriteLine($"Indexing to elasticsearch queue(s) {elasticQueueProcessor.ActiveQueues}");

            Thread.Sleep(5000);

            using (var conn = DatabaseAccess.GetConnection())
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    List<ElasticQueuePusher.ElasticScoreItem> elasticItems = new List<ElasticQueuePusher.ElasticScoreItem>();

                    using (var transaction = await conn.BeginTransactionAsync(cancellationToken))
                    {
                        var highScores = await conn.QueryAsync<SoloScore>("SELECT * FROM solo_scores WHERE id >= @lastId ORDER BY id LIMIT 500", new { lastId }, transaction);

                        if (!highScores.Any())
                        {
                            Console.WriteLine("All done!");
                            break;
                        }

                        elasticItems.Clear();

                        foreach (var score in highScores)
                        {
                            if (!score.ScoreInfo.IsLegacyScore)
                                continue;

                            Console.WriteLine($"Deleting {score.id}...");
                            await conn.ExecuteAsync("DELETE FROM solo_scores_performance WHERE score_id = @id; DELETE FROM solo_scores WHERE id = @id", score, transaction);
                            await conn.ExecuteAsync("DELETE FROM solo_scores_legacy_id_map WHERE ruleset_id = @ruleset_id AND old_score_id = @legacy_score_id", new
                            {
                                score.ruleset_id,
                                legacy_score_id = score.ScoreInfo.LegacyScoreId
                            }, transaction);

                            elasticItems.Add(new ElasticQueuePusher.ElasticScoreItem { ScoreId = (long?)score.id });
                            deleted++;
                        }

                        if (elasticItems.Count > 0)
                        {
                            elasticQueueProcessor.PushToQueue(elasticItems);
                            Console.WriteLine($"Queued {elasticItems.Count} items for indexing");
                        }

                        lastId = highScores.Max(s => s.id);
                        Console.WriteLine($"Processed up to {lastId} ({deleted} deleted)");
                    }
                }
            }

            return 0;
        }
    }
}
