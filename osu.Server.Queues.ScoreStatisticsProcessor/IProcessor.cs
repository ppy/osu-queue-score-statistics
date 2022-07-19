// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    public interface IProcessor
    {
        /// <summary>
        /// The order in which this <see cref="IProcessor"/> will be run.
        /// Defaults to 0.
        /// </summary>
        /// <remarks>
        /// Higher values indicate this <see cref="IProcessor"/> runs after other <see cref="IProcessor"/>s with a smaller <see cref="Order"/> value.
        /// </remarks>
        int Order => 0;

        void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction);

        void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction);

        /// <summary>
        /// Adjust any global statistics outside of the user transaction.
        /// Anything done in here should not need to be rolled back.
        /// </summary>
        /// <param name="score">The user's score.</param>
        /// <param name="conn">The database connection.</param>
        void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn);
    }
}
