// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Game.Rulesets;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Commands.Queue;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("verify-imported-scores", Description = "Verifies data and pp for already imported scores")]
    public class VerifyImportedScoresCommand
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
            Console.WriteLine($"Verifying scores starting from {lastId}");

            elasticQueueProcessor = new ElasticQueuePusher();
            Console.WriteLine($"Indexing to elasticsearch queue(s) {elasticQueueProcessor.ActiveQueues}");

            Thread.Sleep(5000);

            using (var conn = DatabaseAccess.GetConnection())
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    List<ElasticQueuePusher.ElasticScoreItem> elasticItems = new List<ElasticQueuePusher.ElasticScoreItem>();

                    var highScores = await conn.QueryAsync<SoloScore>("SELECT * FROM scores WHERE id >= @lastId ORDER BY id LIMIT 500", new { lastId });

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

                        var rulesetSpecifics = LegacyDatabaseHelper.GetRulesetSpecifics(score.ruleset_id);
                        Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(score.ruleset_id);

                        var highScore = conn.QuerySingleOrDefault<HighScore>($"SELECT * FROM {rulesetSpecifics.HighScoreTable} WHERE score_id = {score.ScoreInfo.LegacyScoreId}");

                        if (highScore == null)
                        {
                            Console.WriteLine($"{score.id}: Original score was deleted!!");

                            // TODO: delete imported score
                            deleted++;
                            continue;
                        }

                        // check data
                        var referenceScore = await BatchInserter.CreateReferenceScore(ruleset, highScore, conn, null);
                        string referenceSerialised = BatchInserter.SerialiseScore(ruleset, highScore, referenceScore);

                        if (score.data != referenceSerialised)
                        {
                            Console.WriteLine($"{score.id}: Data content did not match!!");
                            continue;
                        }

                        dynamic? performance = await conn.QuerySingleOrDefaultAsync("SELECT pp FROM score_performance WHERE score_id = @id", score);

                        if (performance == null)
                        {
                            Console.WriteLine($"{score.id}: Performance entry missing!!");
                            continue;
                        }

                        if (performance.pp != highScore.pp)
                        {
                            Console.WriteLine($"{score.id}: Performance value doesn't match!!");
                            continue;
                        }

                        // await conn.ExecuteAsync("DELETE FROM score_performance WHERE score_id = @id; DELETE FROM scores WHERE id = @id", score, transaction);
                        // await conn.ExecuteAsync("DELETE FROM score_legacy_id_map WHERE ruleset_id = @ruleset_id AND old_score_id = @legacy_score_id", new
                        // {
                        //     score.ruleset_id,
                        //     legacy_score_id = score.ScoreInfo.LegacyScoreId
                        // }, transaction);
                        //
                        // elasticItems.Add(new ElasticQueuePusher.ElasticScoreItem { ScoreId = (long?)score.id });
                        // deleted++;
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

            return 0;
        }
    }
}
