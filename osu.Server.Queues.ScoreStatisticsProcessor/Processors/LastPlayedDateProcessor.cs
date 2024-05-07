// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using JetBrains.Annotations;
using MySqlConnector;
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

        public void RevertFromUserStats(SoloScore score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
        }

        public void ApplyToUserStats(SoloScore score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (userStats.last_played < score.ended_at || userStats.playcount == 0)
                userStats.last_played = score.ended_at;
        }

        public void ApplyGlobal(SoloScore score, MySqlConnection conn)
        {
        }
    }
}
