// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics;
using Dapper;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Updates the rank achieved tallies for users.
    /// </summary>
    [UsedImplicitly]
    public class UserRankCountProcessor : IProcessor
    {
        public bool RunOnFailedScores => false;

        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (!score.Passed)
                return;

            if (previousVersion >= 7)
            {
                // It is assumed that in the case of a revert, either the score is deleted, or a reapplication will immediately follow.

                // First, see if the score we're reverting is the user's best (and as such included in the rank counts).
                var bestScore = getBestScore(score, conn, transaction);

                // If this score isn't the user's best on the beatmap, nothing needs to be reverted.
                if (bestScore?.ID != score.ID)
                    return;

                // If it is, remove the rank before applying the next-best.
                removeRank(userStats, score.Rank);

                var secondBestScore = getSecondBestScore(score, conn, transaction);
                if (secondBestScore != null)
                    addRank(userStats, secondBestScore.Rank);
            }
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (!score.Passed)
                return;

            var bestScore = getBestScore(score, conn, transaction);

            // If there's already another higher score than this one, nothing needs to be done.
            if (bestScore?.ID != score.ID)
                return;

            // If this score is the new best and there's a previous higher score, that score's rank should be removed before we apply the new one.
            var secondBestScore = getSecondBestScore(score, conn, transaction);
            if (secondBestScore != null)
                removeRank(userStats, secondBestScore.Rank);

            Debug.Assert(bestScore != null);
            addRank(userStats, bestScore.Rank);
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }

        private static SoloScoreInfo? getSecondBestScore(SoloScoreInfo score, MySqlConnection conn, MySqlTransaction transaction)
            => getBestScore(score, conn, transaction, 1);

        private static SoloScoreInfo? getBestScore(SoloScoreInfo score, MySqlConnection conn, MySqlTransaction transaction, int offset = 0)
        {
            var rankSource = conn.QueryFirstOrDefault<SoloScore?>(
                "SELECT * FROM solo_scores WHERE `user_id` = @user_id "
                + "AND `beatmap_id` = @beatmap_id "
                + "AND `ruleset_id` = @ruleset_id "
                // preserve is not flagged on the newly arriving score until it has been completely processed (see logic in `ScoreStatisticsQueueProcessor.cs`)
                // therefore we need to make an exception here to ensure it's included.
                + "AND (`preserve` = 1 OR `id` = @new_score_id) "
                + "ORDER BY `data`->'$.total_score' DESC, `id` DESC "
                + "LIMIT @offset, 1", new
                {
                    user_id = score.UserID,
                    beatmap_id = score.BeatmapID,
                    ruleset_id = score.RulesetID,
                    new_score_id = score.ID,
                    offset = offset,
                }, transaction);

            return rankSource?.ScoreInfo;
        }

        private static void addRank(UserStats stats, ScoreRank rank)
            => updateRankCounts(stats, rank, revert: false);

        private static void removeRank(UserStats stats, ScoreRank rank)
            => updateRankCounts(stats, rank, revert: true);

        private static void updateRankCounts(UserStats stats, ScoreRank rank, bool revert)
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
    }
}
