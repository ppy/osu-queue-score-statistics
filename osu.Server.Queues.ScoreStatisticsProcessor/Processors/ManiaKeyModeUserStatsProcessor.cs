// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Scoring;
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

        public void RevertFromUserStats(SoloScore score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
        }

        public void ApplyToUserStats(SoloScore score, UserStats fullRulesetStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (score.ruleset_id != 3)
                return;

            if (score.beatmap == null)
                return;

            int keyCount = (int)score.beatmap.diff_size;
            // TODO: reconsider when handling key conversion mods in the future?
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

            keyModeStats.playcount++;

            if (score.preserve)
            {
                updateRankCounts(score, keyModeStats, conn, transaction);
                UpdateUserStatsAsync(keyModeStats, keyCount, conn, transaction).Wait();
            }
            else
            {
                conn.Execute($"UPDATE `{keyCountTableName}` SET `playcount` = `playcount` + 1, `last_played` = NOW() WHERE `user_id` = @user_id", keyModeStats, transaction);
            }
        }

        /// <summary>
        /// Updates a user's stats with their total PP/accuracy.
        /// </summary>
        /// <remarks>
        /// This does not insert the new stats values into the database, but does update existing.
        /// </remarks>
        public async Task UpdateUserStatsAsync(UserStatsManiaKeyCount keyModeStats, int keyCount, MySqlConnection conn, MySqlTransaction? transaction = null, bool updateIndex = true)
        {
            string keyCountTableName = $"osu_user_stats_mania_{keyCount}k";

            List<SoloScore> scores = conn.Query<SoloScore>(
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
                }, transaction: transaction).ToList();

            (keyModeStats.rank_score, keyModeStats.accuracy_new) = UserTotalPerformanceAggregateHelper.CalculateUserTotalPerformanceAggregates(keyModeStats.user_id, scores);

            if (updateIndex)
            {
                // TODO: partitioned caching similar to UserTotalPerformanceProcessor.
                keyModeStats.rank_score_index = conn.QuerySingle<int>($"SELECT COUNT(*) FROM {keyCountTableName} WHERE rank_score > {keyModeStats.rank_score}", transaction: transaction) + 1;
            }

            await conn.ExecuteAsync(
                $"UPDATE `{keyCountTableName}` "
                + $"SET `rank_score` = @rank_score, `playcount` = @playcount, `rank_score_index` = @rank_score_index, `accuracy_new` = @accuracy_new, "
                + $"`x_rank_count` = @x_rank_count, `xh_rank_count` = @xh_rank_count, `s_rank_count` = @s_rank_count, `sh_rank_count` = @sh_rank_count, `a_rank_count` = @a_rank_count "
                + $"WHERE `user_id` = @user_id", keyModeStats, transaction);
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
