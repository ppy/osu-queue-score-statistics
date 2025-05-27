// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Sentry;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Updates the rank achieved tallies for users.
    /// </summary>
    [UsedImplicitly]
    public class UserRankCountProcessor : IProcessor
    {
        public bool RunOnFailedScores => false;

        public bool RunOnLegacyScores => true;

        public void RevertFromUserStats(SoloScore score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (!score.BeatmapValidForRankedCounts())
                return;

            if (previousVersion >= 7)
            {
                // It is assumed that in the case of a revert, either the score is deleted, or a reapplication will immediately follow.

                // First, see if the score we're reverting is the user's best (and as such included in the rank counts).
                var bestScore = DatabaseHelper.GetUserBestScoreFor(score, conn, transaction);

                // If this score isn't the user's best on the beatmap, nothing needs to be reverted.
                if (bestScore?.id != score.id)
                    return;

                // If it is, remove the rank before applying the next-best.
                removeRank(userStats, score.rank);

                var secondBestScore = DatabaseHelper.GetUserBestScoreFor(score, conn, transaction, offset: 1);
                if (secondBestScore != null)
                    addRank(userStats, secondBestScore.rank);
            }
        }

        public void ApplyToUserStats(SoloScore score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
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
            {
                if (secondBestScore.ended_at < score.beatmap?.beatmapset?.approved_date)
                {
                    SentrySdk.CaptureMessage("Suspicious rank count operation - decrementing user rank count from a score set before beatmap was ranked "
                                             + $"(userId:{score.user_id} newScoreId:{score.id} oldScoreId:{secondBestScore.id})", SentryLevel.Warning);
                }

                removeRank(userStats, secondBestScore.rank);
            }

            Debug.Assert(bestScore != null);
            addRank(userStats, bestScore.rank);
        }

        public void ApplyGlobal(SoloScore score, MySqlConnection conn)
        {
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
