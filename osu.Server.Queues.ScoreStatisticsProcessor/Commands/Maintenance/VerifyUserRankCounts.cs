// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.

// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using MySqlConnector;
using osu.Game.Scoring;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("verify-user-rank-counts", Description = "Verifies SS/S/A rank counts for all users")]
    public class VerifyUserRankCounts
    {
        [Option("--sql", Description = "Specify a custom query to limit the scope of pumping")]
        public string? CustomQuery { get; set; }

        /// <summary>
        /// The ruleset to run this verify job for.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "--ruleset-id")]
        public int RulesetId { get; set; }

        [Option(CommandOptionType.SingleOrNoValue, Template = "-v|--verbose", Description = "Output verbose information on processing.")]
        public bool Verbose { get; set; }

        [Option(CommandOptionType.SingleOrNoValue, Template = "--dry-run")]
        public bool DryRun { get; set; }

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            LegacyDatabaseHelper.RulesetDatabaseInfo databaseInfo = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId);

            Console.WriteLine($"Running for ruleset {RulesetId}");

            if (DryRun)
                Console.WriteLine("RUNNING IN DRY RUN MODE.");

            if (!string.IsNullOrEmpty(CustomQuery))
                CustomQuery = $"WHERE {CustomQuery}";

            using (var db = await DatabaseAccess.GetConnectionAsync(cancellationToken))
            {
                Console.WriteLine("Fetching all users...");
                uint[] userIds = (await db.QueryAsync<uint>($"SELECT `user_id` FROM {databaseInfo.UserStatsTable} {CustomQuery}")).ToArray();
                Console.WriteLine($"Fetched {userIds.Length} users");

                int processedUsers = 0;

                foreach (uint userId in userIds)
                {
                    await processUser(db, userId, cancellationToken);

                    if (++processedUsers % 1000 == 0)
                        Console.WriteLine($"Processed {processedUsers} of {userIds.Length} users");
                }
            }

            return 0;
        }

        private async Task processUser(MySqlConnection db, uint userId, CancellationToken cancellationToken)
        {
            using (var transaction = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            {
                var parameters = new
                {
                    userId,
                    rulesetId = RulesetId,
                };

                IEnumerable<SoloScore> scores = await db.QueryAsync<SoloScore>(new CommandDefinition(
                    "SELECT * FROM scores WHERE preserve = 1 AND ranked = 1 AND user_id = @userId AND ruleset_id = @rulesetId",
                    parameters, cancellationToken: cancellationToken, transaction: transaction));

                var counts = new Dictionary<ScoreRank, uint>
                {
                    { ScoreRank.A, 0 },
                    { ScoreRank.S, 0 },
                    { ScoreRank.SH, 0 },
                    { ScoreRank.X, 0 },
                    { ScoreRank.XH, 0 },
                };

                if (Verbose)
                    Console.WriteLine($"Processing user {userId} ({scores.Count()} scores)..");

                IEnumerable<SoloScore?> maxScoresByBeatmap = scores.GroupBy(s => s.beatmap_id).Select(g => g.MaxBy(s => s.total_score));

                foreach (var score in maxScoresByBeatmap)
                {
                    if (score == null)
                        continue;

                    switch (score.rank)
                    {
                        case ScoreRank.A:
                            counts[ScoreRank.A]++;
                            break;

                        case ScoreRank.S:
                            counts[ScoreRank.S]++;
                            break;

                        case ScoreRank.SH:
                            counts[ScoreRank.SH]++;
                            break;

                        case ScoreRank.X:
                            counts[ScoreRank.X]++;
                            break;

                        case ScoreRank.XH:
                            counts[ScoreRank.XH]++;
                            break;
                    }
                }

                var userStats = await DatabaseHelper.GetUserStatsAsync(userId, RulesetId, db, transaction);

                if (userStats == null)
                    return;

                bool userHasCorrectCounts =
                    userStats.a_rank_count == counts[ScoreRank.A] &&
                    userStats.s_rank_count == counts[ScoreRank.S] &&
                    userStats.sh_rank_count == counts[ScoreRank.SH] &&
                    userStats.x_rank_count == counts[ScoreRank.X] &&
                    userStats.xh_rank_count == counts[ScoreRank.XH];

                if (!userHasCorrectCounts)
                {
                    if (Verbose)
                    {
                        Console.WriteLine($"Fixing incorrect counts for {userId}");
                        Console.WriteLine($"a:  {userStats.a_rank_count} ->  {counts[ScoreRank.A]}");
                        Console.WriteLine($"s:  {userStats.s_rank_count} ->  {counts[ScoreRank.S]}");
                        Console.WriteLine($"sh: {userStats.sh_rank_count} ->  {counts[ScoreRank.SH]}");
                        Console.WriteLine($"x:  {userStats.x_rank_count} ->  {counts[ScoreRank.X]}");
                        Console.WriteLine($"xh: {userStats.xh_rank_count} ->  {counts[ScoreRank.XH]}");
                        Console.WriteLine();
                    }

                    if (!DryRun)
                        await DatabaseHelper.UpdateUserStatsAsync(userStats, db, transaction);
                }

                await transaction.CommitAsync(cancellationToken);
            }
        }
    }
}
