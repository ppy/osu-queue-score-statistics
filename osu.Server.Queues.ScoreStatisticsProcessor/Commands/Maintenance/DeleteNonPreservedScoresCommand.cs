// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
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
        /// <summary>
        /// How many hours non-preserved scores should be retained before being purged.
        /// </summary>
        private const int preserve_hours = 48;

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            using (var readConnection = await DatabaseAccess.GetConnectionAsync(cancellationToken))
            using (var deleteConnection = await DatabaseAccess.GetConnectionAsync(cancellationToken))
            using (var deleteCommand = deleteConnection.CreateCommand())
            using (var s3 = S3.GetClient())
            {
                deleteCommand.CommandText = "DELETE FROM scores WHERE id = @id;";

                MySqlParameter scoreId = deleteCommand.Parameters.Add("id", MySqlDbType.UInt64);

                await deleteCommand.PrepareAsync(cancellationToken);

                while (true)
                {
                    var scores = await readConnection.QueryAsync<SoloScore>(new CommandDefinition(
                        $"SELECT * FROM `scores` WHERE `preserve` = 0 AND `unix_updated_at` < UNIX_TIMESTAMP(DATE_SUB(NOW(), INTERVAL {preserve_hours} HOUR)) LIMIT 1000",
                        cancellationToken: cancellationToken));

                    if (!scores.Any())
                        break;

                    Console.WriteLine($"Processing next batch of {scores.Count()} scores");

                    foreach (var score in scores)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        Console.WriteLine($"Deleting score {score.id}...");
                        scoreId.Value = score.id;
                        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

                        if (score.has_replay)
                        {
                            Console.WriteLine("* Removing replay from S3...");
                            var deleteResult = await s3.DeleteObjectAsync(S3.REPLAYS_BUCKET, score.id.ToString(CultureInfo.InvariantCulture), cancellationToken);

                            switch (deleteResult.HttpStatusCode)
                            {
                                case HttpStatusCode.NoContent:
                                    // below wording is intentionally very roundabout, because s3 does not actually really seem to produce the types of error you'd expect.
                                    // for instance, even if you request removal of a nonexistent object, it'll just throw a 204 No Content back
                                    // with no real way to determine whether it actually even did anything.
                                    Console.WriteLine("* Deletion request completed without error.");
                                    break;

                                default:
                                    await Console.Error.WriteLineAsync($"* Received unexpected status code when attempting to delete replay: {deleteResult.HttpStatusCode}.");
                                    break;
                            }
                        }
                    }
                }
            }

            return 0;
        }
    }
}
