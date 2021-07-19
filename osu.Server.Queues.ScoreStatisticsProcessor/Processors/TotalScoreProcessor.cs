// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using JetBrains.Annotations;
using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Increment total score for the user.
    /// </summary>
    [UsedImplicitly]
    public class TotalScoreProcessor : IProcessor
    {
        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (previousVersion >= 2)
                userStats.total_score -= score.total_score;
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            userStats.total_score += score.total_score;
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }
    }
}
