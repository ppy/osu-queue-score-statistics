// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using Newtonsoft.Json;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Database;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
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
        public static int CurrentReportUpdateCount;
        public static int CurrentReportDeleteCount;
        public static int TotalInsertCount;
        public static int TotalUpdateCount;
        public static int TotalDeleteCount;

        public static int TotalSkipCount;

        private readonly Ruleset ruleset;
        private readonly bool importLegacyPP;
        private readonly bool skipExisting;
        private readonly bool skipNew;
        private readonly bool dryRun;

        public HighScore[] Scores { get; }

        public Task Task { get; }

        public List<ElasticQueuePusher.ElasticScoreItem> ElasticScoreItems { get; } = new List<ElasticQueuePusher.ElasticScoreItem>();

        public List<ScoreItem> ScoreStatisticsItems { get; } = new List<ScoreItem>();

        public BatchInserter(Ruleset ruleset, HighScore[] scores, bool importLegacyPP, bool skipExisting, bool skipNew, bool dryRun = false)
        {
            this.ruleset = ruleset;
            this.importLegacyPP = importLegacyPP;
            this.skipExisting = skipExisting;
            this.skipNew = skipNew;
            this.dryRun = dryRun;

            Scores = scores;
            Task = run(scores);
        }

        private async Task run(HighScore[] scores)
        {
            using (var db = DatabaseAccess.GetConnection())
            using (var transaction = await db.BeginTransactionAsync())
            using (var insertCommand = db.CreateCommand())
            using (var updateCommand = db.CreateCommand())
            using (var deleteCommand = db.CreateCommand())
            {
                // check for existing and skip
                SoloScoreLegacyIDMap[] existingIds = (await db.QueryAsync<SoloScoreLegacyIDMap>(
                    $"SELECT * FROM score_legacy_id_map WHERE `ruleset_id` = {ruleset.RulesetInfo.OnlineID} AND `old_score_id` IN @oldScoreIds",
                    new
                    {
                        oldScoreIds = scores.Select(s => s.score_id)
                    }, transaction)).ToArray();

                insertCommand.CommandText =
                    // main score insert
                    "INSERT INTO scores (user_id, beatmap_id, ruleset_id, data, has_replay, preserve, created_at, unix_updated_at) "
                    + $"VALUES (@userId, @beatmapId, {ruleset.RulesetInfo.OnlineID}, @data, @has_replay, 1, @date, UNIX_TIMESTAMP(@date));";

                // pp insert
                if (importLegacyPP)
                    insertCommand.CommandText += "INSERT INTO score_performance (score_id, pp) VALUES (LAST_INSERT_ID(), @pp);";

                // mapping insert
                insertCommand.CommandText += $"INSERT INTO score_legacy_id_map (ruleset_id, old_score_id, score_id) VALUES ({ruleset.RulesetInfo.OnlineID}, @oldScoreId, LAST_INSERT_ID());";

                updateCommand.CommandText =
                    "UPDATE scores SET data = @data WHERE id = @id";

                deleteCommand.CommandText =
                    "DELETE FROM scores WHERE id = @newId; DELETE FROM score_performance WHERE score_id = @newId; DELETE FROM solo_scores_legacy_id_map WHERE old_score_id = @oldId";

                var userId = insertCommand.Parameters.Add("userId", MySqlDbType.UInt32);
                var oldScoreId = insertCommand.Parameters.Add("oldScoreId", MySqlDbType.UInt64);
                var beatmapId = insertCommand.Parameters.Add("beatmapId", MySqlDbType.UInt24);
                var data = insertCommand.Parameters.Add("data", MySqlDbType.JSON);
                var date = insertCommand.Parameters.Add("date", MySqlDbType.DateTime);
                var hasReplay = insertCommand.Parameters.Add("has_replay", MySqlDbType.Bool);
                var pp = insertCommand.Parameters.Add("pp", MySqlDbType.Float);

                var updateData = updateCommand.Parameters.Add("data", MySqlDbType.JSON);
                var updateId = updateCommand.Parameters.Add("id", MySqlDbType.UInt64);

                var deleteNewId = deleteCommand.Parameters.Add("newId", MySqlDbType.UInt64);
                var deleteOldId = deleteCommand.Parameters.Add("oldId", MySqlDbType.UInt64);

                foreach (var highScore in scores)
                {
                    try
                    {
                        if (highScore.score_id == 0)
                        {
                            // Something really bad probably happened, abort for safety.
                            throw new InvalidOperationException("Score arrived with no ID");
                        }

                        SoloScoreLegacyIDMap? existingMapping = existingIds.FirstOrDefault(e => e.old_score_id == highScore.score_id);

                        // Yes this is a weird way of determining whether it's a deletion.
                        // Look away please.
                        bool isDeletion = highScore.user_id == 0 && highScore.score == 0;

                        if (isDeletion)
                        {
                            // Deletion for a row which wasn't inserted into the new table, can safely ignore.
                            if (existingMapping == null)
                                continue;

                            deleteCommand.Transaction = transaction;

                            deleteOldId.Value = existingMapping.old_score_id;
                            deleteNewId.Value = existingMapping.score_id;

                            if (!deleteCommand.IsPrepared)
                                await deleteCommand.PrepareAsync();

                            await runCommand(deleteCommand);
                            await enqueueForFurtherProcessing(existingMapping.score_id, db, transaction, true);

                            Interlocked.Increment(ref CurrentReportDeleteCount);
                            Interlocked.Increment(ref TotalDeleteCount);
                            continue;
                        }

                        if ((existingMapping != null && skipExisting) || (existingMapping == null && skipNew))
                        {
                            Interlocked.Increment(ref TotalSkipCount);
                            continue;
                        }

                        // At least one row in the old table have invalid dates.
                        // MySQL doesn't like empty dates, so let's ensure we have a valid one.
                        if (highScore.date < DateTimeOffset.UnixEpoch)
                        {
                            Console.WriteLine($"Legacy score {highScore.score_id} has invalid date ({highScore.date}), fixing.");
                            highScore.date = DateTimeOffset.UnixEpoch;
                        }

                        ScoreInfo referenceScore = await CreateReferenceScore(ruleset, highScore, db, transaction);
                        var serialisedScore = SerialiseScore(ruleset, highScore, referenceScore);

                        if (existingMapping != null)
                        {
                            // Note that this only updates the `data` field. We could add others in the future as required.
                            updateCommand.Transaction = transaction;

                            updateId.Value = existingMapping.score_id;
                            updateData.Value = serialisedScore;

                            if (!updateCommand.IsPrepared)
                                await updateCommand.PrepareAsync();

                            // This could potentially be batched further (ie. to run more SQL statements in a single NonQuery call), but in practice
                            // this does not improve throughput.
                            await runCommand(updateCommand);
                            await enqueueForFurtherProcessing(existingMapping.score_id, db, transaction);

                            Interlocked.Increment(ref CurrentReportUpdateCount);
                            Interlocked.Increment(ref TotalUpdateCount);
                        }
                        else
                        {
                            pp.Value = highScore.pp;
                            userId.Value = highScore.user_id;
                            oldScoreId.Value = highScore.score_id;
                            beatmapId.Value = highScore.beatmap_id;
                            date.Value = highScore.date;
                            hasReplay.Value = highScore.replay;
                            data.Value = serialisedScore;

                            if (!insertCommand.IsPrepared)
                                await insertCommand.PrepareAsync();
                            insertCommand.Transaction = transaction;

                            // This could potentially be batched further (ie. to run more SQL statements in a single NonQuery call), but in practice
                            // this does not improve throughput.
                            await runCommand(insertCommand);
                            await enqueueForFurtherProcessing((ulong)insertCommand.LastInsertedId, db, transaction);

                            Interlocked.Increment(ref CurrentReportInsertCount);
                            Interlocked.Increment(ref TotalInsertCount);
                        }
                    }
                    catch (Exception e)
                    {
                        throw new AggregateException($"Processing legacy score {highScore.score_id} failed.", e);
                    }
                }

                await transaction.CommitAsync();
            }
        }

        public static string SerialiseScore(Ruleset ruleset, HighScore highScore, ScoreInfo referenceScore)
        {
            var serialisedScore = JsonConvert.SerializeObject(new SoloScoreInfo
            {
                // id will be written below in the UPDATE call.
                UserID = highScore.user_id,
                BeatmapID = highScore.beatmap_id,
                RulesetID = ruleset.RulesetInfo.OnlineID,
                Passed = true,
                TotalScore = (int)referenceScore.TotalScore,
                Accuracy = referenceScore.Accuracy,
                MaxCombo = highScore.maxcombo,
                Rank = Enum.TryParse(highScore.rank, out ScoreRank parsed) ? parsed : ScoreRank.D,
                Mods = referenceScore.Mods.Select(m => new APIMod(m)).ToArray(),
                Statistics = referenceScore.Statistics,
                MaximumStatistics = referenceScore.MaximumStatistics,
                EndedAt = highScore.date,
                LegacyTotalScore = highScore.score,
                LegacyScoreId = highScore.score_id
            }, new JsonSerializerSettings
            {
                DefaultValueHandling = DefaultValueHandling.Ignore
            });
            return serialisedScore;
        }

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
        public static async Task<ScoreInfo> CreateReferenceScore(Ruleset ruleset, HighScore highScore, MySqlConnection connection, MySqlTransaction? transaction)
        {
            Mod? classicMod = ruleset.CreateMod<ModClassic>();
            Debug.Assert(classicMod != null);

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

            var scoreProcessor = ruleset.CreateScoreProcessor();

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
            HitResult maxBasicResult = ruleset.GetHitResults().Select(h => h.result).Where(h => h.IsBasic()).MaxBy(scoreProcessor.GetBaseScoreForResult);

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

            BeatmapScoringAttributes? scoreAttributes = await connection.QuerySingleOrDefaultAsync<BeatmapScoringAttributes>(
                "SELECT * FROM osu_beatmap_scoring_attribs WHERE beatmap_id = @BeatmapId AND mode = @RulesetId", new
                {
                    BeatmapId = highScore.beatmap_id,
                    RulesetId = ruleset.RulesetInfo.OnlineID
                }, transaction);

            if (scoreAttributes == null)
            {
                // TODO: LOG
                await Console.Error.WriteLineAsync($"{highScore.score_id}: Scoring attribs entry missing for beatmap {highScore.beatmap_id}.");
                return scoreInfo;
            }

#pragma warning disable CS0618
            // Pad the maximum combo.
            // Special case here for osu!mania as it requires per-mod considerations (key mods).
            if (ruleset.RulesetInfo.OnlineID == 3)
            {
                // Using the BeatmapStore class will fail if a particular difficulty attribute value doesn't exist in the database as a result of difficulty calculation not having been run yet.
                // Additionally, to properly fill out attribute objects, the BeatmapStore class would require a beatmap object resulting in another database query.
                // To get around both of these issues, we'll directly look up the attribute ourselves.

                // The isConvertedBeatmap parameter only affects whether mania key mods are allowed.
                // Since we're dealing with high scores, we assume that the database mod values have already been validated for mania-specific beatmaps that don't allow key mods.
                int difficultyMods = (int)LegacyModsHelper.MaskRelevantMods((LegacyMods)highScore.enabled_mods, true, ruleset.RulesetInfo.OnlineID);

                Dictionary<int, BeatmapDifficultyAttribute> dbAttributes = queryAttributes(
                    new DifficultyAttributesLookup(highScore.beatmap_id, ruleset.RulesetInfo.OnlineID, difficultyMods), connection, transaction);

                if (!dbAttributes.TryGetValue(9, out BeatmapDifficultyAttribute? maxComboAttribute))
                {
                    // TODO: LOG
                    await Console.Error.WriteLineAsync($"{highScore.score_id}: Could not determine max combo from the difficulty attributes of beatmap {highScore.beatmap_id}.");
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

            Beatmap beatmap = await connection.QuerySingleAsync<Beatmap>("SELECT * FROM osu_beatmaps WHERE `beatmap_id` = @BeatmapId", new
            {
                BeatmapId = highScore.beatmap_id
            }, transaction);

            LegacyBeatmapConversionDifficultyInfo difficulty = LegacyBeatmapConversionDifficultyInfo.FromAPIBeatmap(beatmap.ToAPIBeatmap());

            StandardisedScoreMigrationTools.UpdateFromLegacy(scoreInfo, difficulty, scoreAttributes.ToAttributes());

            return scoreInfo;
        }

        private static readonly ConcurrentDictionary<DifficultyAttributesLookup, Dictionary<int, BeatmapDifficultyAttribute>> attributes_cache =
            new ConcurrentDictionary<DifficultyAttributesLookup, Dictionary<int, BeatmapDifficultyAttribute>>();

        private static Dictionary<int, BeatmapDifficultyAttribute> queryAttributes(DifficultyAttributesLookup lookup, MySqlConnection connection, MySqlTransaction? transaction)
        {
            if (attributes_cache.TryGetValue(lookup, out Dictionary<int, BeatmapDifficultyAttribute>? existing))
                return existing;

            IEnumerable<BeatmapDifficultyAttribute> dbAttributes =
                connection.Query<BeatmapDifficultyAttribute>(
                    "SELECT * FROM osu_beatmap_difficulty_attribs WHERE `beatmap_id` = @BeatmapId AND `mode` = @RulesetId AND `mods` = @Mods", lookup, transaction);

            return attributes_cache[lookup] = dbAttributes.ToDictionary(a => (int)a.attrib_id, a => a);
        }

        private record DifficultyAttributesLookup(int BeatmapId, int RulesetId, int Mods)
        {
            public override string ToString()
            {
                return $"{{ BeatmapId = {BeatmapId}, RulesetId = {RulesetId}, Mods = {Mods} }}";
            }
        }

        private async Task runCommand(MySqlCommand command)
        {
            if (dryRun)
            {
                Console.WriteLine($"Running: {command.CommandText}");
                Console.WriteLine();

                string paramString = string.Join(", ", command.Parameters.Select(p => $"{p.ParameterName}:{p.Value}"));
                Console.WriteLine($"Params: {paramString}");
                return;
            }

            await command.ExecuteNonQueryAsync();
        }

        private async Task enqueueForFurtherProcessing(ulong scoreId, MySqlConnection connection, MySqlTransaction transaction, bool isDelete = false)
        {
            if (importLegacyPP || isDelete)
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
                    new { id = scoreId }, transaction);
                var history = await connection.QuerySingleOrDefaultAsync<ProcessHistory>("SELECT * FROM `score_process_history` WHERE `score_id` = @id",
                    new { id = scoreId }, transaction);
                ScoreStatisticsItems.Add(new ScoreItem(score, history));
            }
        }
    }
}
