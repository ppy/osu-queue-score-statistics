// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("verify-replays-exist", Description = "Verifies replays exist on S3 where replay flag implies they do.")]
    public class VerifyReplaysExistCommand
    {
        /// <summary>
        /// The <c>scores</c> table row ID to start verifying replays from.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "--start-id")]
        public ulong? StartId { get; set; }

        /// <summary>
        /// The number of scores to fetch in each batch.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "--batch-size")]
        public int BatchSize { get; set; } = 5000;

        [Option(CommandOptionType.SingleOrNoValue, Template = "--dry-run")]
        public bool DryRun { get; set; }

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            ulong lastId = StartId ?? 0;

            using var s3 = S3.GetClient();
            using var conn = await DatabaseAccess.GetConnectionAsync(cancellationToken);

            Console.WriteLine();
            Console.WriteLine($"Verifying replay flag for scores starting from {lastId}");

            if (DryRun)
                Console.WriteLine("RUNNING IN DRY RUN MODE.");

            while (!cancellationToken.IsCancellationRequested)
            {
                IEnumerable<SoloScore> importedScores = await conn.QueryAsync<SoloScore>(
                    "SELECT `id`, `has_replay` FROM scores WHERE id BETWEEN @lastId AND (@lastId + @batchSize - 1) AND has_replay = 1 AND legacy_score_id IS NULL ORDER BY id",
                    new
                    {
                        lastId,
                        batchSize = BatchSize
                    });

                if (!importedScores.Any())
                {
                    if (lastId > await conn.QuerySingleAsync<ulong>("SELECT MAX(id) FROM scores"))
                    {
                        Console.WriteLine("All done!");
                        break;
                    }

                    lastId += (ulong)BatchSize;
                    continue;
                }

                foreach (var score in importedScores)
                {
                    Debug.Assert(score.has_replay);

                    try
                    {
                        // check the replay is available on s3.
                        _ = await s3.GetObjectMetadataAsync(S3.REPLAYS_BUCKET, score.id.ToString(CultureInfo.InvariantCulture), cancellationToken);
                    }
                    catch (AmazonS3Exception ex)
                    {
                        // This is the closest we can get to knowing if a file exists or not.
                        if (ex.ErrorType != ErrorType.Sender || ex.ErrorCode != "Forbidden")
                            throw;

                        Console.WriteLine($"Score {score.id} missing replay");
                        if (!DryRun)
                            await conn.ExecuteAsync("UPDATE scores SET has_replay = 0 WHERE id = @id", score);
                    }

                    lastId = score.id;
                }

                lastId++;
            }

            return 0;
        }
    }
}
