// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor;

namespace osu.Server.Queues.ScorePump.Performance.Totals
{
    [Command("all", Description = "Updates the total PP of all users.")]
    public class UpdateTotalsAllCommand : PerformanceCommand
    {
        [Option(CommandOptionType.SingleValue, Template = "-r|--ruleset", Description = "The ruleset to process score for.")]
        public int RulesetId { get; set; }

        protected override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
        {
            LegacyDatabaseHelper.RulesetDatabaseInfo databaseInfo = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId);

            int[] userIds;

            using (var db = Queue.GetDatabaseConnection())
                userIds = (await db.QueryAsync<int>($"SELECT `user_id` FROM {databaseInfo.UserStatsTable}")).ToArray();

            Console.WriteLine($"Processed 0 of {userIds.Length}");

            int processedCount = 0;

            await ProcessPartitioned(userIds, async id =>
            {
                using (var db = Queue.GetDatabaseConnection())
                {
                    var userStats = await DatabaseHelper.GetUserStatsAsync(id, RulesetId, db);

                    // Only process users with an existing rank_score.
                    if (userStats!.rank_score == 0)
                        return;

                    await Processor.UpdateUserStatsAsync(userStats, RulesetId, db);
                    await DatabaseHelper.UpdateUserStatsAsync(userStats, db);
                }

                Console.WriteLine($"Processed {Interlocked.Increment(ref processedCount)} of {userIds.Length}");
            }, cancellationToken);

            return 0;
        }
    }
}