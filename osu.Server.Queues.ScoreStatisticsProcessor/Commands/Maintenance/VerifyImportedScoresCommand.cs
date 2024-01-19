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
        [Option(CommandOptionType.SingleValue, Template = "--start-id")]
        public ulong? StartId { get; set; }

        [Option(CommandOptionType.SingleOrNoValue, Template = "--dry-run")]
        public bool DryRun { get; set; }

        private ElasticQueuePusher? elasticQueueProcessor;

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            ulong lastId = StartId ?? 0;
            int deleted = 0;
            int fail = 0;

            Console.WriteLine();
            Console.WriteLine($"Verifying scores starting from {lastId}");

            elasticQueueProcessor = new ElasticQueuePusher();
            Console.WriteLine($"Indexing to elasticsearch queue(s) {elasticQueueProcessor.ActiveQueues}");

            if (DryRun)
                Console.WriteLine("RUNNING IN DRY RUN MODE.");

            using var conn = DatabaseAccess.GetConnection();

            while (!cancellationToken.IsCancellationRequested)
            {
                HashSet<ElasticQueuePusher.ElasticScoreItem> elasticItems = new HashSet<ElasticQueuePusher.ElasticScoreItem>();

                IEnumerable<ComparableScore> importedScores = await conn.QueryAsync<ComparableScore>(
                    "SELECT id, "
                    + "ruleset_id, "
                    + "legacy_score_id, "
                    + "legacy_total_score, "
                    + "total_score, "
                    + "pp "
                    + "FROM scores "
                    + "WHERE id >= @lastId ORDER BY id LIMIT 2000", new { lastId });

                // gather high scores for each ruleset
                foreach (var rulesetScores in importedScores.GroupBy(s => s.ruleset_id))
                {
                    var rulesetSpecifics = LegacyDatabaseHelper.GetRulesetSpecifics(rulesetScores.Key);

                    var highScores = await conn.QueryAsync<HighScore>($"SELECT * FROM {rulesetSpecifics.HighScoreTable} WHERE score_id IN ({string.Join(',', rulesetScores.Select(s => s.legacy_score_id))})");

                    foreach (var score in rulesetScores)
                        score.HighScore = highScores.SingleOrDefault(s => s.score_id == score.legacy_score_id!.Value);
                }

                if (!importedScores.Any())
                {
                    Console.WriteLine("All done!");
                    break;
                }

                elasticItems.Clear();

                foreach (var importedScore in importedScores)
                {
                    if (importedScore.legacy_score_id == null) continue;

                    // Score was deleted in legacy table.
                    if (importedScore.HighScore == null)
                    {
                        if (!DryRun)
                        {
                            await conn.ExecuteAsync("DELETE FROM scores WHERE id = @id", importedScore);
                        }

                        Interlocked.Increment(ref deleted);
                        elasticItems.Add(new ElasticQueuePusher.ElasticScoreItem { ScoreId = (long?)importedScore.id });
                        continue;
                    }

                    if (!check(importedScore.id, "performance", importedScore.pp ?? 0, importedScore.HighScore.pp ?? 0))
                    {
                        // PP was reset (had a value in new table but no value in old).
                        if (importedScore.HighScore.pp == null)
                        {
                            if (!DryRun)
                            {
                                await conn.ExecuteAsync("UPDATE scores SET pp = NULL WHERE id = @id", importedScore);
                            }
                        }
                        // PP doesn't match.
                        else
                        {
                            if (!DryRun)
                            {
                                await conn.ExecuteAsync("UPDATE scores SET pp = @pp WHERE id = @id", new
                                {
                                    pp = importedScore.HighScore.pp,
                                    id = importedScore.id,
                                });
                            }
                        }

                        elasticItems.Add(new ElasticQueuePusher.ElasticScoreItem { ScoreId = (long?)importedScore.id });
                        Interlocked.Increment(ref fail);
                    }

                    // TODO: check data. will be slow unless we cache beatmap attribs lookups
                    var referenceScore = BatchInserter.CreateReferenceScore(importedScore.ruleset_id, importedScore.HighScore);

                    if (!check(importedScore.id, "total score", importedScore.total_score, referenceScore.TotalScore))
                    {
                        Interlocked.Increment(ref fail);

                        if (!DryRun)
                        {
                            await conn.ExecuteAsync("UPDATE scores SET total_score = @score WHERE id = @id", new
                            {
                                score = referenceScore.TotalScore,
                                id = importedScore.id,
                            });
                        }
                    }

                    if (!check(importedScore.id, "legacy total score", importedScore.legacy_total_score, referenceScore.LegacyTotalScore))
                    {
                        Interlocked.Increment(ref fail);

                        if (!DryRun)
                        {
                            await conn.ExecuteAsync("UPDATE scores SET legacy_total_score = @score WHERE id = @id", new
                            {
                                score = referenceScore.LegacyTotalScore,
                                id = importedScore.id,
                            });
                        }
                    }
                }

                if (elasticItems.Count > 0)
                {
                    if (!DryRun)
                        elasticQueueProcessor.PushToQueue(elasticItems.ToList());
                    Console.WriteLine($"Queued {elasticItems.Count} items for indexing");
                }

                lastId = importedScores.Max(s => s.id) + 1;

                Console.SetCursorPosition(0, Console.GetCursorPosition().Top);
                Console.Write($"Processed up to {lastId} ({deleted} deleted {fail} failed)");
            }

            return 0;
        }

        private bool check<T>(ulong scoreId, string name, T imported, T original)
        {
            if (imported == null && original == null)
                return true;

            if (imported?.Equals(original) != true)
            {
                Console.WriteLine($"{scoreId}: {name} doesn't match ({imported} vs {original})");
                return false;
            }

            return true;
        }

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        public class ComparableScore
        {
            public ulong id;
            public int ruleset_id;
            public ulong? legacy_score_id;
            public long? legacy_total_score;
            public long? total_score;
            public float? pp;

            public HighScore? HighScore { get; set; }
        }
    }
}
