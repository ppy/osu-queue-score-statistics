// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Queue
{
    [Command("pump-all", Description = "Pumps all existing `solo_scores` scores through the queue for reprocessing")]
    public class PumpAllScoresCommand : BaseCommand
    {
        [Option("--start_id")]
        public long StartId { get; set; }

        [Option("--delay", Description = "Delay in milliseconds between queue operations")]
        public int Delay { get; set; }

        [Option("--sql", Description = "Specify a custom query to limit the scope of pumping")]
        public string? CustomQuery { get; set; }

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            using (var dbMainQuery = Queue.GetDatabaseConnection())
            using (var db = Queue.GetDatabaseConnection())
            {
                string query = $"SELECT * FROM {SoloScore.TABLE_NAME} WHERE id >= @StartId";

                if (!string.IsNullOrEmpty(CustomQuery))
                    query += $" AND {CustomQuery}";

                Console.WriteLine($"Querying with \"{query}\"");
                var scores = dbMainQuery.Query<SoloScore>(query, new { startId = StartId }, buffered: false);

                foreach (var score in scores)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // attach any previous processing information
                    var history = db.QuerySingleOrDefault<ProcessHistory>($"SELECT * FROM {ProcessHistory.TABLE_NAME} WHERE score_id = @id", score);

                    Console.WriteLine($"Pumping {score}");
                    Queue.PushToQueue(new ScoreItem(score, history));

                    if (Delay > 0)
                        await Task.Delay(Delay, cancellationToken);
                }
            }

            return 0;
        }
    }
}
