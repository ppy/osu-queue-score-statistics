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
using osu.Game.Scoring;
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

        /// <summary>
        /// The ruleset to run this verify job for.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "--ruleset-id")]
        public int RulesetId { get; set; }

        /// <summary>
        /// The number of scores to run in each batch. Setting this higher will cause larger SQL statements for insert.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "--batch-size")]
        public int BatchSize { get; set; } = 5000;

        [Option(CommandOptionType.SingleOrNoValue, Template = "--dry-run")]
        public bool DryRun { get; set; }

        [Option(CommandOptionType.SingleOrNoValue, Template = "--delete-only")]
        public bool DeleteOnly { get; set; }

        private ElasticQueuePusher? elasticQueueProcessor;

        private int skipOutput;

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            var rulesetSpecifics = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId);

            ulong lastId = StartId ?? 0;
            int deleted = 0;
            int fail = 0;

            Console.WriteLine();
            Console.WriteLine($"Verifying scores starting from {lastId} for ruleset {RulesetId}");

            elasticQueueProcessor = new ElasticQueuePusher();
            Console.WriteLine($"Indexing to elasticsearch queue(s) {elasticQueueProcessor.ActiveQueues}");

            if (DryRun)
                Console.WriteLine("RUNNING IN DRY RUN MODE.");

            using var conn = DatabaseAccess.GetConnection();

            while (!cancellationToken.IsCancellationRequested)
            {
                HashSet<ElasticQueuePusher.ElasticScoreItem> elasticItems = new HashSet<ElasticQueuePusher.ElasticScoreItem>();

                IEnumerable<ComparableScore> importedScores = await conn.QueryAsync(
                    "SELECT `id`, "
                    + "`ruleset_id`, "
                    + "`legacy_score_id`, "
                    + "`legacy_total_score`, "
                    + "`total_score`, "
                    + "s.`rank`, "
                    + "s.`pp`, "
                    + "h.* "
                    + "FROM scores s "
                    + $"LEFT JOIN {rulesetSpecifics.HighScoreTable} h ON (legacy_score_id = score_id)"
                    + "WHERE id BETWEEN @lastId AND (@lastId + @batchSize - 1) AND legacy_score_id IS NOT NULL AND ruleset_id = @rulesetId ORDER BY id",
                    (ComparableScore score, HighScore highScore) =>
                    {
                        score.HighScore = highScore;
                        return score;
                    },
                    new
                    {
                        lastId,
                        rulesetId = RulesetId,
                        batchSize = BatchSize
                    }, splitOn: "score_id");

                if (!importedScores.Any())
                {
                    if (lastId > await conn.QuerySingleAsync<ulong>("SELECT MAX(id) FROM scores"))
                    {
                        Console.WriteLine("All done!");
                        break;
                    }

                    lastId += (ulong)BatchSize;

                    if (++skipOutput % 100 == 0)
                        Console.WriteLine($"Skipped up to {lastId}...");
                    continue;
                }

                elasticItems.Clear();

                if (!DeleteOnly)
                {
                    Parallel.ForEach(importedScores, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Environment.ProcessorCount,
                    }, importedScore =>
                    {
                        if (importedScore.legacy_score_id == null) return;

                        if (importedScore.HighScore == null) return;

                        importedScore.ReferenceScore = BatchInserter.CreateReferenceScore(importedScore.ruleset_id, importedScore.HighScore);
                    });
                }

                foreach (var importedScore in importedScores)
                {
                    bool requiresIndexing = false;

                    try
                    {
                        if (importedScore.legacy_score_id == null) continue;

                        // Score was deleted in legacy table.
                        //
                        // Importantly, `legacy_score_id` of 0 implies a non-high-score (which doesn't have a matching entry).
                        // We should leave these.
                        if (importedScore.HighScore == null && importedScore.legacy_score_id > 0)
                        {
                            Interlocked.Increment(ref deleted);
                            requiresIndexing = true;

                            if (!DryRun)
                            {
                                await conn.ExecuteAsync("DELETE FROM scores WHERE id = @id", new
                                {
                                    id = importedScore.id
                                });
                            }

                            continue;
                        }

                        if (DeleteOnly)
                            continue;

                        var referenceScore = importedScore.ReferenceScore!;

                        if (!check(importedScore.id, "performance", importedScore.pp ?? 0, importedScore.HighScore.pp ?? 0))
                        {
                            Interlocked.Increment(ref fail);
                            requiresIndexing = true;

                            // PP was reset (had a value in new table but no value in old).
                            if (importedScore.HighScore.pp == null)
                            {
                                if (!DryRun)
                                {
                                    await conn.ExecuteAsync("UPDATE scores SET pp = NULL WHERE id = @id", new
                                    {
                                        id = importedScore.id
                                    });
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
                        }

                        if (!check(importedScore.id, "total score", importedScore.total_score, referenceScore.TotalScore))
                        {
                            Interlocked.Increment(ref fail);
                            requiresIndexing = true;

                            if (referenceScore.TotalScore > 4294967295)
                            {
                                Console.WriteLine($"Score out of range ({referenceScore.TotalScore})!");
                                continue;
                            }

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
                            requiresIndexing = true;

                            if (referenceScore.LegacyTotalScore > 4294967295)
                            {
                                Console.WriteLine($"Score out of range ({referenceScore.LegacyTotalScore})!");
                                continue;
                            }

                            if (!DryRun)
                            {
                                await conn.ExecuteAsync("UPDATE scores SET legacy_total_score = @score WHERE id = @id", new
                                {
                                    score = referenceScore.LegacyTotalScore ?? 0,
                                    id = importedScore.id,
                                });
                            }
                        }

                        if (!check(importedScore.id, "rank", importedScore.rank, referenceScore.Rank))
                        {
                            Interlocked.Increment(ref fail);
                            requiresIndexing = true;

                            if (!DryRun)
                            {
                                await conn.ExecuteAsync("UPDATE scores SET `rank` = @rank WHERE `id` = @id", new
                                {
                                    rank = referenceScore.Rank.ToString(),
                                    id = importedScore.id,
                                });
                            }
                        }
                    }
                    finally
                    {
                        if (requiresIndexing)
                            elasticItems.Add(new ElasticQueuePusher.ElasticScoreItem { ScoreId = (long?)importedScore.id });
                    }
                }

                if (elasticItems.Count > 0)
                {
                    if (!DryRun)
                        elasticQueueProcessor.PushToQueue(elasticItems.ToList());
                    Console.WriteLine($"Queued {elasticItems.Count} items for indexing");
                }

                Console.SetCursorPosition(0, Console.GetCursorPosition().Top);
                Console.Write($"Processed up to {importedScores.Max(s => s.id)} ({deleted} deleted {fail} failed)");

                lastId += (ulong)BatchSize;
            }

            return 0;
        }

        private bool check<T>(ulong scoreId, string name, T imported, T original)
        {
            if (imported == null && original == null)
                return true;

            if (imported?.Equals(original) != true)
            {
                Console.WriteLine();
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
            public long legacy_total_score;
            public long? total_score;
            public ScoreRank rank;
            public float? pp;

            public HighScore? HighScore { get; set; }
            public ScoreInfo? ReferenceScore { get; set; }
        }
    }
}
