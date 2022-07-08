// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump.Performance.Values
{
    [Command("users", Description = "Computes pp of specific users.")]
    public class UpdateValuesUsersCommand : PerformanceCommand
    {
        [UsedImplicitly]
        [Required]
        [Argument(0, Description = "A comma-separated list of users to compute PP for.")]
        public string UsersString { get; set; } = string.Empty;

        [Option(CommandOptionType.SingleValue, Template = "-r|--ruleset", Description = "The ruleset to process score for.")]
        public int RulesetId { get; set; }

        protected override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
        {
            ulong[] userIds = ParseIds(UsersString);

            Console.WriteLine($"Processed 0 of {userIds.Length}");

            int processedCount = 0;

            await ProcessPartitioned(userIds, async id =>
            {
                using (var db = Queue.GetDatabaseConnection())
                    await Processor.ProcessUserScoresAsync((uint)id, RulesetId, db);
                Console.WriteLine($"Processed {Interlocked.Increment(ref processedCount)} of {userIds.Length}");
            }, cancellationToken);

            return 0;
        }
    }
}
