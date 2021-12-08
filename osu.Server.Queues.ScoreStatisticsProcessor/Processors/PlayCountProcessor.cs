// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Dapper;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Increment play counts for user and globals.
    /// </summary>
    [UsedImplicitly]
    public class PlayCountProcessor : IProcessor
    {
        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (previousVersion >= 1)
                userStats.playcount--;

            if (previousVersion >= 3)
            {
                adjustMonthlyPlaycount(score, conn, transaction, true);
                adjustUserBeatmapPlaycount(score, conn, transaction, true);
            }
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            userStats.playcount++;
            adjustMonthlyPlaycount(score, conn, transaction);
            adjustUserBeatmapPlaycount(score, conn, transaction);
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }

        private static void adjustUserBeatmapPlaycount(SoloScoreInfo score, MySqlConnection conn, MySqlTransaction transaction, bool revert = false)
        {
            conn.Execute("INSERT INTO osu_user_beatmap_playcount (beatmap_id, user_id, playcount) VALUES (@beatmap_id, @user_id, @add) ON DUPLICATE KEY UPDATE playcount = GREATEST(0, playcount + @adjust)", new
            {
                score.beatmap_id,
                score.user_id,
                add = revert ? 0 : 1,
                adjust = revert ? -1 : 1,
            }, transaction);
        }

        private static void adjustMonthlyPlaycount(SoloScoreInfo score, MySqlConnection conn, MySqlTransaction transaction, bool revert = false)
        {
            conn.Execute("INSERT INTO osu_user_month_playcount (`year_month`, user_id, playcount) VALUES (@yearmonth, @user_id, @add) ON DUPLICATE KEY UPDATE playcount = GREATEST(0, playcount + @adjust)", new
            {
                yearmonth = score.started_at!.Value.ToString("yyMM"),
                score.user_id,
                add = revert ? 0 : 1,
                adjust = revert ? -1 : 1,
            }, transaction);
        }
    }
}
