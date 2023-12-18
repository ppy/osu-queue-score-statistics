// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using Dapper;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Framework.Development;
using osu.Framework.Utils;
using osu.Game.Online.API.Requests.Responses;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Increment play counts for user and globals.
    /// </summary>
    [UsedImplicitly]
    public class PlayCountProcessor : IProcessor
    {
        public bool RunOnFailedScores => true;

        public bool RunOnLegacyScores => false;

        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (previousVersion >= 1)
                userStats.playcount--;

            if (previousVersion >= 3)
            {
                adjustUserMonthlyPlaycount(score, conn, transaction, true);
                adjustUserBeatmapPlaycount(score, conn, transaction, true);
            }
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            const int beatmap_count = 12;
            const int over_time = 120;

            int secondsForRecentScores = conn.QuerySingleOrDefault<int?>("SELECT UNIX_TIMESTAMP(NOW()) - UNIX_TIMESTAMP(created_at) FROM solo_scores WHERE user_id = @user_id AND ruleset_id = @ruleset_id ORDER BY id DESC LIMIT 1 OFFSET @beatmap_count", new
            {
                user_id = score.UserID,
                ruleset_id = score.RulesetID,
                beatmap_count,
            }, transaction) ?? int.MaxValue;

            if (secondsForRecentScores > over_time)
            {
                userStats.playcount++;

                adjustUserMonthlyPlaycount(score, conn, transaction);
                adjustUserBeatmapPlaycount(score, conn, transaction);

                adjustGlobalPlaycount(conn, transaction);
                adjustGlobalBeatmapPlaycount(score, conn, transaction);
            }
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }

        private static void adjustGlobalPlaycount(MySqlConnection conn, MySqlTransaction transaction)
        {
            // We want to reduce database overhead here, so we only update the global playcount every n plays.
            // Note that we use a non-round number to make the display more natural.
            int increment = DebugUtils.IsNUnitRunning ? 5 : 99;

            if (RNG.Next(0, increment) == 0)
            {
                conn.Execute("UPDATE osu_counts SET count = count + @increment WHERE name = 'playcount'", new
                {
                    increment
                }, transaction);
            }
        }

        private static void adjustGlobalBeatmapPlaycount(SoloScoreInfo score, MySqlConnection conn, MySqlTransaction transaction)
        {
            // We want to reduce database overhead here, so we only update the global beatmap playcount every n plays.
            // Note that we use a non-round number to make the display more natural.
            int increment = score.Beatmap!.PlayCount < 1000 ? 1 : 9;

            if (RNG.Next(0, increment) == 0)
            {
                conn.Execute("UPDATE osu_beatmaps SET playcount = playcount + @increment WHERE beatmap_id = @beatmapId; UPDATE osu_beatmapsets SET play_count = play_count + @increment WHERE beatmapset_id = @beatmapSetId", new
                {
                    increment,
                    beatmapId = score.BeatmapID,
                    beatmapSetId = score.Beatmap.OnlineBeatmapSetID
                }, transaction);

                // Reindex beatmap occasionally.
                if (RNG.Next(0, 10) == 0)
                    LegacyDatabaseHelper.RunLegacyIO("indexing/bulk", "POST", $"beatmapset[]={score.Beatmap.OnlineBeatmapSetID}");

                // TODO: announce playcount milestones
                // const int notify_amount = 1000000;
                // if (score.Beatmap.PlayCount > 0 && Math.Abs((incrementedPlaycount % notify_amount) - (score.Beatmap.PlayCount % notify_amount)) > increment)
                //     throw new NotImplementedException("addEvent");
            }
        }

        private static void adjustUserBeatmapPlaycount(SoloScoreInfo score, MySqlConnection conn, MySqlTransaction transaction, bool revert = false)
        {
            conn.Execute(
                "INSERT INTO osu_user_beatmap_playcount (beatmap_id, user_id, playcount) VALUES (@beatmap_id, @user_id, @add) ON DUPLICATE KEY UPDATE playcount = GREATEST(0, playcount + @adjust)", new
                {
                    beatmap_id = score.BeatmapID,
                    user_id = score.UserID,
                    add = revert ? 0 : 1,
                    adjust = revert ? -1 : 1,
                }, transaction);
        }

        private static void adjustUserMonthlyPlaycount(SoloScoreInfo score, MySqlConnection conn, MySqlTransaction transaction, bool revert = false)
        {
            DateTimeOffset? date = score.StartedAt ?? score.EndedAt;

            Debug.Assert(date != null);

            conn.Execute(
                "INSERT INTO osu_user_month_playcount (`year_month`, user_id, playcount) VALUES (@yearmonth, @user_id, @add) ON DUPLICATE KEY UPDATE playcount = GREATEST(0, playcount + @adjust)", new
                {
                    yearmonth = date.Value.ToString("yyMM"),
                    user_id = score.UserID,
                    add = revert ? 0 : 1,
                    adjust = revert ? -1 : 1,
                }, transaction);
        }
    }
}
