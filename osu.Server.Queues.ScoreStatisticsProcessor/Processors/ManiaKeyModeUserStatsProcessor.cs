// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Rulesets.Mania.Beatmaps;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    [UsedImplicitly]
    public class ManiaKeyModeUserStatsProcessor : IProcessor
    {
        public int Order => int.MaxValue;

        public bool RunOnFailedScores => false;
        public bool RunOnLegacyScores => true;

        public void RevertFromUserStats(SoloScore score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction, List<Action> postTransactionActions)
        {
        }

        public void ApplyToUserStats(SoloScore score, UserStats fullRulesetStats, MySqlConnection conn, MySqlTransaction transaction, List<Action> postTransactionActions)
        {
            if (score.ruleset_id != 3)
                return;

            if (score.beatmap == null)
                return;

            var conversionDifficultyInfo = score.beatmap.GetLegacyBeatmapConversionDifficultyInfo();
            var mods = score.ScoreData.Mods.Select(m => m.ToMod(LegacyRulesetHelper.GetRulesetFromLegacyId(3))).ToList();
            int keyCount = ManiaBeatmapConverter.GetColumnCount(conversionDifficultyInfo, mods);

            if (keyCount != 4 && keyCount != 7)
                return;

            string keyCountTableName = $"osu_user_stats_mania_{keyCount}k";

            UserStatsManiaKeyCount? keyModeStats = conn.QueryFirstOrDefault<UserStatsManiaKeyCount>(
                $"SELECT * FROM `{keyCountTableName}` WHERE `user_id` = @user_id", new { user_id = score.user_id }, transaction);

            // Initial population, since this gets created on demand by this processor.
            if (keyModeStats == null)
            {
                keyModeStats = new UserStatsManiaKeyCount
                {
                    user_id = score.user_id,
                    // make up a rough play count based on user play distribution.
                    // TODO: this does not work for converts, and probably cannot ever work efficiently unless key counts start getting stored against the scores themselves
                    playcount = conn.QuerySingle<int?>(
                        "SELECT @playcount * (SELECT COUNT(1) FROM `scores` "
                        + "WHERE `user_id` = @userId "
                        + "AND `beatmap_id` IN (SELECT `beatmap_id` FROM `osu_beatmaps` WHERE `playmode` = @rulesetId AND `diff_size` = @keyCount)) "
                        + "/ (SELECT GREATEST(1, COUNT(*)) FROM `scores` WHERE `user_id` = @userId AND `ruleset_id` = @rulesetId)",
                        new
                        {
                            playcount = fullRulesetStats.playcount,
                            userId = score.user_id,
                            rulesetId = score.ruleset_id,
                            keyCount = keyCount,
                        }, transaction) ?? 0,
                };

                conn.Execute(
                    $"REPLACE INTO `{keyCountTableName}` "
                    + $"(`user_id`, `country_acronym`, `playcount`, `x_rank_count`, `xh_rank_count`, `s_rank_count`, `sh_rank_count`, `a_rank_count`, `rank_score`, `rank_score_index`, `accuracy_new`, `last_played`) "
                    + $"SELECT @user_id, `country_acronym`, @playcount, @x_rank_count, @xh_rank_count, @s_rank_count, @sh_rank_count, @a_rank_count, @rank_score, @rank_score_index, @accuracy_new, @last_played "
                    + $"FROM `phpbb_users` WHERE `user_id` = @user_id",
                    keyModeStats, transaction);
            }
            else
            {
                keyModeStats.playcount++;
            }

            if (score.preserve)
            {
                updateRankCounts(score, keyModeStats, conn, transaction);
                updateRankedScore(score, keyModeStats, conn, transaction);
                UpdateUserStatsAsync(keyModeStats, keyCount, conn, transaction).Wait();

                conn.Execute(
                    $"UPDATE `{keyCountTableName}` "
                    + $"SET `rank_score` = @rank_score, `ranked_score` = @ranked_score, `playcount` = @playcount, `rank_score_index` = @rank_score_index, `accuracy_new` = @accuracy_new, "
                    + $"`x_rank_count` = @x_rank_count, `xh_rank_count` = @xh_rank_count, `s_rank_count` = @s_rank_count, `sh_rank_count` = @sh_rank_count, `a_rank_count` = @a_rank_count, `last_played` = NOW()"
                    + $"WHERE `user_id` = @user_id", keyModeStats, transaction);
            }
            else
            {
                conn.Execute($"UPDATE `{keyCountTableName}` SET `playcount` = @playcount, `last_played` = NOW() WHERE `user_id` = @user_id", keyModeStats, transaction);
            }
        }

        /// <summary>
        /// Updates a user's key-specific stats with their total PP/accuracy.
        /// </summary>
        /// <remarks>
        /// This does not insert the new stats values into the database.
        /// </remarks>
        public async Task UpdateUserStatsAsync(UserStatsManiaKeyCount keyModeStats, int keyCount, MySqlConnection conn, MySqlTransaction? transaction = null, bool updateIndex = true)
        {
            string keyCountTableName = $"osu_user_stats_mania_{keyCount}k";

            List<SoloScore> scores = (await conn.QueryAsync<SoloScore>(
                "SELECT beatmap_id, pp, accuracy FROM scores WHERE "
                + "`user_id` = @UserId AND "
                + "`ruleset_id` = @RulesetId AND "
                + "`pp` IS NOT NULL AND "
                + "`preserve` = 1 AND "
                + "`ranked` = 1 AND "
                + "`beatmap_id` IN (SELECT `beatmap_id` FROM `osu_beatmaps` WHERE `playmode` = @RulesetId AND `diff_size` = @KeyCount) "
                + "ORDER BY pp DESC LIMIT 1000", new
                {
                    UserId = keyModeStats.user_id,
                    RulesetId = 3,
                    KeyCount = keyCount,
                }, transaction: transaction)).ToList();

            (keyModeStats.rank_score, keyModeStats.accuracy_new) = UserTotalPerformanceAggregateHelper.CalculateUserTotalPerformanceAggregates(keyModeStats.user_id, scores);

            if (updateIndex)
            {
                // TODO: partitioned caching similar to UserTotalPerformanceProcessor.
                keyModeStats.rank_score_index = await conn.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {keyCountTableName} WHERE rank_score > {keyModeStats.rank_score}", transaction: transaction)
                                                + 1;
            }
        }

        // local reimplementation of `RankedScoreProcessor` for keymodes.
        private void updateRankedScore(SoloScore score, UserStatsManiaKeyCount keymodeStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (!score.BeatmapValidForRankedCounts())
                return;

            // Note that most of the below code relies on the fact that classic scoring mode
            // does not reorder scores.
            // Therefore, we will be operating on standardised score right until the actual part
            // where we increase the user's ranked score - at which point we will use classic
            // to meet past user expectations.

            var bestScore = DatabaseHelper.GetUserBestScoreFor(score, conn, transaction);

            // If there's already another higher score than this one, nothing needs to be done.
            if (bestScore?.id != score.id)
                return;

            // If this score is the new best and there's a previous higher score,
            // that score's total should be unapplied from the user's ranked total
            // before we apply the new one.
            var secondBestScore = DatabaseHelper.GetUserBestScoreFor(score, conn, transaction, offset: 1);
            if (secondBestScore != null)
                updateRankedScore(secondBestScore, keymodeStats, revert: true);

            Debug.Assert(bestScore != null);
            updateRankedScore(bestScore, keymodeStats, revert: false);
        }

        private static void updateRankedScore(SoloScore soloScore, UserStatsManiaKeyCount stats, bool revert)
        {
            long delta = soloScore.ToScoreInfo().GetDisplayScore(ScoringMode.Classic) * (revert ? -1 : 1);
            stats.ranked_score += delta;
        }

        // local reimplementation of `UserRankCountProcessor` for keymodes.
        // it's a bit unfortunate, but this is the only way this can be implemented for now until `preserve = 0` is set on lazer scores correctly.
        private void updateRankCounts(SoloScore score, UserStatsManiaKeyCount keymodeStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (!score.BeatmapValidForRankedCounts())
                return;

            var bestScore = DatabaseHelper.GetUserBestScoreFor(score, conn, transaction);

            // If there's already another higher score than this one, nothing needs to be done.
            if (bestScore?.id != score.id)
                return;

            // If this score is the new best and there's a previous higher score, that score's rank should be removed before we apply the new one.
            var secondBestScore = DatabaseHelper.GetUserBestScoreFor(score, conn, transaction, offset: 1);
            if (secondBestScore != null)
                updateRankCounts(keymodeStats, secondBestScore.rank, revert: true);

            Debug.Assert(bestScore != null);
            updateRankCounts(keymodeStats, bestScore.rank, revert: false);
        }

        private static void updateRankCounts(UserStatsManiaKeyCount stats, ScoreRank rank, bool revert)
        {
            int delta = revert ? -1 : 1;

            switch (rank)
            {
                case ScoreRank.XH:
                    stats.xh_rank_count += delta;
                    break;

                case ScoreRank.X:
                    stats.x_rank_count += delta;
                    break;

                case ScoreRank.SH:
                    stats.sh_rank_count += delta;
                    break;

                case ScoreRank.S:
                    stats.s_rank_count += delta;
                    break;

                case ScoreRank.A:
                    stats.a_rank_count += delta;
                    break;
            }
        }

        public void ApplyGlobal(SoloScore score, MySqlConnection conn)
        {
        }
    }
}
