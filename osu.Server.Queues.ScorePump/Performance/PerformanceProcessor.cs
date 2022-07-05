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
    /// <summary>
    /// A helper class which can be used to compute scores' raw PP values and users' total PP values.
    /// </summary>
    public class PerformanceProcessor
    {
        private const BeatmapOnlineStatus min_ranked_status = BeatmapOnlineStatus.Ranked;
        private const BeatmapOnlineStatus max_ranked_status = BeatmapOnlineStatus.Approved;

        private readonly ConcurrentDictionary<int, Beatmap?> beatmapCache = new ConcurrentDictionary<int, Beatmap?>();
        private readonly ConcurrentDictionary<DifficultyAttributeKey, BeatmapDifficultyAttribute[]?> attributeCache = new ConcurrentDictionary<DifficultyAttributeKey, BeatmapDifficultyAttribute[]?>();

        private readonly ConcurrentDictionary<int, bool> builds;
        private readonly ConcurrentDictionary<BlacklistEntry, byte> blacklist;

        private PerformanceProcessor(IEnumerable<KeyValuePair<int, bool>> builds, IEnumerable<KeyValuePair<BlacklistEntry, byte>> blacklist)
        {
            this.builds = new ConcurrentDictionary<int, bool>(builds);
            this.blacklist = new ConcurrentDictionary<BlacklistEntry, byte>(blacklist);
        }

        /// <summary>
        /// Creates a new <see cref="PerformanceProcessor"/>.
        /// </summary>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        /// <returns>The created <see cref="PerformanceProcessor"/>.</returns>
        public static async Task<PerformanceProcessor> CreateAsync(MySqlConnection? connection, MySqlTransaction? transaction = null)
        {
            var dbBuilds = await connection.QueryAsync<Build>($"SELECT * FROM {Build.TABLE_NAME}", transaction: transaction);
            var dbBlacklist = await connection.QueryAsync<PerformanceBlacklistEntry>($"SELECT * FROM {PerformanceBlacklistEntry.TABLE_NAME}", transaction: transaction);

            return new PerformanceProcessor
            (
                dbBuilds.Select(b => new KeyValuePair<int, bool>(b.build_id, b.allow_performance)),
                dbBlacklist.Select(b => new KeyValuePair<BlacklistEntry, byte>(new BlacklistEntry(b.beatmap_id, b.mode), 1))
            );
        }

        /// <summary>
        /// Sets a count in the database.
        /// </summary>
        /// <param name="key">The count's key.</param>
        /// <param name="value">The count's value.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        public async Task SetCountAsync(string key, long value, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            await connection.ExecuteAsync("INSERT INTO `osu_counts` (`name`,`count`) VALUES (@Name, @Count) "
                                          + "ON DUPLICATE KEY UPDATE `name` = VALUES(`name`), `count` = VALUES(`count`)", new
            {
                Name = key,
                Count = value
            }, transaction: transaction);
        }

        /// <summary>
        /// Retrieves a count value from the database.
        /// </summary>
        /// <param name="key">The count's key.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        /// <returns>The count for the provided key.</returns>
        /// <exception cref="InvalidOperationException">If the key wasn't found in the database.</exception>
        public async Task<long> GetCountAsync(string key, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            long? res = await connection.QuerySingleOrDefaultAsync<long?>("SELECT `count` FROM `osu_counts` WHERE `name` = @Name", new
            {
                Name = key
            }, transaction: transaction);

            if (res == null)
                throw new InvalidOperationException($"Unable to retrieve count '{key}'.");

            return res.Value;
        }

        /// <summary>
        /// Processes the raw PP value of all scores from a specified user.
        /// </summary>
        /// <param name="userId">The user to process all scores of.</param>
        /// <param name="rulesetId">The ruleset for which scores should be processed.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        public async Task ProcessUserScoresAsync(uint userId, int rulesetId, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            var scores = (await connection.QueryAsync<SoloScore>($"SELECT * FROM {SoloScore.TABLE_NAME} WHERE `user_id` = @UserId AND `ruleset_id` = @RulesetId", new
            {
                UserId = userId,
                RulesetId = rulesetId
            }, transaction: transaction)).ToArray();

            foreach (SoloScore score in scores)
                await ProcessScoreAsync(score, connection, transaction);
        }

        /// <summary>
        /// Processes the raw PP value of a given score.
        /// </summary>
        /// <param name="scoreId">The score to process.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        public async Task ProcessScoreAsync(ulong scoreId, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            var score = await connection.QuerySingleOrDefaultAsync<SoloScore>($"SELECT * FROM {SoloScore.TABLE_NAME} WHERE `id` = @ScoreId", new
            {
                ScoreId = scoreId
            }, transaction: transaction);

            if (score == null)
            {
                await Console.Error.WriteLineAsync($"Could not find score ID {scoreId}.");
                return;
            }

            await ProcessScoreAsync(score, connection, transaction);
        }

        /// <summary>
        /// Processes the raw PP value of a given score.
        /// </summary>
        /// <param name="score">The score to process.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        public Task ProcessScoreAsync(SoloScore score, MySqlConnection connection, MySqlTransaction? transaction = null) => ProcessScoreAsync(score.ScoreInfo, connection, transaction);

        /// <summary>
        /// Processes the raw PP value of a given score.
        /// </summary>
        /// <param name="score">The score to process.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        public async Task ProcessScoreAsync(SoloScoreInfo score, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            try
            {
                if (blacklist.ContainsKey(new BlacklistEntry(score.beatmap_id, score.ruleset_id)))
                    return;

                Beatmap? beatmap = await GetBeatmapAsync(score.beatmap_id, connection, transaction);

                if (beatmap == null)
                    return;

                if (beatmap.approved < min_ranked_status || beatmap.approved > max_ranked_status)
                    return;

                Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(score.ruleset_id);
                Mod[] mods = score.mods.Select(m => m.ToMod(ruleset)).ToArray();

                DifficultyAttributes? difficultyAttributes = await GetDifficultyAttributesAsync(beatmap, ruleset, mods, connection, transaction);
                if (difficultyAttributes == null)
                    return;

                ScoreInfo scoreInfo = score.ToScoreInfo(mods);
                PerformanceAttributes? performanceAttributes = ruleset.CreatePerformanceCalculator()?.Calculate(scoreInfo, difficultyAttributes);
                if (performanceAttributes == null)
                    return;

                await connection.ExecuteAsync($"INSERT INTO {SoloScorePerformance.TABLE_NAME} (`score_id`, `pp`) VALUES (@ScoreId, @Pp) ON DUPLICATE KEY UPDATE `pp` = @Pp", new
                {
                    ScoreId = score.id,
                    Pp = performanceAttributes.Total
                }, transaction: transaction);
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
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        /// <returns>The difficulty attributes or <c>null</c> if not existing.</returns>
        public async Task<DifficultyAttributes?> GetDifficultyAttributesAsync(Beatmap beatmap, Ruleset ruleset, Mod[] mods, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            BeatmapDifficultyAttribute[]? rawDifficultyAttributes;

            // Todo: We shouldn't be using legacy mods, but this requires difficulty calculation to be performed in-line.
            LegacyMods legacyModValue = LegacyModsHelper.MaskRelevantMods(ruleset.ConvertToLegacyMods(mods), ruleset.RulesetInfo.OnlineID != beatmap.playmode, ruleset.RulesetInfo.OnlineID);
            DifficultyAttributeKey key = new DifficultyAttributeKey(beatmap.beatmap_id, ruleset.RulesetInfo.OnlineID, (uint)legacyModValue);

            if (!attributeCache.TryGetValue(key, out rawDifficultyAttributes))
            {
                rawDifficultyAttributes = attributeCache[key] = (await connection.QueryAsync<BeatmapDifficultyAttribute>(
                    $"SELECT * FROM {BeatmapDifficultyAttribute.TABLE_NAME} WHERE `beatmap_id` = @BeatmapId AND `mode` = @RulesetId AND `mods` = @ModValue", new
                    {
                        BeatmapId = key.BeatmapId,
                        RulesetId = key.RulesetId,
                        ModValue = key.ModValue
                    }, transaction: transaction)).ToArray();
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

        /// <summary>
        /// Retrieves a beatmap from the database.
        /// </summary>
        /// <param name="beatmapId">The beatmap's ID.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        /// <returns>The retrieved beatmap, or <c>null</c> if not existing.</returns>
        public async Task<Beatmap?> GetBeatmapAsync(int beatmapId, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            if (beatmapCache.TryGetValue(beatmapId, out var beatmap))
                return beatmap;

            return beatmapCache[beatmapId] = await connection.QuerySingleOrDefaultAsync<Beatmap?>($"SELECT * FROM {Beatmap.TABLE_NAME} WHERE `beatmap_id` = @BeatmapId", new
            {
                BeatmapId = beatmapId
            }, transaction: transaction);
        }

        /// <summary>
        /// Updates a user's stats with their total PP/accuracy.
        /// </summary>
        /// <remarks>
        /// This does not insert the new stats values into the database.
        /// </remarks>
        /// <param name="rulesetId">The ruleset for which to update the total PP.</param>
        /// <param name="userStats">An existing <see cref="UserStats"/> object to update with.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        public async Task UpdateUserStatsAsync(UserStats userStats, int rulesetId, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            List<SoloScoreWithPerformance> scores = (await connection.QueryAsync<SoloScoreWithPerformance>(
                $"SELECT `s`.*, `p`.`pp` AS `pp` FROM {SoloScore.TABLE_NAME} `s` "
                + $"JOIN {SoloScorePerformance.TABLE_NAME} `p` ON `s`.`id` = `p`.`score_id` "
                + $"WHERE `s`.`user_id` = @UserId "
                + $"AND `s`.`ruleset_id` = @RulesetId", new
                {
                    UserId = userStats.user_id,
                    RulesetId = rulesetId
                }, transaction: transaction)).ToList();

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

            userStats.rank_score = (float)totalPp;
            userStats.accuracy_new = (float)totalAccuracy;
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
