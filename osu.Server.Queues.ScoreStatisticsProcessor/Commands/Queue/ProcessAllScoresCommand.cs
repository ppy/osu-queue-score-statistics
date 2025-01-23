// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Queue
{
    [Command("process-all", Description = "Pumps all existing `scores` scores through the queue for reprocessing")]
    public class ProcessAllScoresCommand
    {
        /// <summary>
        /// A comma-separated list of processors to disable.
        /// </summary>
        [Option("--disable", Description = "A comma-separated list of processors to disable.")]
        public string DisabledProcessors { get; set; } = string.Empty;

        [Option("--force", Description = "When set, reprocess the score even if it was already processed up to the current version.")]
        public bool Force { get; set; } = false;

        [Option("--start-id")]
        public long StartId { get; set; }

        [Option("--delay", Description = "Delay in milliseconds between queue operations")]
        public int Delay { get; set; }

        [Option("--sql", Description = "Specify a custom query to limit the scope of pumping")]
        public string? CustomQuery { get; set; }

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            string? disabledProcessorsEnv = Environment.GetEnvironmentVariable("DISABLED_PROCESSORS");

            if (!string.IsNullOrEmpty(disabledProcessorsEnv))
            {
                if (!string.IsNullOrEmpty(DisabledProcessors))
                    throw new ArgumentException("Attempted to specify disabled processors via parameter and environment at the same time");

                DisabledProcessors = disabledProcessorsEnv;
            }

            ScoreStatisticsQueueProcessor queue = new ScoreStatisticsQueueProcessor(DisabledProcessors.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

            using (var dbMainQuery = await DatabaseAccess.GetConnectionAsync(cancellationToken))
            using (var db = await DatabaseAccess.GetConnectionAsync(cancellationToken))
            {
                string query = "SELECT * FROM scores WHERE id >= @StartId";

                if (!string.IsNullOrEmpty(CustomQuery))
                    query += $" AND {CustomQuery}";

                Console.WriteLine($"Querying with \"{query}\"");
                var scores = dbMainQuery.Query<SoloScore>(query, new { startId = StartId }, buffered: false);

                foreach (var score in scores)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var history = await db.QuerySingleOrDefaultAsync<ProcessHistory?>("SELECT * FROM score_process_history WHERE score_id = @score_id", new { score_id = score.id });

                    Console.WriteLine($"Processing score {score}");

                    Console.WriteLine(history == null
                        ? "- Score has not yet been processed"
                        : $"- Attaching process history with version {history.processed_version}");

                    var scoreItem = new ScoreItem(score, history);

                    queue.ProcessScore(scoreItem, Force);

                    if (scoreItem.Tags?.Any() == true)
                    {
                        Console.WriteLine("Processing completed with tags:");
                        Console.WriteLine(string.Join(", ", scoreItem.Tags));
                    }
                    else
                    {
                        Console.WriteLine("Processing completed but no tags were appended.");
                    }

                    Console.WriteLine();
                }
            }

            return 0;
        }
    }
}
