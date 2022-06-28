// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScorePump.Performance
{
    public abstract class PerformanceCommand : ScorePump
    {
        private readonly ConcurrentDictionary<int, Beatmap?> beatmapCache = new ConcurrentDictionary<int, Beatmap?>();
        private readonly ConcurrentDictionary<DifficultyAttributeKey, BeatmapDifficultyAttribute[]?> attributeCache = new ConcurrentDictionary<DifficultyAttributeKey, BeatmapDifficultyAttribute[]?>();
        private readonly ConcurrentDictionary<int, bool> buildsCache = new ConcurrentDictionary<int, bool>();

        [Option(Description = "Number of threads to use.")]
        public int Threads { get; set; } = 1;

        /// <summary>
        /// Sets a count in the database.
        /// </summary>
        /// <param name="key">The count's key.</param>
        /// <param name="value">The count's value.</param>
        protected async Task SetCount(string key, long value)
        {
            using (var db = Queue.GetDatabaseConnection())
            {
                await db.ExecuteAsync("INSERT INTO `osu_counts` (`name`,`count`) VALUES (@NAME, @COUNT) "
                                      + "ON DUPLICATE KEY UPDATE `name` = VALUES(`name`), `count` = VALUES(`count`)", new
                {
                    Name = key,
                    Count = value
                });
            }
        }

        /// <summary>
        /// Retrieves a count value from the database.
        /// </summary>
        /// <param name="key">The count's key.</param>
        /// <returns>The count for the provided key.</returns>
        /// <exception cref="InvalidOperationException">If the key wasn't found in the database.</exception>
        protected async Task<long> GetCount(string key)
        {
            using (var db = Queue.GetDatabaseConnection())
            {
                long? res = await db.QuerySingleOrDefaultAsync<long?>("SELECT `count` FROM `osu_counts` WHERE `name` = @NAME", new
                {
                    Name = key
                });

                if (res == null)
                    throw new InvalidOperationException($"Unable to retrieve count '{key}'.");

                return res.Value;
            }
        }

        protected async Task ProcessUser(uint userId)
        {
            SoloScore[] scores;

            using (var db = Queue.GetDatabaseConnection())
            {
                scores = (await db.QueryAsync<SoloScore>($"SELECT * FROM {SoloScore.TABLE_NAME} WHERE `user_id` = @UserId", new
                {
                    UserId = userId
                })).ToArray();
            }

            foreach (SoloScore score in scores)
                await ProcessScore(score);
        }

        protected async Task ProcessScore(ulong scoreId)
        {
            SoloScore? score;

            using (var db = Queue.GetDatabaseConnection())
            {
                score = await db.QuerySingleOrDefaultAsync<SoloScore>($"SELECT * FROM {SoloScore.TABLE_NAME} WHERE `id` = @ScoreId", new
                {
                    ScoreId = scoreId
                });
            }

            if (score == null)
            {
                await Console.Error.WriteLineAsync($"Could not find score ID {scoreId}.");
                return;
            }

            await ProcessScore(score);
        }

        protected async Task ProcessScore(SoloScore score)
        {
            try
            {
                Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(score.ruleset_id);
                Mod[] mods = score.ScoreInfo.mods.Select(m => m.ToMod(ruleset)).ToArray();
                ScoreInfo scoreInfo = score.ScoreInfo.ToScoreInfo(mods);

                DifficultyAttributes difficultyAttributes = await GetDifficultyAttributes(score, ruleset, mods);

                PerformanceAttributes? performanceAttributes = ruleset.CreatePerformanceCalculator()?.Calculate(scoreInfo, difficultyAttributes);
                if (performanceAttributes == null)
                    return;

                using (var db = Queue.GetDatabaseConnection())
                {
                    await db.ExecuteAsync($"INSERT INTO {SoloScorePerformance.TABLE_NAME} (`score_id`, `pp`) VALUES (@ScoreId, @Pp) ON DUPLICATE KEY UPDATE `pp` = @Pp", new
                    {
                        ScoreId = score.id,
                        Pp = performanceAttributes.Total
                    });
                }
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"{score.id} failed with: {ex}");
            }
        }

        /// <summary>
        /// Retrieves difficulty attributes from the database.
        /// </summary>
        /// <param name="score">The score.</param>
        /// <param name="ruleset">The score's ruleset.</param>
        /// <param name="mods">The score's mods.</param>
        /// <returns>The difficulty attributes.</returns>
        /// <exception cref="InvalidOperationException">If the beatmap or attributes weren't found in the database.</exception>
        protected async Task<DifficultyAttributes> GetDifficultyAttributes(SoloScore score, Ruleset ruleset, Mod[] mods)
        {
            Beatmap? beatmap;
            BeatmapDifficultyAttribute[]? rawDifficultyAttributes;

            using (var db = Queue.GetDatabaseConnection())
            {
                if (!beatmapCache.TryGetValue(score.beatmap_id, out beatmap))
                {
                    beatmap = beatmapCache[score.beatmap_id] = await db.QuerySingleOrDefaultAsync<Beatmap?>("SELECT * FROM osu_beatmaps WHERE beatmap_id = @BeatmapId", new
                    {
                        BeatmapId = score.beatmap_id
                    });
                }

                if (beatmap == null)
                    throw new InvalidOperationException($"Beatmap not found in database: {score.beatmap_id}");

                // Todo: We shouldn't be using legacy mods, but this requires difficulty calculation to be performed in-line.
                LegacyMods legacyModValue = LegacyModsHelper.MaskRelevantMods(ruleset.ConvertToLegacyMods(mods), score.ruleset_id != beatmap.playmode, score.ruleset_id);
                DifficultyAttributeKey key = new DifficultyAttributeKey(score.beatmap_id, score.ruleset_id, (uint)legacyModValue);

                if (!attributeCache.TryGetValue(key, out rawDifficultyAttributes))
                {
                    rawDifficultyAttributes = attributeCache[key] = (await db.QueryAsync<BeatmapDifficultyAttribute>(
                        "SELECT * FROM osu_beatmap_difficulty_attribs WHERE beatmap_id = @BeatmapId AND mode = @RulesetId AND mods = @ModValue", new
                        {
                            BeatmapId = key.BeatmapId,
                            RulesetId = key.RulesetId,
                            ModValue = key.ModValue
                        })).ToArray();
                }

                if (rawDifficultyAttributes == null)
                    throw new InvalidOperationException($"Databased difficulty attributes were not found for: {key}");
            }

            DifficultyAttributes difficultyAttributes = LegacyRulesetHelper.CreateDifficultyAttributes(score.ruleset_id);
            difficultyAttributes.FromDatabaseAttributes(rawDifficultyAttributes.ToDictionary(a => (int)a.attrib_id, a => (double)a.value), new APIBeatmap
            {
                ApproachRate = beatmap.diff_approach,
                DrainRate = beatmap.diff_drain,
                OverallDifficulty = beatmap.diff_overall,
                CircleCount = beatmap.countNormal,
                SliderCount = beatmap.countSlider,
                SpinnerCount = beatmap.countSpinner
            });

            return difficultyAttributes;
        }

        protected async Task UpdateTotals(uint userId, int rulesetId)
        {
            List<SoloScoreWithPerformance> scores;

            using (var db = Queue.GetDatabaseConnection())
            {
                scores = (await db.QueryAsync<SoloScoreWithPerformance>(
                    $"SELECT s.*, p.pp AS `pp` FROM {SoloScore.TABLE_NAME} s "
                    + $"JOIN {SoloScorePerformance.TABLE_NAME} p ON s.id = p.score_id "
                    + $"WHERE s.user_id = @UserId "
                    + $"AND s.ruleset_id = @RulesetId", new
                    {
                        UserId = userId,
                        RulesetId = rulesetId
                    })).ToList();
            }

            // Populate builds for the scores.
            foreach (var s in scores)
            {
                if (s.pp == null || s.ScoreInfo.build_id == null)
                    continue;

                if (buildsCache.ContainsKey(s.ScoreInfo.build_id.Value))
                    continue;

                using (var db = Queue.GetDatabaseConnection())
                {
                    Build? build = await db.QuerySingleOrDefaultAsync<Build>($"SELECT * FROM {Build.TABLE_NAME} WHERE `build_id` = @BuildId", new
                    {
                        BuildId = s.ScoreInfo.build_id.Value
                    });

                    if (build == null)
                        await Console.Error.WriteLineAsync($"Build {s.ScoreInfo.build_id.Value} was not found for score {s.id}, skipping...");

                    buildsCache[s.ScoreInfo.build_id.Value] = build?.allow_performance ?? false;
                }
            }

            // Filter out invalid scores.
            scores.RemoveAll(s =>
            {
                // Score must have a valid pp.
                if (s.pp == null)
                    return true;

                // Scores with no build were imported from the legacy high scores tables and are always valid.
                if (s.ScoreInfo.build_id == null)
                    return false;

                // Performance needs to be allowed for the build.
                return !buildsCache[s.ScoreInfo.build_id.Value];
            });

            SoloScoreWithPerformance[] groupedItems = scores
                                                      // Group by beatmap ID.
                                                      .GroupBy(i => i.beatmap_id)
                                                      // Extract the maximum PP for each beatmap.
                                                      .Select(g => g.OrderByDescending(i => i.pp).First())
                                                      // And order the beatmaps by decreasing value.
                                                      .OrderByDescending(i => i.pp)
                                                      .ToArray();

            // Build the diminishing sum
            double factor = 1;
            double totalPp = 0;
            double totalAccuracy = 0;

            foreach (var item in groupedItems)
            {
                totalPp += item.pp!.Value * factor;
                totalAccuracy += item.ScoreInfo.accuracy * factor;
                factor *= 0.95;
            }

            // This weird factor is to keep legacy compatibility with the diminishing bonus of 0.25 by 0.9994 each score.
            totalPp += (417.0 - 1.0 / 3.0) * (1.0 - Math.Pow(0.9994, groupedItems.Length));

            // We want our accuracy to be normalized.
            if (groupedItems.Length > 0)
            {
                // We want the percentage, not a factor in [0, 1], hence we divide 20 by 100.
                totalAccuracy *= 100.0 / (20 * (1 - Math.Pow(0.95, groupedItems.Length)));
            }

            LegacyDatabaseHelper.RulesetDatabaseInfo databaseInfo = LegacyDatabaseHelper.GetRulesetSpecifics(rulesetId);

            using (var db = Queue.GetDatabaseConnection())
            {
                await db.ExecuteAsync($"UPDATE {databaseInfo.UserStatsTable} SET `rank_score` = @Pp, `accuracy_new` = @Accuracy WHERE `user_id` = @UserId", new
                {
                    UserId = userId,
                    Pp = totalPp,
                    Accuracy = totalAccuracy
                });
            }
        }

        private record struct DifficultyAttributeKey(int BeatmapId, int RulesetId, uint ModValue);

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        [Serializable]
        private class SoloScoreWithPerformance : SoloScore
        {
            public double? pp { get; set; }
        }
    }
}
