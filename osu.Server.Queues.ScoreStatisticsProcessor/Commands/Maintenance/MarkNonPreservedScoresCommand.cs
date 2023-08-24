// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("mark-non-preserved", Description = "Mark any scores which no longer need to be preserved.")]
    public class MarkNonPreservedScoresCommand : BaseCommand
    {
        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            using (var readConnection = Queue.GetDatabaseConnection())
            using (var deleteConnection = Queue.GetDatabaseConnection())
            using (var markCommand = deleteConnection.CreateCommand())
            {
                markCommand.CommandText = $"UPDATE {SoloScore.TABLE_NAME} SET preserve = 0 WHERE id = @id;";
                MySqlParameter scoreId = markCommand.Parameters.Add("id", MySqlDbType.UInt64);
                await markCommand.PrepareAsync(cancellationToken);

                // We are going to be processing a sheer motherload of scores.
                // The best way to do this right now is per-user, as we have an index for that (and generally a single user will have 1-50,000 scores, making it a good amount to process at once).
                // Or maybe we want a queue for it.
                var scores = await readConnection.QueryAsync<SoloScore>(new CommandDefinition($"SELECT * FROM {SoloScore.TABLE_NAME} WHERE preserve = 1 ORDER BY user_id, ruleset_id", flags: CommandFlags.None, cancellationToken: cancellationToken));

                foreach (var score in scores)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // TODO: check pinned state
                    // TODO: check whether multiplayer score that needs saving

                    // TODO: check whether other score is higher than this one for the user (we only need to keep the top)

                    // TODO: fix mod equality check
                    var maxPPUserScore = scores
                                         .Where(s => s.beatmap_id == score.beatmap_id && s.ruleset_id == score.ruleset_id && s.ScoreInfo.Mods.SequenceEqual(score.ScoreInfo.Mods))
                                         .MaxBy(s => s.ScoreInfo.PP);

                    var maxScoreUserScore = scores
                                            .Where(s => s.beatmap_id == score.beatmap_id && s.ruleset_id == score.ruleset_id && s.ScoreInfo.Mods.SequenceEqual(score.ScoreInfo.Mods))
                                            .MaxBy(s => s.ScoreInfo.TotalScore);

                    // Check whether this score is the user's highest
                    if (maxPPUserScore?.id == score.id || maxScoreUserScore?.id == score.id)
                    {
                        Console.WriteLine($"Maintaining preservation for {score.id} (is user high)");
                        continue;
                    }

                    Console.WriteLine($"Marking score {score.id} non-preserved...");
                    scoreId.Value = score.id;

                    // TODO: break production
                    // await markCommand.ExecuteNonQueryAsync(cancellationToken);
                    // TODO: queue for de-indexing
                }
            }

            return 0;
        }
    }
}
