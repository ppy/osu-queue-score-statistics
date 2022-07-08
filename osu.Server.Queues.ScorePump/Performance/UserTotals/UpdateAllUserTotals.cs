// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor;

namespace osu.Server.Queues.ScorePump.Performance.UserTotals
{
    [Command("all", Description = "Updates the total PP of all users.")]
    public class UpdateAllUserTotals : PerformanceCommand
    {
        protected override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
        {
            LegacyDatabaseHelper.RulesetDatabaseInfo databaseInfo = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId);

            int[] userIds;

            Console.WriteLine("Fetching all users...");

            using (var db = Queue.GetDatabaseConnection())
                userIds = (await db.QueryAsync<int>($"SELECT `user_id` FROM {databaseInfo.UserStatsTable}")).ToArray();

            Console.WriteLine($"Fetched {userIds.Length} users");

            await ProcessUserTotals(userIds, cancellationToken);
            return 0;
        }
    }
}
