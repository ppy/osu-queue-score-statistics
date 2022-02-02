// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using Dapper;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Increment total user play time.
    /// </summary>
    [UsedImplicitly]
    public class PlayTimeProcessor : IProcessor
    {
        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (previousVersion >= 6)
                userStats.total_seconds_played -= getPlayLength(score, conn, transaction);
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (score.ended_at == null)
                throw new InvalidOperationException("Attempting to increment play time when score was never finished.");

            userStats.total_seconds_played += getPlayLength(score, conn, transaction);
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }

        private static int getPlayLength(SoloScoreInfo score, MySqlConnection conn, MySqlTransaction transaction)
        {
            Debug.Assert(score.started_at != null);
            Debug.Assert(score.ended_at != null);

            // to ensure sanity, first get the maximum time feasible from the beatmap's length
            double totalLengthSeconds = conn.QueryFirstOrDefault<double>("SELECT total_length FROM osu_beatmaps WHERE beatmap_id = @beatmap_id", score, transaction);

            foreach (var mod in score.mods)
            {
                if (mod.Settings.TryGetValue(@"speed_change", out var rate))
                    totalLengthSeconds /= (double)rate;
            }

            TimeSpan realTimePassed = score.ended_at.Value - score.started_at.Value;

            // TODO: better handle failed plays once we have incoming data.

            return (int)Math.Min(totalLengthSeconds, realTimePassed.TotalSeconds);
        }
    }
}
