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
using osu.Server.QueueProcessor;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("change-mod-multiplier", Description = "Changes a mod's multiplier globally by adjusting all relevant scores' totals.")]
    public class ChangeModMultiplierCommand
    {
        [Option(CommandOptionType.SingleValue, Template = "-r|--ruleset-id", Description = "Required. The ID of the ruleset for the mod whose multiplier is being adjusted.")]
        public int? RulesetId { get; set; }

        [Option(CommandOptionType.SingleValue, Template = "-m|--mod", Description = "Required. The acronym of the mod whose multiplier is being adjusted.")]
        public string? ModAcronym { get; set; }

        [Option(CommandOptionType.SingleValue, Template = "--old", Description = "Required. The old multiplier of the mod being adjusted.")]
        public double? OldMultiplier { get; set; }

        [Option(CommandOptionType.SingleValue, Template = "--new", Description = "Required. The new multiplier of the mod being adjusted.")]
        public double? NewMultiplier { get; set; }

        [Option(CommandOptionType.SingleValue, Template = "--start-id", Description = "The ID of the `scores` table row to start processing from.")]
        public ulong? StartId { get; set; }

        [Option(CommandOptionType.SingleOrNoValue, Template = "--batch-size", Description = "The maximum number of scores to fetch in each batch.")]
        public int BatchSize { get; set; } = 5000;

        [Option(CommandOptionType.SingleOrNoValue, Template = "--dry-run", Description = "Do not actually change any score totals, just display what would be done.")]
        public bool DryRun { get; set; }

        [Option(CommandOptionType.SingleValue, Template = "--where", Description = "Specify extra conditions to use when querying for scores to migrate.")]
        public string? AdditionalConditions { get; set; }

        private readonly StringBuilder sqlBuffer = new StringBuilder();
        private readonly ElasticQueuePusher elasticQueueProcessor = new ElasticQueuePusher();
        private readonly HashSet<ElasticQueuePusher.ElasticScoreItem> elasticItems = new HashSet<ElasticQueuePusher.ElasticScoreItem>();

        [UsedImplicitly]
        public async Task<int> OnExecuteAsync(CommandLineApplication app, CancellationToken cancellationToken)
        {
            if (RulesetId == null
                || string.IsNullOrEmpty(ModAcronym)
                || OldMultiplier == null
                || NewMultiplier == null)
            {
                await Console.Error.WriteLineAsync("One or more required parameters is missing.");
                app.ShowHelp(false);
                return 1;
            }

            if (NewMultiplier.Value == OldMultiplier.Value)
            {
                Console.WriteLine("New and old multipliers are equal - there is nothing to do.");
                return 0;
            }

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(OldMultiplier.Value, nameof(OldMultiplier));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(NewMultiplier.Value, nameof(NewMultiplier));

            Console.WriteLine();
            Console.WriteLine($"Changing multiplier of mod {ModAcronym} in ruleset {RulesetId} from {OldMultiplier} to {NewMultiplier}");
            Console.WriteLine($"Indexing to elasticsearch queue(s) {elasticQueueProcessor.ActiveQueues}");

            string scoreBatchQuery = "SELECT `id`, `total_score` "
                                     + "FROM `scores` "
                                     + "WHERE `id` BETWEEN @lastId AND (@lastId + @batchSize - 1) "
                                     + "AND `ruleset_id` = @rulesetId "
                                     + "AND JSON_SEARCH(`data`, 'one', @modAcronym, NULL, '$.mods[*].acronym') IS NOT NULL";

            if (!string.IsNullOrEmpty(AdditionalConditions))
                scoreBatchQuery += $" AND ({AdditionalConditions})";

            Console.WriteLine($"Will use following query:\n{scoreBatchQuery}");
            if (string.IsNullOrEmpty(AdditionalConditions))
                Console.WriteLine($"NO ADDITIONAL CONDITIONS SPECIFIED. WILL RUN FOR ALL SCORES MATCHING THE MOD ({ModAcronym}).");

            if (DryRun)
                Console.WriteLine("RUNNING IN DRY RUN MODE.");

            Console.WriteLine("Starting in 15 seconds.");
            Thread.Sleep(15000);
            Console.WriteLine("Starting now.");

            ulong lastId = StartId ?? 0;
            int converted = 0;
            int skipped = 0;

            using var conn = DatabaseAccess.GetConnection();

            while (!cancellationToken.IsCancellationRequested)
            {
                var scoresToAdjust = (await conn.QueryAsync<ScoreToAdjust>(
                    scoreBatchQuery,
                    new
                    {
                        lastId = lastId,
                        batchSize = BatchSize,
                        rulesetId = RulesetId,
                        modAcronym = ModAcronym,
                    })).ToArray();

                foreach (var score in scoresToAdjust)
                {
                    uint oldTotal = score.total_score;
                    score.total_score = (uint)(score.total_score / OldMultiplier * NewMultiplier);
                    Console.WriteLine($"{score.id}: total score change from {oldTotal} to {score.total_score}");

                    sqlBuffer.AppendLine($"UPDATE `scores` SET `total_score` = {score.total_score} WHERE `id` = {score.id};");
                    elasticItems.Add(new ElasticQueuePusher.ElasticScoreItem { ScoreId = (long?)score.id });
                }

                flush(conn);

                lastId += (ulong)BatchSize;
                converted += scoresToAdjust.Length;
                skipped += BatchSize - scoresToAdjust.Length;

                Console.WriteLine($"Processed up to ID {lastId} ({converted} converted {skipped} skipped)");

                if (lastId > await conn.QuerySingleAsync<ulong>("SELECT MAX(`id`) FROM `scores`"))
                {
                    Console.WriteLine("All done!");
                    break;
                }
            }

            flush(conn, force: true);
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
                        elasticQueueProcessor.PushToQueue(elasticItems.ToList());
                        Console.WriteLine($"Queued {elasticItems.Count} items for indexing");
                    }
                }

                elasticItems.Clear();
                sqlBuffer.Clear();
            }
        }

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        [Serializable]
        private class ScoreToAdjust
        {
            public ulong id { get; set; }
            public uint total_score { get; set; }
        }
    }
}
