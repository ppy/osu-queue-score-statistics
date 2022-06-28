// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Beatmap = osu.Server.Queues.ScoreStatisticsProcessor.Models.Beatmap;

namespace osu.Server.Queues.ScorePump.Performance
{
    public class PerformanceProcessor
    {
        private const BeatmapOnlineStatus min_ranked_status = BeatmapOnlineStatus.Ranked;
        private const BeatmapOnlineStatus max_ranked_status = BeatmapOnlineStatus.Approved;

        private readonly ConcurrentDictionary<int, Beatmap?> beatmapCache = new ConcurrentDictionary<int, Beatmap?>();
        private readonly ConcurrentDictionary<DifficultyAttributeKey, BeatmapDifficultyAttribute[]?> attributeCache = new ConcurrentDictionary<DifficultyAttributeKey, BeatmapDifficultyAttribute[]?>();

        private Func<MySqlConnection> getConnection { get; init; } = null!;
        private ConcurrentDictionary<int, bool> builds { get; init; } = null!;
        private ConcurrentDictionary<BlacklistEntry, byte> blacklist { get; init; } = null!;

        private PerformanceProcessor()
        {
        }

        [MemberNotNull(nameof(getConnection), nameof(builds), nameof(blacklist))]
        public static async Task<PerformanceProcessor> Create(Func<MySqlConnection> getConnection)
        {
            using (var db = getConnection())
            {
                var dbBuilds = await db.QueryAsync<Build>($"SELECT * FROM {Build.TABLE_NAME}");
                var dbBlacklist = await db.QueryAsync<PerformanceBlacklistEntry>($"SELECT * FROM {PerformanceBlacklistEntry.TABLE_NAME}");

                return new PerformanceProcessor
                {
                    getConnection = getConnection,
                    builds = new ConcurrentDictionary<int, bool>(dbBuilds.Select(b => new KeyValuePair<int, bool>(b.build_id, b.allow_performance))),
                    blacklist = new ConcurrentDictionary<BlacklistEntry, byte>(dbBlacklist.Select(b => new KeyValuePair<BlacklistEntry, byte>(new BlacklistEntry(b.beatmap_id, b.mode), 1)))
                };
            }
        }

        /// <summary>
        /// Sets a count in the database.
        /// </summary>
        /// <param name="key">The count's key.</param>
        /// <param name="value">The count's value.</param>
        public async Task SetCount(string key, long value)
        {
            using (var db = getConnection())
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
        public async Task<long> GetCount(string key)
        {
            using (var db = getConnection())
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

        public async Task ProcessUser(uint userId, int rulesetId)
        {
            SoloScore[] scores;

            using (var db = getConnection())
            {
                scores = (await db.QueryAsync<SoloScore>($"SELECT * FROM {SoloScore.TABLE_NAME} WHERE `user_id` = @UserId AND `ruleset_id` = @RulesetId", new
                {
                    UserId = userId,
                    RulesetId = rulesetId
                })).ToArray();
            }

            foreach (SoloScore score in scores)
                await ProcessScore(score);
        }

        public async Task ProcessScore(ulong scoreId)
        {
            SoloScore? score;

            using (var db = getConnection())
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

        public async Task ProcessScore(SoloScore score)
        {
            try
            {
                if (blacklist.ContainsKey(new BlacklistEntry(score.beatmap_id, score.ruleset_id)))
                    return;

                Beatmap? beatmap = await GetBeatmap(score.beatmap_id);

                if (beatmap == null)
                    return;

                if (beatmap.approved < min_ranked_status || beatmap.approved > max_ranked_status)
                    return;

                Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(score.ruleset_id);
                Mod[] mods = score.ScoreInfo.mods.Select(m => m.ToMod(ruleset)).ToArray();

                DifficultyAttributes? difficultyAttributes = await GetDifficultyAttributes(beatmap, ruleset, mods);
                if (difficultyAttributes == null)
                    return;

                ScoreInfo scoreInfo = score.ScoreInfo.ToScoreInfo(mods);
                PerformanceAttributes? performanceAttributes = ruleset.CreatePerformanceCalculator()?.Calculate(scoreInfo, difficultyAttributes);
                if (performanceAttributes == null)
                    return;

                using (var db = getConnection())
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
        /// <param name="beatmap">The beatmap.</param>
        /// <param name="ruleset">The score's ruleset.</param>
        /// <param name="mods">The score's mods.</param>
        /// <returns>The difficulty attributes.</returns>
        public async Task<DifficultyAttributes?> GetDifficultyAttributes(Beatmap beatmap, Ruleset ruleset, Mod[] mods)
        {
            BeatmapDifficultyAttribute[]? rawDifficultyAttributes;

            using (var db = getConnection())
            {
                // Todo: We shouldn't be using legacy mods, but this requires difficulty calculation to be performed in-line.
                LegacyMods legacyModValue = LegacyModsHelper.MaskRelevantMods(ruleset.ConvertToLegacyMods(mods), ruleset.RulesetInfo.OnlineID != beatmap.playmode, ruleset.RulesetInfo.OnlineID);
                DifficultyAttributeKey key = new DifficultyAttributeKey(beatmap.beatmap_id, ruleset.RulesetInfo.OnlineID, (uint)legacyModValue);

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
            }

            if (rawDifficultyAttributes == null)
                return null;

            DifficultyAttributes difficultyAttributes = LegacyRulesetHelper.CreateDifficultyAttributes(ruleset.RulesetInfo.OnlineID);
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

        public async Task<Beatmap?> GetBeatmap(int beatmapId)
        {
            if (beatmapCache.TryGetValue(beatmapId, out var beatmap))
                return beatmap;

            using (var db = getConnection())
            {
                return beatmapCache[beatmapId] = await db.QuerySingleOrDefaultAsync<Beatmap?>("SELECT * FROM osu_beatmaps WHERE beatmap_id = @BeatmapId", new
                {
                    BeatmapId = beatmapId
                });
            }
        }

        public async Task UpdateTotals(uint userId, int rulesetId)
        {
            List<SoloScoreWithPerformance> scores;

            using (var db = getConnection())
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

            // Filter out invalid scores.
            scores.RemoveAll(s =>
            {
                // Score must have a valid pp.
                if (s.pp == null)
                    return true;

                // The beatmap/ruleset combo must not be blacklisted.
                if (blacklist.ContainsKey(new BlacklistEntry(s.beatmap_id, s.ruleset_id)))
                    return true;

                // Scores with no build were imported from the legacy high scores tables and are always valid.
                if (s.ScoreInfo.build_id == null)
                    return false;

                // Performance needs to be allowed for the build.
                return !builds[s.ScoreInfo.build_id.Value];
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

            using (var db = getConnection())
            {
                await db.ExecuteAsync($"UPDATE {databaseInfo.UserStatsTable} SET `rank_score` = @Pp, `accuracy_new` = @Accuracy WHERE `user_id` = @UserId", new
                {
                    UserId = userId,
                    Pp = totalPp,
                    Accuracy = totalAccuracy
                });
            }
        }

        private record struct DifficultyAttributeKey(uint BeatmapId, int RulesetId, uint ModValue);

        private record struct BlacklistEntry(int BeatmapId, int RulesetId);

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        [Serializable]
        private class SoloScoreWithPerformance : SoloScore
        {
            public double? pp { get; set; }
        }
    }
}
