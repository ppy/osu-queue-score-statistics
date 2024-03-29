// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    [UsedImplicitly]
    public class LastPlayedDateProcessor : IProcessor
    {
        public const int ORDER = 0;

        public int Order => ORDER;

        public bool RunOnFailedScores => true;
        public bool RunOnLegacyScores => false;

        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (userStats.last_played < score.EndedAt || userStats.playcount == 0)
                userStats.last_played = score.EndedAt;
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }
    }
}
