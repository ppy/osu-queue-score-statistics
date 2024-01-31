using Dapper;
using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Updates the performance for a legacy score.
    /// </summary>
    public class LegacyScorePerformanceProcessor : IProcessor
    {
        // This processor needs to run after the score's PP value has been processed.
        public const int ORDER = ScorePerformanceProcessor.ORDER + 1;

        public int Order => ORDER;

        public bool RunOnFailedScores => false;

        public bool RunOnLegacyScores => true;

        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            // Processor should only be ran on legacy high scores.
            // `score.LegacyScoreId == 0` check is required as the ID will be 0 for non-`high` scores.
            if (!score.IsLegacyScore || score.LegacyScoreId == 0)
                return;

            string highScoresTable = LegacyDatabaseHelper.GetRulesetSpecifics(score.RulesetID).HighScoreTable;
            conn.Execute($"UPDATE {highScoresTable} SET pp = @Pp WHERE score_id = @LegacyScoreId", new
            {
                Pp = score.PP,
                LegacyScoreId = score.LegacyScoreId,
            }, transaction: transaction);
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }
    }
}