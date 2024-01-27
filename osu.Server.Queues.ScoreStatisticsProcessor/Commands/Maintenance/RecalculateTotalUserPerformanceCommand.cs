// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Processors;

// ReSharper disable InconsistentNaming
namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("recalculate-total-user-performance", Description = "Process all users' total performance")]
    public class RecalculateTotalUserPerformanceCommand
    {
        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            var processor = new UserTotalPerformanceProcessor();

            using (var db = DatabaseAccess.GetConnection())
            {
                Stopwatch sw = new Stopwatch();

                foreach (var score in await db.QueryAsync<SoloScore>("SELECT id FROM scores WHERE legacy_score_id IS NULL"))
                {
                    var score2 = db.QuerySingle<SoloScore>($"SELECT id, user_id, ruleset_id FROM scores WHERE id = {score.id}");
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    sw.Restart();

                    processor.ApplyToUserStats(score2.ToScoreInfo(), new UserStatsOsu { user_id = (int)score2.user_id }, db, null!);

                    Console.WriteLine($"Recalculating {score2.user_id} took {sw.ElapsedMilliseconds:N0} ms");
                }
            }

            Console.WriteLine("Finished.");
            return 0;
        }
    }
}
