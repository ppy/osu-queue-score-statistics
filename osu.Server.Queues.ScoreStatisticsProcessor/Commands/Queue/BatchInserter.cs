// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using Newtonsoft.Json;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Database;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Scoring.Legacy;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Queue
{
    /// <summary>
    /// Handles one batch insertion of <see cref="HighScore"/>s. Can be used to parallelize work.
    /// </summary>
    /// <remarks>
    /// Importantly, on a process-wide basis (with the requirement that only one import is happening at once from the same source),
    /// scores for the same beatmap should always be inserted using the same <see cref="BatchInserter"/>. This is to ensure that the new
    /// IDs given to inserted scores are still chronologically correct (we fallback to using IDs for tiebreaker cases where the stored timestamps
    /// are equal to the max precision of mysql TIMESTAMP).
    /// </remarks>
    public class BatchInserter
    {
        public static int CurrentReportInsertCount;
        public static int CurrentReportDeleteCount;
        public static int TotalInsertCount;
        public static int TotalDeleteCount;

        public static int TotalSkipCount;

        private readonly Ruleset ruleset;
        private readonly HitResult maxBasicResult;
        private readonly ModClassic classicMod;

        private readonly bool importLegacyPP;
        private readonly bool dryRun;

        public HighScore[] Scores { get; }

        public Task Task { get; }

        public List<ElasticQueuePusher.ElasticScoreItem> ElasticScoreItems { get; } = new List<ElasticQueuePusher.ElasticScoreItem>();

        public List<ScoreItem> ScoreStatisticsItems { get; } = new List<ScoreItem>();

        public BatchInserter(Ruleset ruleset, HighScore[] scores, bool importLegacyPP, bool dryRun = false)
        {
            this.ruleset = ruleset;
            this.importLegacyPP = importLegacyPP;
            this.dryRun = dryRun;

            using (var scoreProcessor = ruleset.CreateScoreProcessor())
                maxBasicResult = ruleset.GetHitResults().Where(h => h.result.IsBasic()).Select(h => h.result).MaxBy(scoreProcessor.GetBaseScoreForResult);
            classicMod = ruleset.CreateMod<ModClassic>()!;

            Scores = scores;
            Task = Task.Run(() => run(scores));
        }

        private async Task run(HighScore[] scores)
        {
            int insertCount = 0;
            bool first = true;

            int rulesetId = ruleset.RulesetInfo.OnlineID;

            StringBuilder insertBuilder = new StringBuilder("INSERT INTO scores (`user_id`, `ruleset_id`, `beatmap_id`, `has_replay`, `preserve`, `rank`, `passed`, `accuracy`, `max_combo`, `total_score`, `data`, `pp`, `legacy_score_id`, `legacy_total_score`, `ended_at`, `unix_updated_at`) VALUES ");

            Console.WriteLine($" Processing scores {scores.First().score_id} to {scores.Last().score_id}");
            Stopwatch sw = new Stopwatch();
            sw.Start();
            Parallel.ForEach(scores, new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
            }, highScore =>
            {
                try
                {
                    if (highScore.score_id == 0)
                    {
                        // Something really bad probably happened, abort for safety.
                        throw new InvalidOperationException("Score arrived with no ID");
                    }

                    // Yes this is a weird way of determining whether it's a deletion.
                    // Look away please.
                    bool isDeletion = highScore.user_id == 0 && highScore.score == 0;

                    if (isDeletion)
                    {
                        if (highScore.new_id == null)
                        {
                            Interlocked.Increment(ref TotalSkipCount);
                            return;
                        }

                        using (var conn = DatabaseAccess.GetConnection())
                            conn.Execute("DELETE FROM scores WHERE id = @id", new { highScore.new_id });
                        ElasticScoreItems.Add(new ElasticQueuePusher.ElasticScoreItem { ScoreId = (long)highScore.new_id });

                        Interlocked.Increment(ref TotalDeleteCount);
                        Interlocked.Increment(ref CurrentReportDeleteCount);
                        return;
                    }

                    if (highScore.new_id != null)
                    {
                        Interlocked.Increment(ref TotalSkipCount);
                        return;
                    }

                    // At least one row in the old table have invalid dates.
                    // MySQL doesn't like empty dates, so let's ensure we have a valid one.
                    if (highScore.date < DateTimeOffset.UnixEpoch)
                    {
                        Console.WriteLine($" Legacy score {highScore.score_id} has invalid date ({highScore.date}), fixing.");
                        highScore.date = DateTimeOffset.UnixEpoch;
                    }

                    ScoreInfo referenceScore = CreateReferenceScore(highScore);
                    string serialisedScore = SerialiseScoreData(referenceScore);

                    Interlocked.Increment(ref insertCount);

                    lock (insertBuilder)
                    {
                        if (!first)
                            insertBuilder.Append(",");
                        first = false;

                        if (referenceScore.TotalScore > 4294967295)
                            referenceScore.TotalScore = 0;

                        if (referenceScore.LegacyTotalScore > 4294967295)
                            referenceScore.LegacyTotalScore = 0;

                        insertBuilder.Append($"({highScore.user_id}, {rulesetId}, {highScore.beatmap_id}, {(highScore.replay ? "1" : "0")}, 1, '{referenceScore.Rank.ToString()}', 1, {referenceScore.Accuracy}, {referenceScore.MaxCombo}, {referenceScore.TotalScore}, '{serialisedScore}', {highScore.pp?.ToString() ?? "null"}, {highScore.score_id}, {referenceScore.LegacyTotalScore}, '{highScore.date.ToString("yyyy-MM-dd HH:mm:ss")}', {highScore.date.ToUnixTimeSeconds()})");
                    }
                }
                catch (Exception e)
                {
                    throw new AggregateException($"Processing legacy score {highScore.score_id} failed.", e);
                }
            });

            Console.WriteLine($" Processing completed in {sw.Elapsed.TotalSeconds:N1} seconds");

            if (insertCount == 0)
            {
                Console.WriteLine($" Skipped all {scores.Length} scores");

                return;
            }

            insertBuilder.Append("; SELECT LAST_INSERT_ID()");

            string sql = insertBuilder.ToString();

            Console.WriteLine($" Running insert command with {sql.Length:#,0} bytes");
            sw.Restart();

            using (var db = DatabaseAccess.GetConnection())
            {
                // https://dev.mysql.com/doc/refman/8.0/en/information-functions.html#function_last-insert-id
                // If you insert multiple rows using a single INSERT statement, LAST_INSERT_ID() returns the value generated for the first inserted row only.
                ulong firstInsertId = db.ExecuteScalar<ulong>(sql, commandTimeout: 120);
                ulong lastInsertId = firstInsertId + (ulong)scores.Length - 1;
                Console.WriteLine($" Command completed in {sw.Elapsed.TotalSeconds:N1} seconds");

                await enqueueForFurtherProcessing(firstInsertId, lastInsertId, db);
            }

            Interlocked.Add(ref CurrentReportInsertCount, scores.Length);
            Interlocked.Add(ref TotalInsertCount, scores.Length);
        }

        public static string SerialiseScoreData(ScoreInfo referenceScore) =>
            JsonConvert.SerializeObject(new SoloScoreData
            {
                Mods = referenceScore.Mods.Select(m => new APIMod(m)).ToArray(),
                Statistics = referenceScore.Statistics,
                MaximumStatistics = referenceScore.MaximumStatistics,
            }, new JsonSerializerSettings
            {
                DefaultValueHandling = DefaultValueHandling.Ignore
            });

        /// <summary>
        /// Creates a partially-populated "reference" score that provides:
        /// <list type="bullet">
        /// <item><term><see cref="ScoreInfo.Ruleset"/></term></item>
        /// <item><term><see cref="ScoreInfo.Accuracy"/></term></item>
        /// <item><term><see cref="ScoreInfo.Mods"/></term></item>
        /// <item><term><see cref="ScoreInfo.Statistics"/></term></item>
        /// <item><term><see cref="ScoreInfo.MaximumStatistics"/></term></item>
        /// </list>
        /// </summary>
        public ScoreInfo CreateReferenceScore(HighScore highScore)
        {
            int rulesetId = ruleset.RulesetInfo.OnlineID;

            var scoreInfo = new ScoreInfo
            {
                Ruleset = ruleset.RulesetInfo,
                Mods = ruleset.ConvertFromLegacyMods((LegacyMods)highScore.enabled_mods).Append(classicMod).ToArray(),
                Statistics = new Dictionary<HitResult, int>(),
                MaximumStatistics = new Dictionary<HitResult, int>(),
                MaxCombo = highScore.maxcombo,
                LegacyTotalScore = highScore.score,
                IsLegacyScore = true
            };

            // Populate statistics and accuracy.
            scoreInfo.SetCount50(highScore.count50);
            scoreInfo.SetCount100(highScore.count100);
            scoreInfo.SetCount300(highScore.count300);
            scoreInfo.SetCountMiss(highScore.countmiss);
            scoreInfo.SetCountGeki(highScore.countgeki);
            scoreInfo.SetCountKatu(highScore.countkatu);
            LegacyScoreDecoder.PopulateAccuracy(scoreInfo);

            // Trim zero values from statistics.
            scoreInfo.Statistics = scoreInfo.Statistics.Where(kvp => kvp.Value != 0).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            // Populate the maximum statistics.
            foreach ((HitResult result, int count) in scoreInfo.Statistics)
            {
                switch (result)
                {
                    case HitResult.LargeTickHit:
                    case HitResult.LargeTickMiss:
                        scoreInfo.MaximumStatistics[HitResult.LargeTickHit] = scoreInfo.MaximumStatistics.GetValueOrDefault(HitResult.LargeTickHit) + count;
                        break;

                    case HitResult.SmallTickHit:
                    case HitResult.SmallTickMiss:
                        scoreInfo.MaximumStatistics[HitResult.SmallTickHit] = scoreInfo.MaximumStatistics.GetValueOrDefault(HitResult.SmallTickHit) + count;
                        break;

                    case HitResult.IgnoreHit:
                    case HitResult.IgnoreMiss:
                    case HitResult.SmallBonus:
                    case HitResult.LargeBonus:
                        break;

                    default:
                        scoreInfo.MaximumStatistics[maxBasicResult] = scoreInfo.MaximumStatistics.GetValueOrDefault(maxBasicResult) + count;
                        break;
                }
            }

            // In osu! and osu!mania, some judgements affect combo but aren't stored to scores.
            // A special hit result is used to pad out the combo value to match, based on the max combo from the beatmap attributes.
            int maxComboFromStatistics = scoreInfo.MaximumStatistics.Where(kvp => kvp.Key.AffectsCombo()).Select(kvp => kvp.Value).DefaultIfEmpty(0).Sum();

            var scoreAttributes = getScoringAttributes(rulesetId, highScore.beatmap_id);

            if (scoreAttributes == null)
            {
                // TODO: LOG
                Console.Error.WriteLine($"{highScore.score_id}: Scoring attribs entry missing for beatmap {highScore.beatmap_id}.");
                return scoreInfo;
            }

#pragma warning disable CS0618
            // Pad the maximum combo.
            // Special case here for osu!mania as it requires per-mod considerations (key mods).

            if (rulesetId == 3)
            {
                // Using the BeatmapStore class will fail if a particular difficulty attribute value doesn't exist in the database as a result of difficulty calculation not having been run yet.
                // Additionally, to properly fill out attribute objects, the BeatmapStore class would require a beatmap object resulting in another database query.
                // To get around both of these issues, we'll directly look up the attribute ourselves.

                // The isConvertedBeatmap parameter only affects whether mania key mods are allowed.
                // Since we're dealing with high scores, we assume that the database mod values have already been validated for mania-specific beatmaps that don't allow key mods.
                int difficultyMods = (int)LegacyModsHelper.MaskRelevantMods((LegacyMods)highScore.enabled_mods, true, rulesetId);

                Dictionary<int, BeatmapDifficultyAttribute> dbAttributes = queryAttributes(
                    new DifficultyAttributesLookup(highScore.beatmap_id, rulesetId, difficultyMods));

                if (!dbAttributes.TryGetValue(9, out BeatmapDifficultyAttribute? maxComboAttribute))
                {
                    // TODO: LOG
                    Console.Error.WriteLine($" {highScore.score_id}: Could not determine max combo from the difficulty attributes of beatmap {highScore.beatmap_id}.");
                    return scoreInfo;
                }

                if ((int)maxComboAttribute.value > maxComboFromStatistics)
                    scoreInfo.MaximumStatistics[HitResult.LegacyComboIncrease] = (int)maxComboAttribute.value - maxComboFromStatistics;
            }
            else
            {
                if (scoreAttributes.max_combo > maxComboFromStatistics)
                    scoreInfo.MaximumStatistics[HitResult.LegacyComboIncrease] = scoreAttributes.max_combo - maxComboFromStatistics;
            }

#pragma warning restore CS0618

            var difficulty = getDificultyInfo(highScore.beatmap_id);

            StandardisedScoreMigrationTools.UpdateFromLegacy(scoreInfo, difficulty, scoreAttributes.ToAttributes());

            return scoreInfo;
        }

        private static readonly ConcurrentDictionary<int, BeatmapScoringAttributes?> scoring_attributes_cache =
            new ConcurrentDictionary<int, BeatmapScoringAttributes?>();

        private static BeatmapScoringAttributes? getScoringAttributes(int rulesetId, int beatmapId)
        {
            if (scoring_attributes_cache.TryGetValue(beatmapId, out var existing))
                return existing;

            using (var connection = DatabaseAccess.GetConnection())
            {
                BeatmapScoringAttributes? scoreAttributes = connection.QuerySingleOrDefault<BeatmapScoringAttributes>(
                    "SELECT * FROM osu_beatmap_scoring_attribs WHERE beatmap_id = @BeatmapId AND mode = @RulesetId", new
                    {
                        BeatmapId = beatmapId,
                        RulesetId = rulesetId,
                    });

                return scoring_attributes_cache[beatmapId] = scoreAttributes;
            }
        }

        private static readonly ConcurrentDictionary<int, LegacyBeatmapConversionDifficultyInfo> difficulty_info_cache =
            new ConcurrentDictionary<int, LegacyBeatmapConversionDifficultyInfo>();

        private static LegacyBeatmapConversionDifficultyInfo getDificultyInfo(int beatmapId)
        {
            if (difficulty_info_cache.TryGetValue(beatmapId, out var existing))
                return existing;

            using (var connection = DatabaseAccess.GetConnection())
            {
                Beatmap beatmap = connection.QuerySingle<Beatmap>("SELECT * FROM osu_beatmaps WHERE `beatmap_id` = @BeatmapId", new
                {
                    BeatmapId = beatmapId
                });

                return difficulty_info_cache[beatmapId] = LegacyBeatmapConversionDifficultyInfo.FromAPIBeatmap(beatmap.ToAPIBeatmap());
            }
        }

        private static readonly ConcurrentDictionary<DifficultyAttributesLookup, Dictionary<int, BeatmapDifficultyAttribute>> attributes_cache =
            new ConcurrentDictionary<DifficultyAttributesLookup, Dictionary<int, BeatmapDifficultyAttribute>>();

        private static Dictionary<int, BeatmapDifficultyAttribute> queryAttributes(DifficultyAttributesLookup lookup)
        {
            if (attributes_cache.TryGetValue(lookup, out Dictionary<int, BeatmapDifficultyAttribute>? existing))
                return existing;

            using (var connection = DatabaseAccess.GetConnection())
            {
                IEnumerable<BeatmapDifficultyAttribute> dbAttributes =
                    connection.Query<BeatmapDifficultyAttribute>(
                        "SELECT * FROM osu_beatmap_difficulty_attribs WHERE `beatmap_id` = @BeatmapId AND `mode` = @RulesetId AND `mods` = @Mods", lookup);

                return attributes_cache[lookup] = dbAttributes.ToDictionary(a => (int)a.attrib_id, a => a);
            }
        }

        private record DifficultyAttributesLookup(int BeatmapId, int RulesetId, int Mods)
        {
            public override string ToString()
            {
                return $"{{ BeatmapId = {BeatmapId}, RulesetId = {RulesetId}, Mods = {Mods} }}";
            }
        }

        private async Task enqueueForFurtherProcessing(ulong firstId, ulong lastId, MySqlConnection connection)
        {
            for (ulong scoreId = firstId; scoreId <= lastId; scoreId++)
            {
                if (importLegacyPP)
                {
                    // we can proceed by pushing the score directly to ES for indexing.
                    ElasticScoreItems.Add(new ElasticQueuePusher.ElasticScoreItem
                    {
                        ScoreId = (long)scoreId
                    });
                }
                else
                {
                    if (dryRun)
                    {
                        // We can't retrieve this from the database because it hasn't been inserted.
                        ScoreStatisticsItems.Add(new ScoreItem(new SoloScore(), new ProcessHistory()));
                        return;
                    }

                    // the legacy PP value was not imported.
                    // push the score to redis for PP processing.
                    // on completion of PP processing, the score will be pushed to ES for indexing.
                    // the score refetch here is wasteful, but convenient and reliable, as the actual updated/inserted `SoloScore` row
                    // is not constructed anywhere before this...
                    var score = await connection.QuerySingleAsync<SoloScore>("SELECT * FROM `scores` WHERE `id` = @id",
                        new { id = scoreId });
                    var history = await connection.QuerySingleOrDefaultAsync<ProcessHistory>("SELECT * FROM `score_process_history` WHERE `score_id` = @id",
                        new { id = scoreId });
                    ScoreStatisticsItems.Add(new ScoreItem(score, history));
                }
            }
        }
    }
}
