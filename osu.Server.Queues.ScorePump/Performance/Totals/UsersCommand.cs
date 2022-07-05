// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor;

namespace osu.Server.Queues.ScorePump.Performance.Totals
{
    [Command("users", Description = "Updates the total PP of specific users.")]
    public class UsersCommand : PerformanceCommand
    {
        [UsedImplicitly]
        [Required]
        [Argument(0, Description = "A space-separated list of users to compute PP for.")]
        public int[] UserIds { get; set; } = null!;

        [Option(CommandOptionType.SingleValue, Template = "-r|--ruleset", Description = "The ruleset to process score for.")]
        public int RulesetId { get; set; }

        protected override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine($"Processed 0 of {UserIds.Length}");

            int processedCount = 0;

            await ProcessPartitioned(UserIds, async id =>
            {
                using (var db = Queue.GetDatabaseConnection())
                {
                    var userStats = await DatabaseHelper.GetUserStatsAsync(id, RulesetId, db);
                    await Processor.UpdateUserStatsAsync(userStats!, RulesetId, db);
                    await DatabaseHelper.UpdateUserStatsAsync(userStats!, db);
                }

                Console.WriteLine($"Processed {Interlocked.Increment(ref processedCount)} of {UserIds.Length}");
            }, cancellationToken);

            return 0;
        }
    }
}
