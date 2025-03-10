// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Performance.UserTotals
{
    [Command("all", Description = "Updates the total PP of all users.")]
    public class UpdateAllUserTotalsCommand : PerformanceCommand
    {
        protected override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
        {
            LegacyDatabaseHelper.RulesetDatabaseInfo databaseInfo = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId);

            uint[] userIds;

            Console.WriteLine("Fetching all users...");

            using (var db = await DatabaseAccess.GetConnectionAsync(cancellationToken))
                userIds = (await db.QueryAsync<uint>($"SELECT {databaseInfo.UserStatsTable}.`user_id` FROM {databaseInfo.UserStatsTable} JOIN {databaseInfo.UsersTable} USING (user_id) WHERE user_warnings = 0")).ToArray();

            Console.WriteLine($"Fetched {userIds.Length} users");

            await ProcessUserTotals(userIds, cancellationToken);
            return 0;
        }
    }
}
