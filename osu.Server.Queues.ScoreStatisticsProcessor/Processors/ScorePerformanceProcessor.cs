// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Dapper;
using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    public class ScorePerformanceProcessor : IProcessor
    {
        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            // .Wait() in this class should be safe, since the queue processor runs under its own (non-TPL) task scheduler.
            var dbInfo = LegacyDatabaseHelper.GetRulesetSpecifics(score.ruleset_id);
            var processor = PerformanceProcessor.CreateAsync(conn, transaction).Result;

            processor.ProcessScoreAsync(score, conn, transaction).Wait();
            processor.UpdateUserStatsAsync(userStats, score.ruleset_id, conn, transaction).Wait();

            int warnings = conn.QuerySingleOrDefault<int>($"SELECT `user_warnings` FROM {dbInfo.UsersTable} WHERE `user_id` = @UserId", new
            {
                UserId = userStats.user_id
            }, transaction);

            if (warnings > 0)
                userStats.rank_score = 0;
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }
    }
}
