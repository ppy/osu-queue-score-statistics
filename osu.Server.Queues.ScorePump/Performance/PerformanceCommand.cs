// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using MySqlConnector;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
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

        [Option(CommandOptionType.SingleValue, Template = "-r|--ruleset", Description = "The ruleset to process score for.")]
        public int RulesetId { get; set; }

        protected async Task SetCount(MySqlConnection connection, string key, long value)
        {
            await connection.ExecuteAsync("INSERT INTO `osu_counts` (`name`,`count`) VALUES (@NAME, @COUNT) "
                                          + "ON DUPLICATE KEY UPDATE `name` = VALUES(`name`), `count` = VALUES(`count`)", new
            {
                Name = key,
                Count = value
            });
        }

        protected async Task<long> GetCount(MySqlConnection connection, string key)
        {
            long? res = await connection.QuerySingleOrDefaultAsync<long?>("SELECT `count` FROM `osu_counts` WHERE `name` = @NAME", new
            {
                Name = key
            });

            if (res == null)
                throw new InvalidOperationException($"Unable to retrieve count '{key}'.");

            return res.Value;
        }

        protected async Task<DifficultyAttributes?> GetDifficultyAttributes(SoloScore score, Ruleset ruleset, Mod[] mods)
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
                    return null;

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
                    return null;
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
