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
            int fail = 0;

            Console.WriteLine();
            Console.WriteLine($"Verifying scores starting from {lastId}");

            elasticQueueProcessor = new ElasticQueuePusher();
            Console.WriteLine($"Indexing to elasticsearch queue(s) {elasticQueueProcessor.ActiveQueues}");

            using (var conn = DatabaseAccess.GetConnection())
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    List<ElasticQueuePusher.ElasticScoreItem> elasticItems = new List<ElasticQueuePusher.ElasticScoreItem>();

                    var importedScores = await conn.QueryAsync<SoloScore>("SELECT * FROM scores WHERE id >= @lastId ORDER BY id LIMIT 500", new { lastId });

                    if (!importedScores.Any())
                    {
                        Console.WriteLine("All done!");
                        break;
                    }

                    elasticItems.Clear();

                    foreach (var importedScore in importedScores)
                    {
                        if (!importedScore.ScoreInfo.IsLegacyScore)
                            continue;

                        var rulesetSpecifics = LegacyDatabaseHelper.GetRulesetSpecifics(importedScore.ruleset_id);
                        Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(importedScore.ruleset_id);

                        var highScore = conn.QuerySingleOrDefault<HighScore>($"SELECT * FROM {rulesetSpecifics.HighScoreTable} WHERE score_id = {importedScore.ScoreInfo.LegacyScoreId}");

                        if (highScore == null)
                        {
                            Console.WriteLine($"{importedScore.id}: Original score was deleted!!");

                            // TODO: delete imported score
                            deleted++;
                            continue;
                        }

                        dynamic? importedPerformance = await conn.QuerySingleOrDefaultAsync("SELECT pp FROM score_performance WHERE score_id = @id", importedScore);

                        // check data
                        var referenceScore = await BatchInserter.CreateReferenceScore(ruleset, highScore, conn, null);

                        if (importedPerformance == null)
                        {
                            Console.WriteLine($"{importedScore.id}: Performance entry missing!!");
                            fail++;
                            continue;
                        }

                        if (!check(importedScore.id, "total score", importedScore.ScoreInfo.TotalScore, referenceScore.TotalScore))
                            fail++;
                        if (!check(importedScore.id, "legacy total score", importedScore.ScoreInfo.LegacyTotalScore, referenceScore.LegacyTotalScore))
                            fail++;
                        if (!check(importedScore.id, "performance", importedPerformance.pp, highScore.pp))
                            fail++;

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

                    lastId = importedScores.Max(s => s.id);
                    Console.WriteLine($"Processed up to {lastId} ({deleted} deleted {fail} failed)");
                }
            }

            return 0;
        }

        private bool check<T>(ulong scoreId, string name, T imported, T original)
        {
            if (imported?.Equals(original) != true)
            {
                Console.WriteLine($"{scoreId}: {name} doesn't match ({imported} vs {original})");
                return false;
            }

            return true;
        }
    }
}
