// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Game.Online.API.Requests.Responses;
using osu.Server.QueueProcessor;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("migrate-solo-scores", Description = "Migrate scores from `solo_scores` to `scores` table.")]
    public class MigrateSoloScoresCommand
    {
        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            using var db = DatabaseAccess.GetConnection();

            foreach (var score in db.Query<SoloScoreInfo>("SELECT * FROM solo_scores JOIN multiplayer_score_links_old ON (id = score_id)", buffered: false))
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                Console.WriteLine($"Processing score {score.ID}...");

                if (cancellationToken.IsCancellationRequested)
                    break;
            }

            Console.WriteLine("Finished.");
            return 0;
        }
    }
}
