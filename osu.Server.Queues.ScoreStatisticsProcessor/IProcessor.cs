// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    public interface IProcessor
    {
        void RevertFromUserStats(SoloScore score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction);

        void ApplyToUserStats(SoloScore score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction);

        /// <summary>
        /// Adjust any global statistics outside of the user transaction.
        /// Anything done in here should not need to be rolled back.
        /// </summary>
        /// <param name="score">The user's score.</param>
        /// <param name="conn">The database connection.</param>
        void ApplyGlobal(SoloScore score, MySqlConnection conn);
    }
}
