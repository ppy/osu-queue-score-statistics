// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump.Performance.Values
{
    [Command("users", Description = "Computes pp of specific users.")]
    public class UsersCommand : PerformanceCommand
    {
        [Required]
        [Argument(0, Description = "A space-separated list of users to compute PP for.")]
        public uint[] UserIds { get; set; } = null!;

        [Option(CommandOptionType.SingleValue, Template = "-r|--ruleset", Description = "The ruleset to process score for.")]
        public int RulesetId { get; set; }

        protected override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine($"Processed 0 of {UserIds.Length}");

            int processedCount = 0;

            await ProcessPartitioned(UserIds, async id =>
            {
                await Processor.ProcessUserAsync(id, RulesetId);
                Console.WriteLine($"Processed {Interlocked.Increment(ref processedCount)} of {UserIds.Length}");
            }, cancellationToken);

            return 0;
        }
    }
}
