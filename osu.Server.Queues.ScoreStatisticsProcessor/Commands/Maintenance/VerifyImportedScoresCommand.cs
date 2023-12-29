// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Game.Rulesets;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Commands.Queue;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;

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

                    IEnumerable<ComparableScore> importedScores = await conn.QueryAsync<ComparableScore>(
                        "SELECT id, "
                        + "ruleset_id, "
                        + "data->'$.legacy_score_id' as legacy_score_id, "
                        + "data->'$.legacy_total_score' as legacy_total_score, "
                        + "data->'$.total_score' as total_score, "
                        + "pp "
                        + "FROM scores "
                        + "LEFT JOIN score_performance ON scores.id = score_performance.score_id "
                        + "WHERE id >= @lastId ORDER BY id LIMIT 500", new { lastId });

                    if (!importedScores.Any())
                    {
                        Console.WriteLine("All done!");
                        break;
                    }

                    elasticItems.Clear();

                    foreach (var importedScore in importedScores)
                    {
                        if (importedScore.legacy_score_id == null)
                            continue;

                        var rulesetSpecifics = LegacyDatabaseHelper.GetRulesetSpecifics(importedScore.ruleset_id);
                        Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(importedScore.ruleset_id);

                        var highScore = conn.QuerySingleOrDefault<HighScore>($"SELECT * FROM {rulesetSpecifics.HighScoreTable} WHERE score_id = {importedScore.legacy_score_id}");

                        if (highScore == null)
                        {
                            Console.WriteLine($"{importedScore.id}: Original score was deleted!!");

                            // TODO: delete imported score
                            deleted++;
                            continue;
                        }

                        // check data
                        var referenceScore = await BatchInserter.CreateReferenceScore(ruleset, highScore, conn, null);

                        if (importedScore.pp == null)
                        {
                            Console.WriteLine($"{importedScore.id}: Performance entry missing!!");
                            fail++;
                            continue;
                        }

                        if (!check(importedScore.id, "total score", importedScore.total_score, referenceScore.TotalScore))
                            fail++;
                        if (!check(importedScore.id, "legacy total score", importedScore.legacy_total_score, referenceScore.LegacyTotalScore))
                            fail++;
                        if (!check(importedScore.id, "performance", importedScore.pp, highScore.pp))
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

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        private class ComparableScore
        {
            public ulong id;
            public int ruleset_id;
            public long? legacy_score_id;
            public long? legacy_total_score;
            public long? total_score;
            public float? pp;
        }
    }
}
