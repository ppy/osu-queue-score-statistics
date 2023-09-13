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
        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (!score.Passed)
                return;

            if (previousVersion >= 7)
            {
                // It is assumed that in the case of a revert, either the score is deleted, or a reapplication will immediately follow.

                // First, see if the score we're reverting is the user's best (and as such included in the rank counts).
                var bestScore = getBestScore(score, conn, transaction);

                if (bestScore?.ID != score.ID)
                {
                    // This score isn't the user's best on the beatmap, so nothing needs to be reverted.
                    return;
                }

                updateRankCounts(userStats, score.Rank, revert: true);

                // This score is the user's best, so fetch the next best so that we can apply the rank from that score.
                var secondBestScore = getSecondBestScore(score, conn, transaction);
                if (secondBestScore != null)
                    updateRankCounts(userStats, secondBestScore.Rank, revert: false);
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

            // If there is, the higher score's rank count should be reverted before we replace it.
            var secondBestScore = getSecondBestScore(score, conn, transaction);
            if (secondBestScore != null)
                updateRankCounts(userStats, secondBestScore.Rank, revert: true);

            Debug.Assert(bestScore != null);
            updateRankCounts(userStats, bestScore.Rank, revert: false);
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
                + "AND `preserve` = 1 OR `id` = @new_score_id "
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
