// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

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
            if (previousVersion >= 7)
            {
                // note that this implementation is only supposed to be correct if a revert is immediately succeeded by a reprocessing of the same score.
                // at the time of writing this is the only case where this method is called, but if that ever changes this will likely need reconsideration.

                // first, see if the score we're reverting is the user's best (and as such included in the rank counts).
                var bestScore = getBestScore(score.UserID, score.BeatmapID, score.RulesetID, excludedScoreId: null, conn, transaction);

                if (bestScore?.ID != score.ID)
                {
                    // this score isn't the user's best on the beatmap, so nothing needs to be reverted.
                    return;
                }

                updateRankCounts(userStats, score.Rank, revert: true);

                // this score is the user's best, so fetch the next best (excluding it) so that we can apply the rank from that score.
                var secondBestScore = getBestScore(score.UserID, score.BeatmapID, score.RulesetID, excludedScoreId: score.ID, conn, transaction);
                if (secondBestScore != null)
                    updateRankCounts(userStats, secondBestScore.Rank, revert: false);
            }
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            // while looking for the best score, exclude the score being processed right now.
            // this does nothing on the first process (as `getBestScore()` already only looks at scores processed at least once),
            // but is important on subsequent reprocessings during `processed_version` bumps.
            var bestScore = getBestScore(score.UserID, score.BeatmapID, score.RulesetID, excludedScoreId: score.ID, conn, transaction);

            if (bestScore == null)
            {
                updateRankCounts(userStats, score.Rank, revert: false);
                return;
            }

            if (score.TotalScore < bestScore.TotalScore) return;
            if (score.TotalScore == bestScore.TotalScore && score.ID <= bestScore.ID) return;

            updateRankCounts(userStats, bestScore.Rank, revert: true);
            updateRankCounts(userStats, score.Rank, revert: false);
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }

        private static SoloScoreInfo? getBestScore(int userId, int beatmapId, int rulesetId, ulong? excludedScoreId, MySqlConnection conn, MySqlTransaction transaction)
        {
            // purpose of join with `process_history` is to only look for already-processed scores
            var rankSource = conn.QueryFirstOrDefault<SoloScore?>(
                "SELECT * FROM solo_scores `s` "
                + "JOIN solo_scores_process_history `ph` ON `ph`.`score_id` = `s`.`id` "
                + "WHERE (@ExcludedScoreId IS NULL OR `s`.`id` != @ExcludedScoreId) "
                + "AND `s`.`user_id` = @UserId "
                + "AND `s`.`beatmap_id` = @BeatmapId "
                + "AND `s`.`ruleset_id` = @RulesetId "
                + "AND `s`.`preserve` = 1 "
                + "ORDER BY `s`.`data`->'$.total_score' DESC, `s`.`id` DESC "
                + "LIMIT 1", new
                {
                    ExcludedScoreId = excludedScoreId,
                    UserId = userId,
                    BeatmapId = beatmapId,
                    RulesetId = rulesetId,
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
