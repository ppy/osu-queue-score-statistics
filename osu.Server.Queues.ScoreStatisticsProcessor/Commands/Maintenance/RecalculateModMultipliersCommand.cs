// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;
using MySqlConnector;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("recalculate-mod-multipliers", Description = "Recalculates total score after a change to mod multipliers")]
    public class RecalculateModMultipliersCommand
    {
        [Option(CommandOptionType.SingleValue, Template = "--start-id")]
        public ulong? StartId { get; set; }

        [Option(CommandOptionType.SingleValue, Template = "--batch-size")]
        public int BatchSize { get; set; } = 5000;

        [Option(CommandOptionType.SingleOrNoValue, Template = "--dry-run")]
        public bool DryRun { get; set; }

        private readonly StringBuilder sqlBuffer = new StringBuilder();

        private readonly ElasticQueuePusher elasticQueuePusher = new ElasticQueuePusher();
        private readonly List<ElasticQueuePusher.ElasticScoreItem> elasticItems = new List<ElasticQueuePusher.ElasticScoreItem>();

        [UsedImplicitly]
        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            ulong lastId = StartId ?? 0;
            ulong updatedScores = 0;

            using var conn = DatabaseAccess.GetConnection();

            Console.WriteLine();
            Console.WriteLine($"Recalculating total score in line with new mod multipliers, starting from ID {lastId}");
            Console.WriteLine($"Indexing to elastic queue(s) {elasticQueuePusher.ActiveQueues}");

            if (DryRun)
                Console.WriteLine("RUNNING IN DRY RUN MODE.");

            await Task.Delay(5000, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var scoresWithMods = (await conn.QueryAsync<SoloScore>(
                    "SELECT * FROM `scores` WHERE `id` BETWEEN @lastId AND (@lastId + @batchSize - 1) AND JSON_LENGTH(`data`, '$.mods') > 0",
                    new
                    {
                        lastId,
                        batchSize = BatchSize,
                    })).ToArray();

                if (scoresWithMods.Length == 0)
                {
                    if (lastId > await conn.QuerySingleAsync<ulong>("SELECT MAX(id) FROM scores"))
                    {
                        Console.WriteLine("All done!");
                        break;
                    }

                    lastId += (ulong)BatchSize;
                    continue;
                }

                uint[] beatmapIds = scoresWithMods.Select(score => score.beatmap_id).Distinct().ToArray();
                var beatmapsById = (await conn.QueryAsync<Beatmap>(@"SELECT * FROM `osu_beatmaps` WHERE `beatmap_id` IN @ids", new { ids = beatmapIds }))
                    .ToDictionary(beatmap => beatmap.beatmap_id);

                foreach (var score in scoresWithMods)
                {
                    score.beatmap = beatmapsById[score.beatmap_id];
                    var scoreInfo = score.ToScoreInfo();

                    double multiplier = 1;

                    foreach (var mod in scoreInfo.Mods)
                        multiplier *= mod.ScoreMultiplier;

                    long newTotalScore = (long)Math.Round(scoreInfo.TotalScoreWithoutMods * multiplier);

                    if (newTotalScore == scoreInfo.TotalScore)
                        continue;

                    Console.WriteLine($"Updating score {score.id}. Without mods: {scoreInfo.TotalScoreWithoutMods}. With mods: {scoreInfo.TotalScore} (old) -> {newTotalScore} (new)");

                    sqlBuffer.Append($@"UPDATE `scores` SET `total_score` = {newTotalScore} WHERE `id` = {score.id};");
                    elasticItems.Add(new ElasticQueuePusher.ElasticScoreItem { ScoreId = (long?)score.id });
                    updatedScores++;
                }

                lastId += (ulong)BatchSize;

                Console.WriteLine($"Processed up to {lastId - 1} ({updatedScores} updated)");

                flush(conn);
            }

            flush(conn, true);

            return 0;
        }

        private void flush(MySqlConnection conn, bool force = false)
        {
            int bufferLength = sqlBuffer.Length;

            if (bufferLength == 0)
                return;

            if (bufferLength > 1024 || force)
            {
                if (!DryRun)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Flushing sql batch ({bufferLength:N0} bytes)");
                    conn.Execute(sqlBuffer.ToString());

                    if (elasticItems.Count > 0)
                    {
                        elasticQueuePusher.PushToQueue(elasticItems.ToList());
                        Console.WriteLine($"Queued {elasticItems.Count} items for indexing");
                    }
                }

                elasticItems.Clear();
                sqlBuffer.Clear();
            }
        }
    }
}
