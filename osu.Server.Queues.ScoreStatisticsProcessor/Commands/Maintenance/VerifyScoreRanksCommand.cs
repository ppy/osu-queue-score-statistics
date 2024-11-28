// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using MySqlConnector;
using osu.Game.Rulesets.Catch.Scoring;
using osu.Game.Rulesets.Mania.Scoring;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Scoring;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Taiko.Scoring;
using osu.Game.Scoring;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using StringBuilder = System.Text.StringBuilder;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("verify-score-ranks", Description = "Verifies rank values for all scores")]
    public class VerifyScoreRanksCommand
    {
        /// <summary>
        /// The score ID to start processing from.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "--start-id")]
        public ulong? StartId { get; set; }

        [Option(CommandOptionType.SingleOrNoValue, Template = "-v|--verbose",
            Description = "Output when a score is preserved too.")]
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

        private readonly ElasticQueuePusher elasticQueueProcessor = new ElasticQueuePusher();

        private readonly StringBuilder sqlBuffer = new StringBuilder();

        private readonly HashSet<ElasticQueuePusher.ElasticScoreItem> elasticItems =
            new HashSet<ElasticQueuePusher.ElasticScoreItem>();

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            ulong lastId = StartId ?? 0;
            int fail = 0;

            using var conn = await DatabaseAccess.GetConnectionAsync(cancellationToken);

            if (lastId == 0)
            {
                ulong min = ulong.MaxValue;

                for (int i = 0; i < 3; i++)
                {
                    // Have to do it this way to make use of the available table indices.
                    min = Math.Min(min, await conn.QuerySingleAsync<ulong?>(
                        "SELECT MIN(id) FROM scores WHERE id >= @lastId AND legacy_score_id IS NULL AND ruleset_id = @rulesetId",
                        new
                        {
                            lastId,
                            rulesetId = i
                        }) ?? min);
                }

                lastId = min;
            }

            Console.WriteLine();
            Console.WriteLine($"Verifying score ranks starting from {lastId}");

            Console.WriteLine($"Indexing to elasticsearch queue(s) {elasticQueueProcessor.ActiveQueues}");

            if (DryRun)
                Console.WriteLine("RUNNING IN DRY RUN MODE.");

            while (!cancellationToken.IsCancellationRequested)
            {
                SoloScore[] scores = (await conn.QueryAsync<SoloScore>(
                    "SELECT id, accuracy, data, `rank`, ruleset_id FROM scores WHERE id >= @lastId AND legacy_score_id IS NULL ORDER BY id LIMIT @batchSize",
                    new
                    {
                        lastId,
                        batchSize = BatchSize
                    })).ToArray();

                if (!scores.Any())
                {
                    Console.WriteLine("All done!");
                    break;
                }

                foreach (var score in scores)
                {
                    bool requiresIndexing = false;

                    try
                    {
                        var processor = getProcessorForScore(score);
                        ScoreRank rank = processor.RankFromScore(score.accuracy, score.ScoreData.Statistics);

                        IEnumerable<Mod> mods = score.ScoreData.Mods.Select(apiMod => apiMod.ToMod(processor.Ruleset));

                        foreach (var mod in mods.OfType<IApplicableToScoreProcessor>())
                            rank = mod.AdjustRank(rank, score.accuracy);

                        if (!check(score.id, "rank", score.rank, rank))
                        {
                            Interlocked.Increment(ref fail);
                            requiresIndexing = true;
                            sqlBuffer.Append($"UPDATE scores SET `rank` = '{rank.ToString()}' WHERE `id` = {score.id};");
                        }
                    }
                    finally
                    {
                        if (requiresIndexing)
                        {
                            elasticItems.Add(new ElasticQueuePusher.ElasticScoreItem { ScoreId = (long?)score.id });
                        }
                    }
                }

                if (!Quiet)
                {
                    Console.Write($"Processed up to {scores.Max(s => s.id)} ({fail} fixed)");
                    Console.SetCursorPosition(0, Console.GetCursorPosition().Top);
                }

                lastId = scores.Last().id + 1;
                flush(conn);
            }

            flush(conn, true);

            Console.WriteLine($"Finished ({fail} fixed)");

            return 0;
        }

        private static readonly Dictionary<int, ScoreProcessor>
            score_processors = new Dictionary<int, ScoreProcessor>();

        private static ScoreProcessor getProcessorForScore(SoloScore soloScore)
        {
            if (score_processors.TryGetValue(soloScore.ruleset_id, out var processor))
                return processor;

            switch (soloScore.ruleset_id)
            {
                case 0:
                    return score_processors[0] = new OsuScoreProcessor();

                case 1:
                    return score_processors[1] = new TaikoScoreProcessor();

                case 2:
                    return score_processors[2] = new CatchScoreProcessor();

                case 3:
                    return score_processors[3] = new ManiaScoreProcessor();

                default:
                    throw new InvalidOperationException();
            }
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
    }
}
