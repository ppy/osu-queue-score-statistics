// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using Dapper;
using Dapper.Contrib.Extensions;
using McMaster.Extensions.CommandLineUtils;
using osu.Game.IO.Serialization;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScorePump
{
    [Command("update-v2", Description = "Converts all scores to new storage format")]
    public class ConvertTableStructureV2 : ScorePump
    {
        [Option("--start_id")]
        public long StartId { get; set; }

        public int OnExecute(CancellationToken cancellationToken)
        {
            using (var dbMainQuery = Queue.GetDatabaseConnection())
            using (var db = Queue.GetDatabaseConnection())
            {
                const string query = "SELECT * FROM solo_scores WHERE id >= @StartId";

                Console.WriteLine($"Querying with \"{query}\"");
                var scores = dbMainQuery.Query<SoloScore>(query, this, buffered: false);

                foreach (var score in scores)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // convert to new score format
                    Console.WriteLine($"Reading score {score}");

                    score.ended_at ??= score.started_at;

                    var data = score.Serialize();

                    SoloScoreV2 scorev2 = new SoloScoreV2
                    {
                        id = score.id,
                        user_id = score.user_id,
                        beatmap_id = score.beatmap_id,
                        ruleset_id = score.ruleset_id,

                        created_at = score.started_at,
                        updated_at = score.updated_at,
                        deleted_at = score.deleted_at,

                        data = data,
                    };

                    // insert
                    db.Insert(scorev2);
                }
            }

            return 0;
        }
    }
}
