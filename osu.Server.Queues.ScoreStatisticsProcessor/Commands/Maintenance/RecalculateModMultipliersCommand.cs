// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;
using MySqlConnector;
using osu.Game.Database;
using osu.Game.Rulesets.Scoring;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using BeatmapStore = osu.Server.Queues.ScoreStatisticsProcessor.Stores.BeatmapStore;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("recalculate-mod-multipliers", Description = "Recalculates total score after a change to mod multipliers")]
    public class RecalculateModMultipliersCommand
    {
        [Option(CommandOptionType.SingleValue, Template = "--start-id")]
        public ulong? StartId { get; set; }

        [Option(CommandOptionType.SingleValue, Template = "--batch-size")]
        public int BatchSize { get; set; } = 5000;

        [Option(CommandOptionType.SingleOrNoValue, Template = "--dry-run")]
        [MemberNotNullWhen(false, nameof(elasticQueuePusher))]
        public bool DryRun { get; set; }

        [Option(CommandOptionType.SingleOrNoValue, Template = "-v|--verbose", Description = "Output verbose information on processing.")]
        public bool Verbose { get; set; }

        private readonly StringBuilder sqlBuffer = new StringBuilder();

        private ElasticQueuePusher? elasticQueuePusher;

        private readonly List<ElasticQueuePusher.ElasticScoreItem> elasticItems = new List<ElasticQueuePusher.ElasticScoreItem>();

        [UsedImplicitly]
        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            ulong lastId = StartId ?? 0;

            ulong skipped = 0;
            ulong updated = 0;

            using var conn = await DatabaseAccess.GetConnectionAsync(cancellationToken);

            Console.WriteLine();
            Console.WriteLine($"Recalculating total score in line with new mod multipliers, starting from ID {lastId}");

            if (DryRun)
                Console.WriteLine("RUNNING IN DRY RUN MODE.");
            else
            {
                elasticQueuePusher = new ElasticQueuePusher();
                Console.WriteLine($"Indexing to elastic queue(s) {elasticQueuePusher.ActiveQueues}");
            }

            await Task.Delay(5000, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var scoresWithMods = (await conn.QueryAsync<SoloScore>(
                    "SELECT * FROM `scores` WHERE `id` BETWEEN @lastId AND (@lastId + @batchSize - 1) AND JSON_LENGTH(`data`, '$.mods') > 0",
                    new
                    {
                        lastId,
                        batchSize = BatchSize,
                    })).ToArray();

                if (scoresWithMods.Length == 0)
                {
                    if (lastId > await conn.QuerySingleAsync<ulong>("SELECT MAX(id) FROM scores"))
                    {
                        Console.WriteLine("All done!");
                        break;
                    }

                    lastId += (ulong)BatchSize;
                    continue;
                }

                foreach (var score in scoresWithMods)
                {
                    string source = score.is_legacy_score ? "stable" : "lazer ";

                    var beatmap = await BeatmapStore.GetBeatmapAsync(score.beatmap_id, conn);

                    if (beatmap == null)
                    {
                        if (Verbose)
                            Console.WriteLine($"[{score.id,11} {source}] Skipped due to missing beatmap");
                        skipped++;
                        continue;
                    }

                    score.beatmap = beatmap;
                    var scoreInfo = score.ToScoreInfo();
                    var difficultyInfo = beatmap.GetLegacyBeatmapConversionDifficultyInfo();
                    var ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(score.ruleset_id);

                    long oldTotalScore = score.total_score;
                    long newTotalScore;

                    if (score.is_legacy_score)
                    {
                        var scoringAttributes = BatchInserter.GetCachedScoringAttributes(new BatchInserter.BeatmapLookup((int)score.beatmap_id, score.ruleset_id), conn)?.ToAttributes();

                        if (scoringAttributes == null)
                        {
                            if (Verbose)
                                Console.WriteLine($"[{score.id,11} {source}] Skipped due to missing scoring attributes");
                            skipped++;
                            continue;
                        }

                        StandardisedScoreMigrationTools.UpdateFromLegacy(scoreInfo, ruleset, difficultyInfo, scoringAttributes.Value);
                        newTotalScore = scoreInfo.TotalScore;
                    }
                    else
                    {
                        if (scoreInfo.TotalScoreWithoutMods == 0 && scoreInfo.TotalScore != 0)
                        {
                            if (Verbose)
                                Console.WriteLine($"[{score.id,11} {source}] Skipped due to missing total score without mods");
                            skipped++;
                            continue;
                        }

                        var multiplierCalculator = ruleset.CreateScoreMultiplierCalculator(new ScoreMultiplierContext(scoreInfo.BeatmapInfo!.Difficulty));
                        double multiplier = multiplierCalculator.CalculateFor(scoreInfo.Mods);

                        newTotalScore = (long)Math.Round(scoreInfo.TotalScoreWithoutMods * multiplier);
                    }

                    if (newTotalScore == oldTotalScore)
                    {
                        if (Verbose)
                            Console.WriteLine($"[{score.id,11} {source}] Skipped due to no change in score");
                        skipped++;
                        continue;
                    }

                    if (Verbose)
                        Console.WriteLine($"[{score.id,11} {source}] Updating score: {oldTotalScore,8} (old) -> {newTotalScore,8} (new)");

                    sqlBuffer.Append($@"UPDATE `scores` SET `total_score` = {newTotalScore} WHERE `id` = {score.id};");
                    elasticItems.Add(new ElasticQueuePusher.ElasticScoreItem { ScoreId = (long?)score.id });
                    updated++;
                }

                lastId += (ulong)BatchSize;

                Console.WriteLine($"Processed up to {lastId - 1} ({updated} updated, {skipped} skipped)");

                flush(conn);
            }

            flush(conn, true);

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
                    Console.WriteLine();
                    Console.WriteLine($"Flushing sql batch ({bufferLength:N0} bytes)");
                    conn.Execute(sqlBuffer.ToString());

                    if (elasticItems.Count > 0)
                    {
                        elasticQueuePusher.PushToQueue(elasticItems.ToList());
                        Console.WriteLine($"Queued {elasticItems.Count} items for indexing");
                    }
                }

                elasticItems.Clear();
                sqlBuffer.Clear();
            }
        }
    }
}
