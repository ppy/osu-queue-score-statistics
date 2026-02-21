// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3.Model;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using MySqlConnector;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("delete-non-preserved", Description = "Delete non-preserved scores which are stale enough.")]
    public class DeleteNonPreservedScoresCommand
    {
        [Option(CommandOptionType.SingleOrNoValue, Template = "--dry-run", Description = "Don't actually mark, just output.")]
        public bool DryRun { get; set; }

        [Option(CommandOptionType.SingleOrNoValue, Template = "-v|--verbose", Description = "Output when a score is preserved too.")]
        public bool Verbose { get; set; }

        /// <summary>
        /// How many days non-preserved scores should be retained before being purged.
        /// </summary>
        private const int preserve_days = 2;

        private const string scores_table = "scores";
        private const string scores_cleanup_table = $"{scores_table}_cleanup";

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            using var db = await DatabaseAccess.GetConnectionAsync(cancellationToken);
            using var s3 = S3.GetClient();

            // Partitions are actually off-by-one, so we +1 here.
            //
            // For instance:
            // PARTITION p20260219 VALUES LESS THAN (0,1771372800) ENGINE = InnoDB
            // translates to p20260219 partition stores scores older than 2026-02-18 00:00:00 UTC+0
            DateTime cutoffDate = DateTime.UtcNow.Date.AddDays(1 - preserve_days);
            Console.WriteLine($"Processing partitions on {scores_table} starting from p{cutoffDate:yyyyMMdd}");

            List<string> partitions = await getEligiblePartitionsAsync(db, cutoffDate, cancellationToken);

            if (!partitions.Any())
            {
                Console.WriteLine("No eligible partitions found for deletion.");
                return 0;
            }

            Console.WriteLine($"Found {partitions.Count} partition(s) to process: {string.Join(", ", partitions)}");

            foreach (string partition in partitions)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                Console.WriteLine();
                Console.WriteLine($"Processing partition {partition}...");

                await processPartitionAsync(db, s3, partition, cancellationToken);

                Console.WriteLine($"Dropping partition {partition}...");
                if (!DryRun)
                    await db.ExecuteAsync(new CommandDefinition($"ALTER TABLE {scores_table} DROP PARTITION {partition}", cancellationToken: cancellationToken));
                Console.WriteLine($"Partition {partition} dropped successfully!");
            }

            return 0;
        }

        private async Task<List<string>> getEligiblePartitionsAsync(MySqlConnection db, DateTime cutoffDate, CancellationToken cancellationToken)
        {
            IEnumerable<string> partitions = await db.QueryAsync<string>(new CommandDefinition(
                $@"SELECT partition_name
                FROM information_schema.partitions
                WHERE table_schema = 'osu' AND table_name = '{scores_table}'
                AND partition_name IS NOT NULL
                ORDER BY partition_name",
                cancellationToken: cancellationToken));

            List<string> eligiblePartitions = new List<string>();

            foreach (string partition in partitions)
            {
                if (partition == "p0catch" || partition == "p1")
                    continue;

                if (DateTime.TryParseExact(partition.Substring(1), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var partitionDate))
                {
                    if (partitionDate <= cutoffDate)
                        eligiblePartitions.Add(partition);
                }
            }

            return eligiblePartitions;
        }

        private async Task processPartitionAsync(MySqlConnection db, Amazon.S3.IAmazonS3 s3, string partitionName, CancellationToken cancellationToken)
        {
            await db.ExecuteAsync($"CREATE TABLE {scores_cleanup_table} LIKE {scores_table}");
            await db.ExecuteAsync($"ALTER TABLE {scores_cleanup_table} REMOVE PARTITIONING");

            if (!DryRun)
            {
                Console.WriteLine("Moving partition contents to temporary table...");

                // https://dev.mysql.com/doc/refman/8.4/en/partitioning-management-exchange.html
                await db.ExecuteAsync($"ALTER TABLE {scores_table} EXCHANGE PARTITION {partitionName} WITH TABLE {scores_cleanup_table} WITHOUT VALIDATION");
            }

            long count = await db.QuerySingleAsync<long>(new CommandDefinition($"SELECT COUNT(id) FROM `{scores_cleanup_table}`", cancellationToken: cancellationToken));
            Console.WriteLine($"Partition contains {count:N0} scores.");

            var scores = (await db.QueryAsync<SoloScore>(
                    new CommandDefinition($"SELECT id, legacy_score_id FROM `{scores_cleanup_table}` WHERE `has_replay` = 1", cancellationToken: cancellationToken)))
                .ToArray();

            Console.WriteLine($"Cleaning up {scores.Length} scores with replays...");

            foreach (var score in scores)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                if (Verbose)
                    Console.WriteLine($"Deleting replay {score.id}...");

                if (score.is_legacy_score)
                {
                    // TODO: we likely do want logic here to handle the cleanup of replays.
                    // for now, make sure we don't attempt to clean up stable scores with replays here.
                    throw new InvalidOperationException($"Legacy score id:{score.id} legacy_id:{score.legacy_score_id} has replay flag set");
                }

                if (!DryRun)
                {
                    DeleteObjectResponse? deleteResult = await s3.DeleteObjectAsync(S3.REPLAYS_BUCKET, score.id.ToString(CultureInfo.InvariantCulture), cancellationToken);

                    switch (deleteResult.HttpStatusCode)
                    {
                        case HttpStatusCode.NoContent:
                            break;

                        default:
                            await Console.Error.WriteLineAsync($"* Received unexpected status code when attempting to delete replay: {deleteResult.HttpStatusCode}.");
                            break;
                    }
                }
            }

            Console.WriteLine("Cleaning up temporary table...");
            await db.ExecuteAsync($"DROP TABLE {scores_cleanup_table}");
        }
    }
}
