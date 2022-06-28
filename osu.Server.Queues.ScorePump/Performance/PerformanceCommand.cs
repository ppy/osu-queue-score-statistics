// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
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
                LegacyMods legacyModValue = LegacyModsHelper.MaskRelevantMods(ruleset.ConvertToLegacyMods(mods), score.ruleset_id != beatmap.playmode);
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

        private record struct DifficultyAttributeKey(int BeatmapId, int RulesetId, uint ModValue);
    }
}
