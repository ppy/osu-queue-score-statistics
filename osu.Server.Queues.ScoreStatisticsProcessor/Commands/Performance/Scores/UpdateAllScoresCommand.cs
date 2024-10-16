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

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Performance.Scores
{
    [Command(Name = "all", Description = "Computes pp of all scores from all users.")]
    public class UpdateAllScoresCommand : PerformanceCommand
    {
        private const int max_users_per_query = 10000;

        [Option(Description = "Score ID to start processing from.")]
        public ulong From { get; set; }

        protected override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
        {
            LegacyDatabaseHelper.RulesetDatabaseInfo databaseInfo = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId);

            ulong? totalCount;
            ulong currentScoreId = From;

            using (var db = DatabaseAccess.GetConnection())
            {
                totalCount = await db.QuerySingleAsync<ulong>("SELECT MAX(id) FROM scores");
            }

            Console.WriteLine($"Processing all scores starting from {currentScoreId}");

            int processedCount = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                ulong[] scoreIds;

                using (var db = DatabaseAccess.GetConnection())
                {
                    scoreIds = (await db.QueryAsync<ulong>($"SELECT id FROM scores WHERE `id` > @ScoreId ORDER BY `id` LIMIT @Limit", new
                    {
                        ScoreId = currentScoreId,
                        Limit = max_users_per_query
                    })).ToArray();
                }

                if (scoreIds.Length == 0)
                    break;

                await ProcessPartitioned(scoreIds, async id =>
                {
                    using (var db = DatabaseAccess.GetConnection())
                        await ScoreProcessor.ProcessScoreAsync(id, db);

                    if (Interlocked.Increment(ref processedCount) % 1000 == 0)
                        Console.WriteLine($"Processed {processedCount} of {totalCount}");
                }, cancellationToken);

                currentScoreId = scoreIds.Max();
            }

            return 0;
        }
    }
}
