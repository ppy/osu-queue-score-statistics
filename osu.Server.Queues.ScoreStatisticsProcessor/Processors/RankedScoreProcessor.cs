// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics;
using Dapper;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Beatmaps;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    [UsedImplicitly]
    public class RankedScoreProcessor : IProcessor
    {
        public bool RunOnFailedScores => false;

        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (!score.Passed)
                return;

            if (!isBeatmapValidForRankedScore(score.BeatmapID, conn, transaction))
                return;

            if (previousVersion >= 8)
            {
                // It is assumed that in the case of a revert, either the score is deleted, or a reapplication will immediately follow.

                // First, see if the score we're reverting is the user's best (and as such included in the total ranked score).
                var bestScore = getBestScore(score, conn, transaction);

                // If this score isn't the user's best on the beatmap, nothing needs to be reverted.
                if (bestScore?.ID != score.ID)
                    return;

                // If it is, unapply from total ranked score before applying the next-best.
                updateRankedScore(score, userStats, revert: true);

                var secondBestScore = getSecondBestScore(score, conn, transaction);
                if (secondBestScore != null)
                    updateRankedScore(secondBestScore, userStats, revert: false);
            }
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (!score.Passed)
                return;

            if (!isBeatmapValidForRankedScore(score.BeatmapID, conn, transaction))
                return;

            // Note that most of the below code relies on the fact that classic scoring mode
            // does not reorder scores.
            // Therefore, we will be operating on standardised score right until the actual part
            // where we increase the user's ranked score - at which point we will use classic
            // to meet past user expectations.

            var bestScore = getBestScore(score, conn, transaction);

            // If there's already another higher score than this one, nothing needs to be done.
            if (bestScore?.ID != score.ID)
                return;

            // If this score is the new best and there's a previous higher score,
            // that score's total should be unapplied from the user's ranked total
            // before we apply the new one.
            var secondBestScore = getSecondBestScore(score, conn, transaction);
            if (secondBestScore != null)
                updateRankedScore(secondBestScore, userStats, revert: true);

            Debug.Assert(bestScore != null);
            updateRankedScore(bestScore, userStats, revert: false);
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }

        private static bool isBeatmapValidForRankedScore(int beatmapId, MySqlConnection conn, MySqlTransaction transaction)
        {
            var status = conn.QuerySingleOrDefault<BeatmapOnlineStatus>("SELECT `approved` FROM osu_beatmaps WHERE `beatmap_id` = @beatmap_id",
                new { beatmap_id = beatmapId },
                transaction);

            // see https://osu.ppy.sh/wiki/en/Gameplay/Score/Ranked_score
            return status == BeatmapOnlineStatus.Ranked
                   || status == BeatmapOnlineStatus.Approved
                   || status == BeatmapOnlineStatus.Loved;
        }

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

        private static SoloScoreInfo? getSecondBestScore(SoloScoreInfo score, MySqlConnection conn, MySqlTransaction transaction)
            => getBestScore(score, conn, transaction, 1);

        private static void updateRankedScore(SoloScoreInfo soloScoreInfo, UserStats stats, bool revert)
        {
            long delta = soloScoreInfo.GetDisplayScore(ScoringMode.Classic) * (revert ? -1 : 1);
            stats.ranked_score += delta;
        }
    }
}
