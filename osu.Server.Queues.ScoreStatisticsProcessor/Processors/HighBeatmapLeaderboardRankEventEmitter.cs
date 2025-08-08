// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Dapper;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Emit <c>osu_events</c> rows if the score is notable due to being high up enough on the beatmap's leaderboard.
    /// </summary>
    [UsedImplicitly]
    public class HighBeatmapLeaderboardRankEventEmitter : IProcessor
    {
        public bool RunOnFailedScores => false;

        /// <remarks>
        /// While <c>osu-web-10</c> inserts event rows on its own
        /// (https://github.com/peppy/osu-web-10/blob/8ff8ee751672e41b771137a670d40b4e400df65d/www/web/osu-submit-20190809.php#L1198-L1216),
        /// it does so using the old score tables to determine the score's rank.
        /// In other words, it inserts event rows <i>for lazer mode off</i>.
        /// We still want to emit events for the score <i>for lazer mode on</i> here, because the score's position on the lazer mode leaderboard is likely different.
        /// </remarks>
        public bool RunOnLegacyScores => true;

        public void RevertFromUserStats(SoloScore score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
            // we're not even trying this. user events are transient and only last 90 days anyway.
        }

        public void ApplyToUserStats(SoloScore score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (DatabaseHelper.IsUserRestricted(conn, userStats.user_id, transaction))
                return;

            var userBestScore = DatabaseHelper.GetUserBestScoreFor(score, conn, transaction);

            // don't want to emit events if score isn't user's best
            if (userBestScore?.id != score.id)
                return;

            // also don't want to emit events if the user has tied their best.
            // this is possible because `GetUserBestScoreFor()` sorts scores by id descending in case of total score ties.
            // determining that is a bit difficult to do. for leaderboard placements let's just use total score as surrogate of leaderboard position.
            var secondUserBestScore = DatabaseHelper.GetUserBestScoreFor(score, conn, transaction, offset: 1);
            if (secondUserBestScore != null && secondUserBestScore.total_score >= score.total_score)
                return;

            int? scoreRank = WebRequestHelper.GetScoreRankOnBeatmapLeaderboard(score);

            if (scoreRank <= 1000)
            {
                WebRequestHelper.RunSharedInteropCommand($"users/{userStats.user_id}/{score.beatmap_id}/{score.ruleset_id}/rank-achieved", "POST", new
                {
                    position_after = scoreRank,
                    rank = score.rank.ToString(),
                    legacy_score_event = false,
                });
            }

            if (scoreRank == 1)
            {
                int? previousLeaderId = conn.QuerySingleOrDefault<int?>(
                    "SELECT `user_id` FROM `beatmap_leaders` WHERE `beatmap_id` = @BeatmapId AND `ruleset_id` = @RulesetId",
                    new
                    {
                        BeatmapId = score.beatmap_id,
                        RulesetId = score.ruleset_id,
                    }, transaction);

                conn.Execute(
                    "INSERT INTO `beatmap_leaders` (`score_id`, `beatmap_id`, `ruleset_id`, `user_id`) VALUES (@ScoreId, @BeatmapId, @RulesetId, @UserId) " +
                    "ON DUPLICATE KEY UPDATE `user_id` = @UserId, `score_id` = @ScoreId",
                    new
                    {
                        ScoreId = score.id,
                        BeatmapId = score.beatmap_id,
                        RulesetId = score.ruleset_id,
                        UserId = score.user_id,
                    },
                    transaction);

                if (previousLeaderId != null && previousLeaderId != score.user_id && score.beatmap!.playcount > 100)
                {
                    WebRequestHelper.RunSharedInteropCommand($"users/{previousLeaderId}/{score.beatmap_id}/{score.ruleset_id}/first-place-lost", "POST", new
                    {
                        legacy_score_event = false,
                    });
                }
            }
        }

        public void ApplyGlobal(SoloScore score, MySqlConnection conn)
        {
        }
    }
}
