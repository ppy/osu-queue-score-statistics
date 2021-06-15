// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Adjust max combo (if required) for the user.
    /// </summary>
    [UsedImplicitly]
    public class MaxComboProcessor : IProcessor
    {
        public void RevertFromUserStats(SoloScore score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
            // TODO: this will require access to stable scores to be implemented correctly.
        }

        public void ApplyToUserStats(SoloScore score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            // TODO: assert the user's score is not higher than the max combo for the beatmap.
            userStats.max_combo = (short)Math.Max(userStats.max_combo, score.max_combo);
        }

        public void ApplyGlobal(SoloScore score, MySqlConnection conn)
        {
        }
    }
}
