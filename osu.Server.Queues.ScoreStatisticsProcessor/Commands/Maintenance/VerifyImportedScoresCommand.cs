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
using MySqlConnector;
using osu.Game.Scoring;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Commands.Queue;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using StringBuilder = System.Text.StringBuilder;

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

        [Option(CommandOptionType.SingleOrNoValue, Template = "-v|--verbose", Description = "Output when a score is preserved too.")]
        public bool Verbose { get; set; }

        [Option(CommandOptionType.SingleOrNoValue, Template = "-q|--quiet", Description = "Reduces output.")]
        public bool Quiet { get; set; }

        /// <summary>
        /// The number of scores to run in each batch. Setting this higher will cause larger SQL statements for insert.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "--batch-size")]
        public int BatchSize { get; set; } = 5000;

        [Option(CommandOptionType.SingleOrNoValue, Template = "--dry-run")]
        public bool DryRun { get; set; }

        [Option(CommandOptionType.SingleOrNoValue, Template = "--delete-only")]
        public bool DeleteOnly { get; set; }

        private readonly ElasticQueuePusher elasticQueueProcessor = new ElasticQueuePusher();

        private readonly StringBuilder sqlBuffer = new StringBuilder();

        private readonly HashSet<ElasticQueuePusher.ElasticScoreItem> elasticItems = new HashSet<ElasticQueuePusher.ElasticScoreItem>();

        private int skipOutput;

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            var rulesetSpecifics = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId);

            ulong lastId = StartId ?? 0;
            int deleted = 0;
            int fail = 0;

            using var conn = await DatabaseAccess.GetConnectionAsync(cancellationToken);

            if (lastId == 0)
            {
                lastId = await conn.QuerySingleAsync<ulong?>(
                    "SELECT id FROM scores WHERE ruleset_id = @rulesetId AND legacy_score_id = (SELECT MIN(legacy_score_id) FROM scores WHERE ruleset_id = @rulesetId AND id >= @lastId AND legacy_score_id > 0)",
                    new
                    {
                        lastId,
                        rulesetId = RulesetId,
                    }) ?? lastId;
            }

            Console.WriteLine();
            Console.WriteLine($"Verifying scores starting from {lastId} for ruleset {RulesetId}");

            Console.WriteLine($"Indexing to elasticsearch queue(s) {elasticQueueProcessor.ActiveQueues}");

            if (DryRun)
                Console.WriteLine("RUNNING IN DRY RUN MODE.");

            while (!cancellationToken.IsCancellationRequested)
            {
                IEnumerable<ComparableScore> importedScores = await conn.QueryAsync(
                    "SELECT `id`, "
                    + "`ruleset_id`, "
                    + "`legacy_score_id`, "
                    + "`legacy_total_score`, "
                    + "`total_score`, "
                    + "`has_replay`, "
                    + "s.ranked,"
                    + "s.`rank`, "
                    + "s.`pp`, "
                    + "h.* "
                    + "FROM scores s "
                    + $"LEFT JOIN {rulesetSpecifics.HighScoreTable} h ON (legacy_score_id = score_id) "
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
                        // Score was set via lazer, we have nothing to verify.
                        if (importedScore.legacy_score_id == null) continue;

                        // Score was deleted in legacy table.
                        //
                        // Importantly, `legacy_score_id` of 0 implies a non-high-score (which doesn't have a matching entry).
                        // We should leave these.
                        if (importedScore.HighScore == null)
                        {
                            if (importedScore.legacy_score_id > 0)
                            {
                                // don't delete pinned scores
                                int countPinned = conn.QuerySingle<int>($"SELECT COUNT(*) FROM `score_pins` WHERE score_id = {importedScore.id}");
                                if (countPinned > 0)
                                    continue;

                                Interlocked.Increment(ref deleted);
                                requiresIndexing = true;

                                sqlBuffer.Append($"DELETE FROM scores WHERE id = {importedScore.id};");

                                continue;
                            }
                            else
                            {
                                // Score was sourced from the osu_scores table, and we don't really care about verifying these.
                                continue;
                            }
                        }

                        if (DeleteOnly)
                            continue;

                        var referenceScore = importedScore.ReferenceScore!;

                        if (!check(importedScore.id, "performance", importedScore.pp ?? 0, importedScore.HighScore.pp ?? 0))
                        {
                            Interlocked.Increment(ref fail);
                            requiresIndexing = true;

                            // PP was reset (had a value in new table but no value in old) or doesn't match.
                            sqlBuffer.Append($"UPDATE scores SET pp = {importedScore.HighScore.pp.ToString() ?? "NULL"} WHERE id = {importedScore.id};");
                        }

                        if (!check(importedScore.id, "ranked", importedScore.ranked, true))
                        {
                            Interlocked.Increment(ref fail);
                            requiresIndexing = true;
                            sqlBuffer.Append($"UPDATE scores SET ranked = 1 WHERE id = {importedScore.id};");
                        }

                        if (!check(importedScore.id, "replay", importedScore.has_replay, importedScore.HighScore.replay))
                        {
                            Interlocked.Increment(ref fail);
                            sqlBuffer.Append($"UPDATE scores SET has_replay = {importedScore.HighScore.replay} WHERE id = {importedScore.id};");
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

                            sqlBuffer.Append($"UPDATE scores SET total_score = {referenceScore.TotalScore} WHERE id = {importedScore.id};");
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

                            sqlBuffer.Append($"UPDATE scores SET legacy_total_score = {referenceScore.LegacyTotalScore ?? 0} WHERE id = {importedScore.id};");
                        }

                        if (!check(importedScore.id, "rank", importedScore.rank, referenceScore.Rank))
                        {
                            Interlocked.Increment(ref fail);
                            requiresIndexing = true;
                            sqlBuffer.Append($"UPDATE scores SET `rank` = '{referenceScore.Rank.ToString()}' WHERE `id` = {importedScore.id};");
                        }
                    }
                    finally
                    {
                        if (requiresIndexing)
                            elasticItems.Add(new ElasticQueuePusher.ElasticScoreItem { ScoreId = (long?)importedScore.id });
                    }
                }

                if (!Quiet)
                {
                    Console.Write($"Processed up to {importedScores.Max(s => s.id)} ({deleted} deleted {fail} failed)");
                    Console.SetCursorPosition(0, Console.GetCursorPosition().Top);
                }

                lastId += (ulong)BatchSize;
                flush(conn);
            }

            flush(conn, true);

            Console.WriteLine($"Finished ({deleted} deleted {fail} failed)");

            return 0;
        }

        private void flush(MySqlConnection conn, bool force = false)
        {
            int bufferLength = sqlBuffer.Length;

            if (bufferLength == 0)
                return;

            if (bufferLength > 1024 || force)
            {
                if (!DryRun)
                {
                    if (!Quiet)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"Flushing sql batch ({bufferLength:N0} bytes)");
                    }

                    conn.Execute(sqlBuffer.ToString());

                    if (elasticItems.Count > 0)
                    {
                        elasticQueueProcessor.PushToQueue(elasticItems.ToList());

                        if (!Quiet)
                            Console.WriteLine($"Queued {elasticItems.Count} items for indexing");

                        elasticItems.Clear();
                    }
                }

                sqlBuffer.Clear();
            }
        }

        private bool check<T>(ulong scoreId, string name, T imported, T original)
        {
            if (imported == null && original == null)
                return true;

            if (imported?.Equals(original) != true)
            {
                if (Verbose)
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
            public bool has_replay;
            public ScoreRank rank;
            public bool ranked;
            public float? pp;

            public HighScore? HighScore { get; set; }
            public ScoreInfo? ReferenceScore { get; set; }
        }
    }
}
