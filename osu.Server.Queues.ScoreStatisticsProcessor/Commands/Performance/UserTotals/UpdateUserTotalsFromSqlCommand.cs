// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Performance.UserTotals
{
    [Command("sql", Description = "Updates the total PP of scoped users.")]
    public class UpdateUserTotalsFromSqlCommand : PerformanceCommand
    {
        [UsedImplicitly]
        [Required]
        [Argument(0, Description = "Specify a custom query to limit the scope of users to process.")]
        public string? Query { get; set; }

        protected override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
        {
            using var db = DatabaseAccess.GetConnection();

            LegacyDatabaseHelper.RulesetDatabaseInfo databaseInfo = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId);

            Console.WriteLine($"Fetching scoped users ({Query})...");

            uint[] userIds = (await db.QueryAsync<uint>($"SELECT `user_id` FROM {databaseInfo.UserStatsTable} WHERE {Query}")).ToArray();

            Console.WriteLine($"Fetched {userIds.Length} users");

            await ProcessUserTotals(userIds, cancellationToken);
            return 0;
        }
    }
}
