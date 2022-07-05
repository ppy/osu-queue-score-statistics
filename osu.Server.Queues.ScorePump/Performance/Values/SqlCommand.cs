// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump.Performance.Values
{
    [Command(Name = "sql", Description = "Computes pp of users given by an SQL select statement.")]
    public class SqlCommand : PerformanceCommand
    {
        [Required]
        [Argument(0, Description = "The SQL statement selecting the user ids to compute.")]
        public string Statement { get; set; } = null!;

        [Option(CommandOptionType.SingleValue, Template = "-r|--ruleset", Description = "The ruleset to process score for.")]
        public int RulesetId { get; set; }

        protected override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
        {
            uint[] userIds;

            using (var db = Queue.GetDatabaseConnection())
                userIds = (await db.QueryAsync<uint>(Statement)).ToArray();

            if (userIds.Length == 0)
                throw new InvalidOperationException("SQL query returned 0 users to process.");

            Console.WriteLine($"Processed 0 of {userIds.Length}");

            int processedCount = 0;

            await ProcessPartitioned(userIds, async id =>
            {
                using (var db = Queue.GetDatabaseConnection())
                    await Processor.ProcessUserScoresAsync(id, RulesetId, db);
                Console.WriteLine($"Processed {Interlocked.Increment(ref processedCount)} of {userIds.Length}");
            }, cancellationToken);

            return 0;
        }
    }
}
