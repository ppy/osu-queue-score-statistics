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
using osu.Game.Rulesets.Scoring;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
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

        [Option(CommandOptionType.SingleOrNoValue, Template = "-v|--verbose", Description = "Per-score output.")]
        public bool Verbose { get; set; }

        private readonly StringBuilder sqlBuffer = new StringBuilder();

        [UsedImplicitly]
        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            ulong lastId = StartId ?? 0;
            ulong backfills = 0;

            using var conn = await DatabaseAccess.GetConnectionAsync(cancellationToken);

            Console.WriteLine();
            Console.WriteLine($"Populating total score without mods on scores without it, starting from ID {lastId}");

            if (DryRun)
                Console.WriteLine("RUNNING IN DRY RUN MODE.");

            await Task.Delay(5000, cancellationToken);

            // prefetch a mapping of lazer / tachyon build IDs to string versions for later use.
            // note that `osu_builds` has two kinds of rows for builds: "main" rows (with `stream_id` set),
            // and "platform-specific" rows (with platform suffixes in `version`, as well as null `stream_id`).
            // `scores.build_id` points at the *platform-specific* rows, so filtering by stream ID cannot be easily done here.
            var lazerBuildVersionsById = (await conn.QueryAsync<Build>(@"SELECT `build_id`, `version` FROM `osu_builds`"))
                .ToDictionary(build => build.build_id, build => build.version);

            while (!cancellationToken.IsCancellationRequested)
            {
                var scoresToPopulate = (await conn.QueryAsync<SoloScore>(
                    // conditions for backpopulation:
                    // - score must not be legacy.
                    //   the totals of legacy scores are already computed from `legacy_total_score` via `StandardisedScoreMigrationTools`,
                    //   so populating `total_score_without_mods` is unnecessary in that case, `legacy_total_score` is sufficient for lossless mod multiplier changes.
                    // - score must have mods.
                    //   it is assumed that a score without mods cannot have any other score multiplier than 1.0x,
                    //   and therefore it would hold that `total_score_without_mods` == `total_score`.
                    // - score must not have total score without mods already populated.
                    //
                    // WARNING: the query below MUST use `JSON_VALUE` to read `total_score_without_mods` because mysql is being weird with the standard arrow syntax.
                    // if `data` does not have `total_score_without_mods` set at all, `data->'$.total_score_without_mods'` will return the typical SQL NULL,
                    // but if `data` has `{'total_score_without_mods': null}`, then `data->'$.total_score_without_mods'` will return `CAST('null' AS JSON)` which IS NOT NULL.
                    // We want to be matching both of these cases. Thus, we use `JSON_VALUE`, which bypasses this footgun.
                    """
                    SELECT * FROM scores
                    WHERE `id` BETWEEN @lastId AND (@lastId + @batchSize - 1)
                        AND `legacy_score_id` IS NULL
                        AND JSON_LENGTH(`data`, '$.mods') > 0
                        AND JSON_VALUE(`data`, '$.total_score_without_mods') IS NULL
                    ORDER BY `id`
                    """,
                    new
                    {
                        lastId,
                        batchSize = BatchSize,
                    })).ToArray();

                if (scoresToPopulate.Length == 0)
                {
                    if (lastId > await conn.QuerySingleAsync<ulong>("SELECT MAX(id) FROM scores"))
                    {
                        Console.WriteLine("All done!");
                        break;
                    }

                    lastId += (ulong)BatchSize;
                    continue;
                }

                var beatmapIds = scoresToPopulate.Select(score => score.beatmap_id).ToHashSet();
                var beatmapsById = (await conn.QueryAsync<Beatmap>(@"SELECT * FROM `osu_beatmaps` WHERE `beatmap_id` IN @ids", new { ids = beatmapIds }))
                    .ToDictionary(beatmap => beatmap.beatmap_id);

                foreach (var score in scoresToPopulate)
                {
                    if (!beatmapsById.TryGetValue(score.beatmap_id, out var beatmap))
                    {
                        Console.WriteLine($"Skipping score {score.id} (missing beatmap {score.beatmap_id})");
                        continue;
                    }

                    score.beatmap = beatmap;
                    var scoreInfo = score.ToScoreInfo();

                    if (score.build_id == null || !lazerBuildVersionsById.TryGetValue(score.build_id.Value, out string? buildVersion))
                        throw new InvalidOperationException($"Aborting: score {score.id} has missing or invalid build ID of {score.build_id}!");

                    scoreInfo.ClientVersion = buildVersion;

                    var ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(scoreInfo.RulesetID);

                    var scoreMultiplierCalculator = ruleset.CreateScoreMultiplierCalculator(new ScoreMultiplierContext(beatmap.GetLegacyBeatmapConversionDifficultyInfo(), scoreInfo));
                    double modMultiplier = scoreMultiplierCalculator.CalculateFor(scoreInfo.Mods);
                    scoreInfo.TotalScoreWithoutMods = (long)Math.Round(scoreInfo.TotalScore / modMultiplier);

                    if (Verbose)
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
                sqlBuffer.Clear();
            }
        }
    }
}
