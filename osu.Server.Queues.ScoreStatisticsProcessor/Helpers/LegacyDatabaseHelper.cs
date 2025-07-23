// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public static class LegacyDatabaseHelper
    {
        private static readonly LegacyDatabaseHelper.RulesetDatabaseInfo osu_info = new LegacyDatabaseHelper.RulesetDatabaseInfo(0, "osu", false);
        private static readonly LegacyDatabaseHelper.RulesetDatabaseInfo taiko_info = new LegacyDatabaseHelper.RulesetDatabaseInfo(1, "taiko", true);
        private static readonly LegacyDatabaseHelper.RulesetDatabaseInfo fruits_info = new LegacyDatabaseHelper.RulesetDatabaseInfo(2, "fruits", true);
        private static readonly LegacyDatabaseHelper.RulesetDatabaseInfo mania_info = new LegacyDatabaseHelper.RulesetDatabaseInfo(3, "mania", true);

        public static LegacyDatabaseHelper.RulesetDatabaseInfo GetRulesetSpecifics(int rulesetId)
        {
            switch (rulesetId)
            {
                default:
                case 0:
                    return osu_info;

                case 1:
                    return taiko_info;

                case 2:
                    return fruits_info;

                case 3:
                    return mania_info;
            }
        }

        public class RulesetDatabaseInfo
        {
            public readonly int RulesetId;
            public readonly string UsersTable;
            public readonly string ScoreTable;
            public readonly string HighScoreTable;
            public readonly string LeadersTable;
            public readonly string UserStatsTable;
            public readonly string ReplayTable;
            public readonly string LastProcessedPpUserCount;
            public readonly string LastProcessedPpScoreCount;
            public readonly string TodaysRankColumn;

            public RulesetDatabaseInfo(int rulesetId, string rulesetIdentifier, bool legacySuffix)
            {
                RulesetId = rulesetId;

                string tableSuffix = legacySuffix ? $"_{rulesetIdentifier}" : string.Empty;

                // If using the dumps, set this environment variable to "sample_users".
                string usersTable = Environment.GetEnvironmentVariable("DB_USERS_TABLE") ?? "phpbb_users";
                string dbName = Environment.GetEnvironmentVariable("DB_NAME") ?? "osu";

                UsersTable = $"`{dbName}`.`{usersTable}`";
                ScoreTable = $"`{dbName}`.`osu_scores{tableSuffix}`";
                HighScoreTable = $"`{dbName}`.`osu_scores{tableSuffix}_high`";
                LeadersTable = $"`{dbName}`.`osu_leaders{tableSuffix}`";
                UserStatsTable = $"`{dbName}`.`osu_user_stats{tableSuffix}`";
                ReplayTable = $"`{dbName}`.`osu_replays{tableSuffix}`";
                LastProcessedPpUserCount = $"pp_last_user_id{tableSuffix}";
                LastProcessedPpScoreCount = $"pp_last_score_id{tableSuffix}";
                TodaysRankColumn = $"pp_rank_column_{rulesetIdentifier}";
            }
        }
    }
}
