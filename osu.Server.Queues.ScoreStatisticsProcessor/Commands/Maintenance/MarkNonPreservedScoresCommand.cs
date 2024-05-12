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
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("mark-non-preserved", Description = "Mark any scores which no longer need to be preserved.")]
    public class MarkNonPreservedScoresCommand
    {
        private readonly ElasticQueuePusher elasticQueueProcessor = new ElasticQueuePusher();

        [Option(CommandOptionType.SingleValue, Template = "-r|--ruleset", Description = "The ruleset to process.")]
        public int RulesetId { get; set; }

        [Option(CommandOptionType.SingleOrNoValue, Template = "--dry-run", Description = "Don't actually mark, just output.")]
        public bool DryRun { get; set; }

        [Option(CommandOptionType.SingleOrNoValue, Template = "-v|--verbose", Description = "Output when a score is preserved too.")]
        public bool Verbose { get; set; }

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            LegacyDatabaseHelper.RulesetDatabaseInfo databaseInfo = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId);

            Console.WriteLine($"Running for ruleset {RulesetId}");
            if (DryRun)
                Console.WriteLine("RUNNING IN DRY RUN MODE.");

            using (var db = DatabaseAccess.GetConnection())
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
            var parameters = new
            {
                userId,
                rulesetId = RulesetId,
            };

            IEnumerable<SoloScore> scores = await db.QueryAsync<SoloScore>(new CommandDefinition("SELECT * FROM scores WHERE preserve = 1 AND user_id = @userId AND ruleset_id = @rulesetId", parameters, cancellationToken: cancellationToken));

            if (!scores.Any())
                return;

            IEnumerable<ulong> pins = db.Query<ulong>("SELECT score_id FROM score_pins WHERE user_id = @userId AND ruleset_id = @rulesetId AND score_type = 'solo_score'", parameters);

            Console.WriteLine($"Processing user {userId} ({scores.Count()} scores)..");

            foreach (var score in scores)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                if (pins.Contains(score.id))
                {
                    if (Verbose)
                        Console.WriteLine($"Maintaining preservation for {score.id} (is pinned)");
                    continue;
                }

                if (await checkIsMultiplayerScoreAsync(db, score))
                {
                    if (Verbose)
                        Console.WriteLine($"Maintaining preservation for {score.id} (is multiplayer)");
                    continue;
                }

                // check whether this score is a user high (either total_score or pp)
                if (checkIsUserHigh(scores, score))
                {
                    if (Verbose)
                        Console.WriteLine($"Maintaining preservation for {score.id} (is user high)");
                    continue;
                }

                Console.WriteLine($"Marking score {score.id} non-preserved...");

                if (!DryRun)
                {
                    await db.ExecuteAsync("UPDATE scores SET preserve = 0, unix_updated_at = UNIX_TIMESTAMP() WHERE id = @scoreId;", new
                    {
                        scoreId = score.id
                    });

                    elasticQueueProcessor.PushToQueue(new ElasticQueuePusher.ElasticScoreItem
                    {
                        ScoreId = (long?)score.id
                    });
                }
            }
        }

        private async Task<bool> checkIsMultiplayerScoreAsync(MySqlConnection db, SoloScore score) =>
            await db.QuerySingleOrDefaultAsync<ulong?>("SELECT `playlist_item_id` FROM `multiplayer_playlist_item_scores` WHERE `score_id` = @scoreId", new
            {
                scoreId = score.id
            }) != null;

        private static bool checkIsUserHigh(IEnumerable<SoloScore> userScores, SoloScore candidate)
        {
            var maxPPUserScore = userScores
                                 .Where(s => s.beatmap_id == candidate.beatmap_id && s.ruleset_id == candidate.ruleset_id && compareMods(candidate, s) && s.ranked)
                                 .MaxBy(s => s.pp);

            var maxScoreUserScore = userScores
                                    .Where(s => s.beatmap_id == candidate.beatmap_id && s.ruleset_id == candidate.ruleset_id && compareMods(candidate, s) && s.ranked)
                                    .MaxBy(s => s.total_score);

            // Check whether this score is the user's highest
            return maxPPUserScore?.id == candidate.id || maxScoreUserScore?.id == candidate.id;

            bool compareMods(SoloScore a, SoloScore b)
            {
                // Compare non-ordered mods, ignoring any settings applied.
                var aMods = new HashSet<string>(a.ScoreData.Mods.Select(m => m.Acronym));
                var bMods = new HashSet<string>(b.ScoreData.Mods.Select(m => m.Acronym));

                return aMods.SetEquals(bMods);
            }
        }
    }
}
