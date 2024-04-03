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

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("reindex-beatmap", Description = "Queue all scores from a beatmap for reindexing.")]
    public class ReindexBeatmapCommand
    {
        /// <summary>
        /// The beatmap to reindex.
        /// </summary>
        [Argument(0, Description = "The beatmap to reindex.")]
        public int BeatmapId { get; set; }

        private readonly ElasticQueuePusher elasticQueueProcessor = new ElasticQueuePusher();

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine($"Indexing to elasticsearch queue(s) {elasticQueueProcessor.ActiveQueues}");

            using var conn = DatabaseAccess.GetConnection();

            var scores = (await conn.QueryAsync<SoloScore>("SELECT id FROM scores WHERE beatmap_id = @beatmapId AND preserve = 1", new
            {
                beatmapId = BeatmapId
            })).ToArray();

            Console.WriteLine($"Pushing {scores.Length} scores for reindexing...");

            elasticQueueProcessor.PushToQueue(scores.Select(s => new ElasticQueuePusher.ElasticScoreItem
            {
                ScoreId = (long)s.id
            }).ToList());

            Console.WriteLine("Done!");
            return 0;
        }
    }
}
