// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("reorder-tied-scores", Description = "Single use command to fix incorrectly ordered score inserts.")]
    public class ReorderIncorrectlyImportedTiedScoresCommand
    {
        /// <summary>
        /// The ruleset to run this verify job for.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "--ruleset-id")]
        public int RulesetId { get; set; }

        [Option(CommandOptionType.SingleOrNoValue, Template = "--dry-run")]
        public bool DryRun { get; set; }

        /// <summary>
        /// The beatmap ID to start verifying from.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "--start-id")]
        public ulong? StartId { get; set; }

        private readonly ElasticQueuePusher elasticQueueProcessor = new ElasticQueuePusher();

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine();
            Console.WriteLine($"Verifying tied score orders for ruleset {RulesetId}");
            Console.WriteLine($"Indexing to elasticsearch queue(s) {elasticQueueProcessor.ActiveQueues}");

            var rulesetSpecifics = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId);

            int totalReordered = 0;

            if (DryRun)
                Console.WriteLine("RUNNING IN DRY RUN MODE.");

            using var conn = await DatabaseAccess.GetConnectionAsync(cancellationToken);

            dynamic[] beatmaps = (await conn.QueryAsync($"SELECT * FROM osu_beatmaps WHERE approved > 0 and beatmap_id >= {StartId ?? 0}")).ToArray();

            for (int i = 0; i < beatmaps.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                dynamic beatmap = beatmaps[i];

                if (i % 1000 == 0)
                    Console.WriteLine($"Processing {beatmap.beatmap_id}...");

                // Use old scores table because the lookup is faster.
                ulong[] topScoresCheck =
                    (await conn.QueryAsync<ulong>(
                        $"SELECT score FROM {rulesetSpecifics.HighScoreTable} WHERE beatmap_id = {beatmap.beatmap_id} AND hidden = 0 ORDER BY score DESC LIMIT 2",
                        commandTimeout: 60000))
                    .ToArray();

                if (topScoresCheck.Length != 2 || topScoresCheck[0] != topScoresCheck[1])
                    continue;

                ulong topScore = topScoresCheck[0];

                Console.Write($"{beatmap.beatmap_id} has tied scores, checking order... ");

                var topScores = (await conn.QueryAsync<SoloScore>(
                        $"SELECT id, legacy_score_id FROM scores WHERE beatmap_id = {beatmap.beatmap_id} and ruleset_id = {RulesetId} AND preserve = 1 AND legacy_score_id IN (SELECT score_id FROM {rulesetSpecifics.HighScoreTable} WHERE beatmap_id = {beatmap.beatmap_id} AND hidden = 0 AND score = {topScore}) ORDER BY id",
                        commandTimeout: 60000))
                    .ToArray();

                var topScoresSorted = topScores.OrderBy(s => s.legacy_score_id).ToArray();

                if (topScores.SequenceEqual(topScoresSorted))
                {
                    Console.WriteLine("OK");
                    continue;
                }

                Console.WriteLine("FAIL");

                using (var transaction = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
                {
                    for (int j = 0; j < topScores.Length; j++)
                    {
                        ulong legacyScoreId = topScoresSorted[j].legacy_score_id!.Value;
                        ulong oldScoreId = topScoresSorted[j].id;
                        ulong newScoreId = topScores[j].id;

                        Console.WriteLine($"- legacy {legacyScoreId} remapping from {oldScoreId} to {newScoreId}");

                        if (!DryRun)
                        {
                            await conn.ExecuteAsync("UPDATE scores SET id = @newScoreId, unix_updated_at = UNIX_TIMESTAMP(NOW()) WHERE legacy_score_id = @legacyScoreId AND ruleset_id = @rulesetId", new
                            {
                                newScoreId = newScoreId,
                                legacyScoreId = legacyScoreId,
                                rulesetId = RulesetId,
                            }, transaction);
                        }
                    }

                    await transaction.CommitAsync(cancellationToken);
                }

                if (!DryRun)
                {
                    elasticQueueProcessor.PushToQueue(topScores.Select(s => new ElasticQueuePusher.ElasticScoreItem
                    {
                        ScoreId = (long)s.id
                    }).ToList());
                }

                totalReordered++;

                Console.WriteLine($"Reordering complete ({totalReordered} reordered, {i + 1}/{beatmaps.Length})");
            }

            return 0;
        }
    }
}
