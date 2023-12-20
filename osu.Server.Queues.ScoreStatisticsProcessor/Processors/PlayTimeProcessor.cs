// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Increment total user play time.
    /// </summary>
    [UsedImplicitly]
    public class PlayTimeProcessor : IProcessor
    {
        public bool RunOnFailedScores => true;

        public bool RunOnLegacyScores => false;

        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (previousVersion >= 6)
                userStats.total_seconds_played -= PlayValidityHelper.GetPlayLength(score);
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            userStats.total_seconds_played += PlayValidityHelper.GetPlayLength(score);
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }
    }
}
