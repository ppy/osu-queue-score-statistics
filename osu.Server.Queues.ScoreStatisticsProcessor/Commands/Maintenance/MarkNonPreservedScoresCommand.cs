// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("mark-non-preserved", Description = "Mark any scores which no longer need to be preserved.")]
    public class MarkNonPreservedScoresCommand : BaseCommand
    {
        [Option(CommandOptionType.SingleValue, Template = "-r|--ruleset", Description = "The ruleset to process.")]
        public int RulesetId { get; set; }

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            LegacyDatabaseHelper.RulesetDatabaseInfo databaseInfo = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId);

            Console.WriteLine($"Running for ruleset {RulesetId}");

            using (var db = Queue.GetDatabaseConnection())
            {
                Console.WriteLine("Fetching all users...");
                int[] userIds = (await db.QueryAsync<int>($"SELECT `user_id` FROM {databaseInfo.UserStatsTable}")).ToArray();
                Console.WriteLine($"Fetched {userIds.Length} users");

                foreach (int userId in userIds)
                    await processUser(db, userId, cancellationToken);
            }

            return 0;
        }

        private async Task processUser(MySqlConnection db, int userId, CancellationToken cancellationToken)
        {
            var scores = await db.QueryAsync<SoloScore>(new CommandDefinition($"SELECT * FROM {SoloScore.TABLE_NAME} WHERE preserve = 1 AND user_id = @userId AND ruleset_id = @rulesetId", new
            {
                userId = userId,
                rulesetId = RulesetId,
            }, cancellationToken: cancellationToken));

            if (!scores.Any())
                return;

            Console.WriteLine($"Processing user {userId} ({scores.Count()} scores)..");

            foreach (var score in scores)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                if (checkPinned(db, userId, score))
                {
                    Console.WriteLine($"Maintaining preservation for {score.id} (is pinned)");
                    continue;
                }

                if (checkIsMultiplayerScore(db, score))
                {
                    Console.WriteLine($"Maintaining preservation for {score.id} (is multiplayer)");
                    continue;
                }

                // check whether this score is a user high (either total_score or pp)
                if (checkIsUserHigh(scores, score))
                {
                    Console.WriteLine($"Maintaining preservation for {score.id} (is user high)");
                    continue;
                }

                Console.WriteLine($"Marking score {score.id} non-preserved...");

                await db.ExecuteAsync($"UPDATE {SoloScore.TABLE_NAME} SET preserve = 0 WHERE id = @scoreId;", new
                {
                    scoreId = score.id
                });

                // TODO: queue for de-indexing
            }
        }

        private bool checkIsMultiplayerScore(MySqlConnection db, SoloScore score)
        {
            // TODO: implement (once multiplayer scores are in solo_scores, is an ongoing effort in osu-web).
            return false;
        }

        private static bool checkIsUserHigh(IEnumerable<SoloScore> userScores, SoloScore candidate)
        {
            // TODO: fix mod equality check (should not consider options; should not be ordered comparison)
            var maxPPUserScore = userScores
                                 .Where(s => s.beatmap_id == candidate.beatmap_id && s.ruleset_id == candidate.ruleset_id && s.ScoreInfo.Mods.SequenceEqual(candidate.ScoreInfo.Mods))
                                 .MaxBy(s => s.ScoreInfo.PP);

            var maxScoreUserScore = userScores
                                    .Where(s => s.beatmap_id == candidate.beatmap_id && s.ruleset_id == candidate.ruleset_id && s.ScoreInfo.Mods.SequenceEqual(candidate.ScoreInfo.Mods))
                                    .MaxBy(s => s.ScoreInfo.TotalScore);

            // Check whether this score is the user's highest
            return maxPPUserScore?.id == candidate.id || maxScoreUserScore?.id == candidate.id;
        }

        private bool checkPinned(MySqlConnection db, int userId, SoloScore score) =>
            db.QuerySingle<int>("SELECT COUNT(*) FROM score_pins WHERE user_id = @userId AND score_id = @scoreId AND ruleset_id = @rulesetId AND score_type = 'solo_score'", new
            {
                userId = userId,
                rulesetId = RulesetId,
                scoreId = score.id
            }) > 1;
    }
}
