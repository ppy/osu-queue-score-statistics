// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private ElasticQueuePusher? elasticQueueProcessor;

        [Option(CommandOptionType.SingleValue, Template = "-r|--ruleset", Description = "The ruleset to process.")]
        public int RulesetId { get; set; }

        [Option(CommandOptionType.SingleOrNoValue, Template = "--dry-run", Description = "Don't actually mark, just output.")]
        public bool DryRun { get; set; }

        [Option(CommandOptionType.SingleOrNoValue, Template = "-v|--verbose", Description = "Output when a score is preserved too.")]
        public bool Verbose { get; set; }

        [Option(Description = "Optional where clause", Template = "--where")]
        public string Where { get; set; } = "1 = 1";

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            if (!DryRun)
                elasticQueueProcessor = new ElasticQueuePusher();

            LegacyDatabaseHelper.RulesetDatabaseInfo databaseInfo = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId);

            Console.WriteLine($"Running for ruleset {RulesetId}");
            if (DryRun)
                Console.WriteLine("RUNNING IN DRY RUN MODE.");

            using (var db = await DatabaseAccess.GetConnectionAsync(cancellationToken))
            {
                Console.WriteLine("Fetching all users...");
                int[] userIds = (await db.QueryAsync<int>($"SELECT `user_id` FROM {databaseInfo.UserStatsTable} WHERE {Where}")).ToArray();
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

            IEnumerable<SoloScore> scores = await db.QueryAsync<SoloScore>(new CommandDefinition(
                "SELECT id, beatmap_id, ranked, data, total_score, legacy_total_score, pp FROM scores WHERE preserve = 1 AND user_id = @userId AND ruleset_id = @rulesetId",
                parameters, cancellationToken: cancellationToken));

            if (!scores.Any())
                return;

            IEnumerable<ulong> pins = db.Query<ulong>("SELECT score_id FROM score_pins WHERE user_id = @userId AND ruleset_id = @rulesetId", parameters);
            IEnumerable<ulong> multiplayerScores = db.Query<ulong>("SELECT score_id FROM multiplayer_playlist_item_scores WHERE user_id = @userId", parameters);

            Console.WriteLine($"Processing user {userId} ({scores.Count()} scores)..");

            foreach (var score in scores)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // check whether this score is a user high (either total_score or pp)
                if (checkIsUserHigh(scores, score, out var preservedAlternatives))
                {
                    if (Verbose)
                        formatOutput(score, false, "user high");
                    continue;
                }

                if (multiplayerScores.Contains(score.id))
                {
                    if (Verbose)
                        formatOutput(score, false, "multiplayer score");
                    continue;
                }

                if (pins.Contains(score.id))
                {
                    if (Verbose)
                        formatOutput(score, false, "pinned score");
                    continue;
                }

                if (Verbose)
                {
                    formatOutput(score, true, "superseded");
                    Console.WriteLine("         -> remaining:");
                    foreach (SoloScore alternative in preservedAlternatives)
                        Console.WriteLine($"            https://osu.ppy.sh/scores/{alternative.id}");
                }
                else
                {
                    Console.WriteLine($"Marking score {score.id} for deletion");
                }

                if (!DryRun)
                {
                    await db.ExecuteAsync("UPDATE scores SET preserve = 0, unix_updated_at = UNIX_TIMESTAMP() WHERE id = @scoreId;", new
                    {
                        scoreId = score.id
                    });

                    elasticQueueProcessor!.PushToQueue(new ElasticQueuePusher.ElasticScoreItem
                    {
                        ScoreId = (long?)score.id
                    });
                }
            }
        }

        private static void formatOutput(SoloScore score, bool delete, string reason)
        {
            Console.WriteLine(delete
                ? $"[DELETE] https://osu.ppy.sh/scores/{score.id}#beatmap_id={score.beatmap_id,-10} ({reason})"
                : $"[ KEEP ] https://osu.ppy.sh/scores/{score.id}#beatmap_id={score.beatmap_id,-10} ({reason})"
            );
        }

        private static bool checkIsUserHigh(IEnumerable<SoloScore> userScores, SoloScore candidate, out HashSet<SoloScore> preservedAlternatives)
        {
            var scores = userScores.Where(s =>
                s.beatmap_id == candidate.beatmap_id
                && compareMods(candidate, s)
                && s.ranked
            );

            // As a special case, if the score we are checking is non-ranked, preserve ranked alternatives but if there are none, compare against non-ranked instead.
            if (!candidate.ranked && !scores.Any())
            {
                scores = userScores.Where(s =>
                    s.beatmap_id == candidate.beatmap_id
                    && compareMods(candidate, s)
                );
            }

            Debug.Assert(scores.Any());

            preservedAlternatives = new HashSet<SoloScore>();

            // TODO: this can likely be optimised (to not recalculate every score, in the case there's many candidates per beatmap).
            if (scores.MaxBy(s => s.pp) is SoloScore maxPPScore)
                preservedAlternatives.Add(maxPPScore);
            if (scores.Where(s => s.legacy_total_score == 0).MaxBy(s => s.total_score) is SoloScore maxTotalScoreLazer)
                preservedAlternatives.Add(maxTotalScoreLazer);
            // i'm not sure that we need this one but for now let's play it safe and not nuke scores users may care about.
            if (scores.Where(s => s.legacy_total_score > 0).MaxBy(s => s.legacy_total_score) is SoloScore maxTotalScoreStable)
                preservedAlternatives.Add(maxTotalScoreStable);
            // there's a very high possibility that this one is either `maxTotalScoreLazer` or `maxTotalScoreStable`, but just to be 100% sure...
            if (scores.MaxBy(s => s.total_score) is SoloScore maxTotalScore)
                preservedAlternatives.Add(maxTotalScore);

            // Check whether this score is the user's highest
            return preservedAlternatives.Any(s => s.id == candidate.id);

            static bool compareMods(SoloScore a, SoloScore b)
            {
                // Compare non-ordered mods, ignoring any settings applied.
                var aMods = new HashSet<string>(a.ScoreData.Mods.Select(m => m.Acronym));
                var bMods = new HashSet<string>(b.ScoreData.Mods.Select(m => m.Acronym));

                return aMods.SetEquals(bMods);
            }
        }
    }
}
