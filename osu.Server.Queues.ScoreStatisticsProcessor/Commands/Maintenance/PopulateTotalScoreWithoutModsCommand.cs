// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;
using MySqlConnector;
using osu.Game.Scoring.Legacy;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("populate-total-score-without-mods", Description = "Populates total score without mods for scores that have it missing")]
    public class PopulateTotalScoreWithoutModsCommand
    {
        [Option(CommandOptionType.SingleValue, Template = "--start-id")]
        public ulong? StartId { get; set; }

        [Option(CommandOptionType.SingleValue, Template = "--batch-size")]
        public int BatchSize { get; set; } = 5000;

        [Option(CommandOptionType.SingleOrNoValue, Template = "--dry-run")]
        public bool DryRun { get; set; }

        private readonly StringBuilder sqlBuffer = new StringBuilder();

        [UsedImplicitly]
        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            ulong lastId = StartId ?? 0;
            ulong backfills = 0;

            using var conn = DatabaseAccess.GetConnection();

            Console.WriteLine();
            Console.WriteLine($"Populating total score without mods on scores without it, starting from ID {lastId}");

            if (DryRun)
                Console.WriteLine("RUNNING IN DRY RUN MODE.");

            await Task.Delay(5000, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var scoresWithMissing = (await conn.QueryAsync<SoloScore>(
                    // WARNING: the query below MUST use `JSON_VALUE` because mysql is being weird with the standard arrow syntax.
                    // if `data` does not have `total_score_without_mods` set at all, `data->'$.total_score_without_mods'` will return the typical SQL NULL,
                    // but if `data` has `{'total_score_without_mods': null}`, then `data->'$.total_score_without_mods'` will return `CAST('null' AS JSON)` which IS NOT NULL.
                    // We want to be matching both of these cases. Thus, we use `JSON_VALUE`, which bypasses this footgun.
                    "SELECT * FROM scores WHERE `id` BETWEEN @lastId AND (@lastId + @batchSize - 1) AND JSON_VALUE(`data`, '$.total_score_without_mods') IS NULL ORDER BY `id`",
                    new
                    {
                        lastId,
                        batchSize = BatchSize,
                    })).ToArray();

                if (scoresWithMissing.Length == 0)
                {
                    if (lastId > await conn.QuerySingleAsync<ulong>("SELECT MAX(id) FROM scores"))
                    {
                        Console.WriteLine("All done!");
                        break;
                    }

                    lastId += (ulong)BatchSize;
                    continue;
                }

                uint[] beatmapIds = scoresWithMissing.Select(score => score.beatmap_id).Distinct().ToArray();
                var beatmapsById = (await conn.QueryAsync<Beatmap>(@"SELECT * FROM `osu_beatmaps` WHERE `beatmap_id` IN @ids", new { ids = beatmapIds }))
                    .ToDictionary(beatmap => beatmap.beatmap_id);

                foreach (var score in scoresWithMissing)
                {
                    score.beatmap = beatmapsById[score.beatmap_id];
                    var scoreInfo = score.ToScoreInfo();
                    LegacyScoreDecoder.PopulateTotalScoreWithoutMods(scoreInfo);

                    Console.WriteLine($"Updating score {score.id} to {scoreInfo.TotalScoreWithoutMods} (without mods) / {score.total_score} (with mods)");

                    // `JSON_SET` is used because it inserts the key-value pair if the key is completely missing
                    // and replaces the value (presumed NULL due to the filter above) if the key is present.
                    sqlBuffer.Append($@"UPDATE `scores` SET `data` = JSON_SET(`data`, '$.total_score_without_mods', {scoreInfo.TotalScoreWithoutMods}) WHERE `id` = {score.id};");
                    backfills++;
                }

                lastId += (ulong)BatchSize;

                Console.WriteLine($"Processed up to {lastId - 1} ({backfills} backfilled)");

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

            if (DryRun)
            {
                sqlBuffer.Clear();
                return;
            }

            if (bufferLength > 1024 || force)
            {
                Console.WriteLine();
                Console.WriteLine($"Flushing sql batch ({bufferLength:N0} bytes)");
                conn.Execute(sqlBuffer.ToString());
            }
        }
    }
}
