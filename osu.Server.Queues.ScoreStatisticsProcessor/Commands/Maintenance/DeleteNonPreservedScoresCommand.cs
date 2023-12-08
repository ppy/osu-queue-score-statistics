// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using MySqlConnector;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("cleanup", Description = "Delete non-preserved scores which are stale enough.")]
    public class DeleteNonPreservedScoresCommand
    {
        /// <summary>
        /// How many hours non-preserved scores should be retained before being purged.
        /// </summary>
        private const int preserve_hours = 48;

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            using (var readConnection = DatabaseAccess.GetConnection())
            using (var deleteConnection = DatabaseAccess.GetConnection())
            using (var deleteCommand = deleteConnection.CreateCommand())
            {
                deleteCommand.CommandText =
                    "DELETE FROM solo_scores_performance WHERE score_id = @id;" +
                    "DELETE FROM solo_scores_process_history WHERE score_id = @id;" +
                    "DELETE FROM solo_scores WHERE id = @id;";

                MySqlParameter scoreId = deleteCommand.Parameters.Add("id", MySqlDbType.UInt64);

                await deleteCommand.PrepareAsync(cancellationToken);

                var scores = await readConnection.QueryAsync<SoloScore>(new CommandDefinition($"SELECT * FROM solo_scores WHERE preserve = 0 AND updated_at < DATE_SUB(NOW(), INTERVAL {preserve_hours} HOUR)", flags: CommandFlags.None, cancellationToken: cancellationToken));

                foreach (var score in scores)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    Console.WriteLine($"Deleting score {score.id}...");
                    scoreId.Value = score.id;
                    await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

                    if (score.has_replay)
                    {
                        // TODO: delete replay from s3
                    }
                }
            }

            return 0;
        }
    }
}
