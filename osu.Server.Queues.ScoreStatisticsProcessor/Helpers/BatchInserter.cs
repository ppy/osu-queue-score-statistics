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
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using BeatmapStore = osu.Server.Queues.ScoreStatisticsProcessor.Stores.BeatmapStore;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
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

        private readonly bool dryRun;
        private readonly bool throwOnFailure;

        public HighScore[] Scores { get; }

        public Task Task { get; }

        public List<ElasticQueuePusher.ElasticScoreItem> ElasticScoreItems { get; } = new List<ElasticQueuePusher.ElasticScoreItem>();

        public List<ScoreItem> ScoreStatisticsItems { get; } = new List<ScoreItem>();

        static BatchInserter()
        {
            _ = BeatmapStatusWatcher.StartPollingAsync(updates =>
            {
                foreach (int beatmapSetId in updates.BeatmapSetIDs)
                {
                    using (var db = DatabaseAccess.GetConnection())
                    {
                        ICollection<BeatmapLookup> scoringAttributesKeys = scoring_attributes_cache.Keys;
                        ICollection<DifficultyAttributesLookup> difficultyAttributesKeys = attributes_cache.Keys;

                        foreach (int beatmapId in db.Query<int>("SELECT beatmap_id FROM osu_beatmaps WHERE beatmapset_id = @beatmapSetId AND `deleted_at` IS NULL",
                                     new { beatmapSetId }))
                        {
                            Console.WriteLine($"Invalidating cache for beatmap_id {beatmapId}");
                            difficulty_info_cache.TryRemove(beatmapId, out _);

                            foreach (var key in scoringAttributesKeys)
                            {
                                if (key.BeatmapId == beatmapId)
                                    scoring_attributes_cache.TryRemove(key, out _);
                            }

                            foreach (var key in difficultyAttributesKeys)
                            {
                                if (key.BeatmapId == beatmapId)
                                    attributes_cache.TryRemove(key, out _);
                            }
                        }
                    }
                }
            });
        }

        public BatchInserter(Ruleset ruleset, HighScore[] scores, bool dryRun = false, bool throwOnFailure = true)
        {
            this.ruleset = ruleset;
            this.dryRun = dryRun;
            this.throwOnFailure = throwOnFailure;

            Scores = scores;
            Task = Task.Run(() => run(scores));
        }

        private async Task run(HighScore[] scores)
        {
            int insertCount = 0;

            int rulesetId = ruleset.RulesetInfo.OnlineID;

            Console.WriteLine($" Processing scores {scores.Min(s => s.score_id)} to {scores.Max(s => s.score_id)}");
            Stopwatch sw = new Stopwatch();
            sw.Start();

            Parallel.ForEach(scores, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, highScore =>
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
                        {
                            conn.Execute("DELETE FROM score_pins WHERE user_id = @new_user_id AND score_id = @new_id;"
                                         + "DELETE FROM scores WHERE id = @new_id", new
                            {
                                highScore.new_id,
                                highScore.new_user_id,
                            });
                        }

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

                    ScoreInfo referenceScore = CreateReferenceScore(ruleset.RulesetInfo.OnlineID, highScore);
                    string serialisedScore = SerialiseScoreData(referenceScore);

                    Interlocked.Increment(ref insertCount);

                    if (referenceScore.TotalScore > 4294967295)
                        referenceScore.TotalScore = 0;

                    if (referenceScore.LegacyTotalScore > 4294967295)
                        referenceScore.LegacyTotalScore = 0;

                    // All preserved scores should be passes.
                    Debug.Assert(!highScore.ShouldPreserve || highScore.pass);

                    // All scores with replays should be preserved.
                    Debug.Assert(!highScore.ShouldPreserve || highScore.replay);

                    // For non-preserved flags, we zero the score_id.
                    // This is because they come from a different table with a different range and it would be hard to track.
                    if (!highScore.ShouldPreserve)
                        highScore.score_id = 0;

                    // For now, mark all non-preserved scores as not ranked.
                    // In theory, this should be the only case of non-ranked scores when importing from old tables.
                    bool isRanked = highScore.ShouldPreserve;

                    highScore.InsertSql =
                        $"({highScore.user_id}, {rulesetId}, {highScore.beatmap_id}, {(highScore.replay ? "1" : "0")}, {(highScore.ShouldPreserve ? "1" : "0")}, {(isRanked ? "1" : "0")}, '{referenceScore.Rank.ToString()}', {(highScore.pass ? "1" : "0")}, {referenceScore.Accuracy}, {referenceScore.MaxCombo}, {referenceScore.TotalScore}, '{serialisedScore}', {highScore.pp?.ToString() ?? "null"}, {highScore.score_id}, {referenceScore.LegacyTotalScore}, '{highScore.date:yyyy-MM-dd HH:mm:ss}', {highScore.date.ToUnixTimeSeconds()})";
                }
                catch (Exception e)
                {
                    if (throwOnFailure)
                        throw new AggregateException($"Processing legacy score {highScore.score_id} failed.", e);

                    Console.WriteLine($"Processing legacy score {highScore.score_id} failed.");
                }
            });

            Console.WriteLine($" Processing completed in {sw.Elapsed.TotalSeconds:N1} seconds");

            if (insertCount == 0)
            {
                Console.WriteLine($" Skipped all {scores.Length} scores");
                return;
            }

            bool first = true;
            StringBuilder insertBuilder = new StringBuilder(
                "INSERT INTO scores (`user_id`, `ruleset_id`, `beatmap_id`, `has_replay`, `preserve`, `ranked`, `rank`, `passed`, `accuracy`, `max_combo`, `total_score`, `data`, `pp`, `legacy_score_id`, `legacy_total_score`, `ended_at`, `unix_updated_at`) VALUES ");

            scores = scores.Where(score => !string.IsNullOrEmpty(score.InsertSql)).ToArray();

            foreach (var score in scores)
            {
                if (!first)
                    insertBuilder.Append(",");
                first = false;

                insertBuilder.Append(score.InsertSql);
            }

            insertBuilder.Append("; SELECT LAST_INSERT_ID()");

            string sql = insertBuilder.ToString();

            if (dryRun)
            {
                Console.WriteLine($" DRY RUN would insert command with {sql.Length:#,0} bytes");
                return;
            }

            Console.WriteLine($" Running insert command with {sql.Length:#,0} bytes");
            sw.Restart();

            using (var db = await DatabaseAccess.GetConnectionAsync())
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
                TotalScoreWithoutMods = referenceScore.TotalScoreWithoutMods,
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
        public static ScoreInfo CreateReferenceScore(int rulesetId, HighScore highScore)
        {
            var rulesetCache = getRulesetCache(rulesetId);

            var scoreInfo = new ScoreInfo
            {
                Ruleset = rulesetCache.Ruleset.RulesetInfo,
                Mods = rulesetCache.Ruleset.ConvertFromLegacyMods((LegacyMods)highScore.enabled_mods).Append(rulesetCache.ClassicMod).ToArray(),
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
                        scoreInfo.MaximumStatistics[rulesetCache.MaxBasicResult] = scoreInfo.MaximumStatistics.GetValueOrDefault(rulesetCache.MaxBasicResult) + count;
                        break;
                }
            }

            // In osu! and osu!mania, some judgements affect combo but aren't stored to scores.
            // A special hit result is used to pad out the combo value to match, based on the max combo from the beatmap attributes.
            int maxComboFromStatistics = scoreInfo.MaximumStatistics.Where(kvp => kvp.Key.AffectsCombo()).Select(kvp => kvp.Value).DefaultIfEmpty(0).Sum();

            var scoreAttributes = getScoringAttributes(new BeatmapLookup(highScore.beatmap_id, rulesetId));

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

                Dictionary<int, BeatmapDifficultyAttribute> dbAttributes = getAttributes(
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

            var difficulty = getDifficultyInfo(highScore.beatmap_id);

            StandardisedScoreMigrationTools.UpdateFromLegacy(scoreInfo, rulesetCache.Ruleset, difficulty, scoreAttributes.ToAttributes());

            return scoreInfo;
        }

        private static readonly ConcurrentDictionary<BeatmapLookup, BeatmapScoringAttributes?> scoring_attributes_cache =
            new ConcurrentDictionary<BeatmapLookup, BeatmapScoringAttributes?>();

        private static BeatmapScoringAttributes? getScoringAttributes(BeatmapLookup lookup)
        {
            if (scoring_attributes_cache.TryGetValue(lookup, out var existing))
                return existing;

            using (var connection = DatabaseAccess.GetConnection())
            {
                BeatmapScoringAttributes? scoreAttributes = connection.QuerySingleOrDefault<BeatmapScoringAttributes>(
                    "SELECT * FROM osu_beatmap_scoring_attribs WHERE beatmap_id = @BeatmapId AND mode = @RulesetId", new
                    {
                        BeatmapId = lookup.BeatmapId,
                        RulesetId = lookup.RulesetId,
                    });

                return scoring_attributes_cache[lookup] = scoreAttributes;
            }
        }

        private static readonly ConcurrentDictionary<int, LegacyBeatmapConversionDifficultyInfo> difficulty_info_cache =
            new ConcurrentDictionary<int, LegacyBeatmapConversionDifficultyInfo>();

        private static LegacyBeatmapConversionDifficultyInfo getDifficultyInfo(int beatmapId)
        {
            if (difficulty_info_cache.TryGetValue(beatmapId, out var existing))
                return existing;

            try
            {
                using (var connection = DatabaseAccess.GetConnection())
                {
                    Beatmap beatmap = connection.QuerySingle<Beatmap>("SELECT * FROM osu_beatmaps WHERE `beatmap_id` = @BeatmapId", new
                    {
                        BeatmapId = beatmapId
                    });

                    return difficulty_info_cache[beatmapId] = beatmap.GetLegacyBeatmapConversionDifficultyInfo();
                }
            }
            catch (Exception e)
            {
                throw new AggregateException($"Beatmap {beatmapId} missing from database", e);
            }
        }

        private static readonly ConcurrentDictionary<DifficultyAttributesLookup, Dictionary<int, BeatmapDifficultyAttribute>> attributes_cache =
            new ConcurrentDictionary<DifficultyAttributesLookup, Dictionary<int, BeatmapDifficultyAttribute>>();

        private static Dictionary<int, BeatmapDifficultyAttribute> getAttributes(DifficultyAttributesLookup lookup)
        {
            if (attributes_cache.TryGetValue(lookup, out Dictionary<int, BeatmapDifficultyAttribute>? existing))
                return existing;

            using (var connection = DatabaseAccess.GetConnection())
            {
                IEnumerable<BeatmapDifficultyAttribute> dbAttributes =
                    connection.Query<BeatmapDifficultyAttribute>(
                        $"SELECT * FROM {BeatmapStore.DIFF_ATTRIB_DATABASE}.osu_beatmap_difficulty_attribs WHERE `beatmap_id` = @BeatmapId AND `mode` = @RulesetId AND `mods` = @Mods", lookup);

                return attributes_cache[lookup] = dbAttributes.ToDictionary(a => (int)a.attrib_id, a => a);
            }
        }

        private static readonly ConcurrentDictionary<int, RulesetCache> ruleset_cache = new ConcurrentDictionary<int, RulesetCache>();

        private static RulesetCache getRulesetCache(int rulesetId)
        {
            if (ruleset_cache.TryGetValue(rulesetId, out var cache))
                return cache;

            var ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(rulesetId);

            HitResult maxBasicResult;
            using (var scoreProcessor = ruleset.CreateScoreProcessor())
                maxBasicResult = ruleset.GetHitResultsForDisplay().Where(h => h.result.IsBasic()).Select(h => h.result).MaxBy(scoreProcessor.GetBaseScoreForResult);

            cache = new RulesetCache(ruleset, maxBasicResult, ruleset.CreateMod<ModClassic>()!);

            ruleset_cache[rulesetId] = cache;

            return cache;
        }

        private record RulesetCache(Ruleset Ruleset, HitResult MaxBasicResult, Mod ClassicMod);

        private record BeatmapLookup(int BeatmapId, int RulesetId)
        {
            public override string ToString()
            {
                return $"{{ BeatmapId = {BeatmapId}, RulesetId = {RulesetId} }}";
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
                // the legacy PP value was not imported.
                // push the score to redis for PP processing.
                // on completion of PP processing, the score will be pushed to ES for indexing.
                // the score refetch here is wasteful, but convenient and reliable, as the actual updated/inserted `SoloScore` row
                // is not constructed anywhere before this...
                var score = await connection.QuerySingleOrDefaultAsync<SoloScore>("SELECT * FROM `scores` WHERE `id` = @id AND legacy_score_id IS NOT NULL",
                    new { id = scoreId });

                if (score == null)
                {
                    // likely a deletion; already queued for ES above.
                    continue;
                }

                var history = await connection.QuerySingleOrDefaultAsync<ProcessHistory>("SELECT * FROM `score_process_history` WHERE `score_id` = @id", new { id = scoreId });

                ScoreStatisticsItems.Add(new ScoreItem(score, history));
            }
        }
    }
}
