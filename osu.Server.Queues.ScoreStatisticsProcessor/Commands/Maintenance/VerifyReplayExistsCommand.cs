// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("verify-replays-exist", Description = "Verifies replays exist on S3 where replay flag implies they do.")]
    public class VerifyReplaysExistCommand
    {
        /// <summary>
        /// The high score ID to start deleting imported high scores from.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "--start-id")]
        public ulong? StartId { get; set; }

        [Option(CommandOptionType.SingleOrNoValue, Template = "-v|--verbose", Description = "Output when a score is preserved too.")]
        public bool Verbose { get; set; }

        /// <summary>
        /// The number of scores to run in each batch. Setting this higher will cause larger SQL statements for insert.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "--batch-size")]
        public int BatchSize { get; set; } = 5000;

        [Option(CommandOptionType.SingleOrNoValue, Template = "--dry-run")]
        public bool DryRun { get; set; }

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            ulong lastId = StartId ?? 0;

            var s3Key = Environment.GetEnvironmentVariable("S3_KEY") ?? string.Empty;
            var s3Secret = Environment.GetEnvironmentVariable("S3_SECRET") ?? string.Empty;

            using var s3 = new AmazonS3Client(new BasicAWSCredentials(s3Key, s3Secret), new AmazonS3Config
            {
                CacheHttpClient = true,
                HttpClientCacheSize = 32,
                RegionEndpoint = RegionEndpoint.USWest1,
                UseHttp = true,
                ForcePathStyle = true
            });

            using var conn = DatabaseAccess.GetConnection();

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
                    if (!score.has_replay) continue;

                    try
                    {
                        // check the replay is available on s3.
                        _ = await s3.GetObjectMetadataAsync("score-replays", score.id.ToString(CultureInfo.InvariantCulture), cancellationToken);
                    }
                    catch
                    {
                        Console.WriteLine($"Score {score.id} missing replay");
                        if (!DryRun)
                            await conn.ExecuteAsync("UPDATE scores SET has_replay = 0 WHERE id = @id", score);
                    }
                }
            }

            return 0;
        }
    }
}
