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
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("verify-user-ranked-score", Description = "Verified ranked score for users.")]
    public class VerifyUserRankedScore
    {
        [Option("--sql", Description = "Specify a custom query to limit the scope of pumping")]
        public string? CustomQuery { get; set; }

        /// <summary>
        /// The ruleset to run this verify job for.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "-r|--ruleset-id")]
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

                if (Verbose)
                    Console.WriteLine($"Processing user {userId} ({scores.Count()} scores)..");

                IEnumerable<SoloScore?> maxScoresByBeatmap = scores.GroupBy(s => s.beatmap_id)
                                                                   .Select(g => g.OrderByDescending(s => s.total_score)
                                                                                 .ThenByDescending(s => s.id)
                                                                                 .FirstOrDefault());

                long totalRankedScore = maxScoresByBeatmap.Sum(s => s!.total_score);

                var userStats = await DatabaseHelper.GetUserStatsAsync(userId, RulesetId, db, transaction);

                if (userStats == null)
                    return;

                bool userHasCorrectTotal =
                    userStats.ranked_score == totalRankedScore;

                if (!userHasCorrectTotal)
                {
                    if (Verbose)
                    {
                        Console.WriteLine($"Fixing incorrect ranked score for {userId}");
                        Console.WriteLine($"{userStats.ranked_score} ->  {totalRankedScore}");
                        Console.WriteLine();
                    }

                    userStats.ranked_score = totalRankedScore;

                    if (!DryRun)
                        await DatabaseHelper.UpdateUserStatsAsync(userStats, db, transaction);
                }

                if (DryRun)
                    await transaction.RollbackAsync(cancellationToken);
                else
                    await transaction.CommitAsync(cancellationToken);
            }
        }
    }
}
