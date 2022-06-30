// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;

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

        protected override async Task<int> Execute(CommandLineApplication app)
        {
            Console.WriteLine($"Processed 0 of {UserIds.Length}");

            int processedCount = 0;
            await Task.WhenAll(Partitioner
                               .Create(UserIds)
                               .GetPartitions(Threads)
                               .AsParallel()
                               .Select(processPartition));

            return 0;

            async Task processPartition(IEnumerator<int> partition)
            {
                using (partition)
                {
                    while (partition.MoveNext())
                    {
                        await Task.Yield();

                        await Processor.UpdateTotals(partition.Current, RulesetId);

                        Console.WriteLine($"Processed {Interlocked.Increment(ref processedCount)} of {UserIds.Length}");
                    }
                }
            }
        }
    }
}
