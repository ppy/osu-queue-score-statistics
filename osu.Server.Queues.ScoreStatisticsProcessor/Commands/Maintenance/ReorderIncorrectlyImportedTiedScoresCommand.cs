// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.QueueProcessor;
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

            int totalReordered = 0;

            if (DryRun)
                Console.WriteLine("RUNNING IN DRY RUN MODE.");

            using var conn = DatabaseAccess.GetConnection();

            dynamic[] beatmaps = (await conn.QueryAsync($"SELECT * FROM osu_beatmaps WHERE approved > 0 and beatmap_id >= {StartId ?? 0}")).ToArray();

            for (int i = 0; i < beatmaps.Length; i++)
            {
                dynamic beatmap = beatmaps[i];
                Console.WriteLine($"Processing {beatmap.beatmap_id}...");

                ulong[] topScoresCheck =
                    (await conn.QueryAsync<ulong>(
                        $"SELECT total_score FROM scores WHERE beatmap_id = {beatmap.beatmap_id} and ruleset_id = {RulesetId} AND preserve = 1 AND legacy_score_id IS NOT NULL ORDER BY total_score DESC LIMIT 2"))
                    .ToArray();

                if (topScoresCheck.Length != 2 || topScoresCheck[0] != topScoresCheck[1])
                    continue;

                ulong topScore = topScoresCheck[0];

                Console.WriteLine("Has tied scores, checking order...");

                var topScores = (await conn.QueryAsync<SoloScore>(
                        $"SELECT * FROM scores WHERE beatmap_id = {beatmap.beatmap_id} and ruleset_id = {RulesetId} AND preserve = 1 AND total_score = {topScore} ORDER BY id"))
                    .ToArray();

                var topScoresSorted = topScores.OrderBy(s => s.legacy_score_id).ToArray();

                if (topScores.SequenceEqual(topScoresSorted))
                    continue;

                Console.WriteLine("Requires reordering...");

                totalReordered++;

                Console.WriteLine($"Reordering complete ({totalReordered} reordered, {i}/{beatmaps.Length})");
            }

            return 0;
        }
    }
}
