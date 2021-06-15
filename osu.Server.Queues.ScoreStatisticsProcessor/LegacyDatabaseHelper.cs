// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    public static class LegacyDatabaseHelper
    {
        public static RulesetDatabaseInfo GetRulesetSpecifics(int rulesetId)
        {
            switch (rulesetId)
            {
                default:
                case 0:
                    return new RulesetDatabaseInfo(0, "osu", false);

                case 1:
                    return new RulesetDatabaseInfo(1, "taiko", true);

                case 2:
                    return new RulesetDatabaseInfo(2, "fruits", true);

                case 3:
                    return new RulesetDatabaseInfo(3, "mania", true);
            }
        }

        public class RulesetDatabaseInfo
        {
            public readonly string ScoreTable;
            public readonly string HighScoreTable;
            public readonly string LeadersTable;
            public readonly string UserStatsTable;
            public readonly string ReplayTable;

            public RulesetDatabaseInfo(int rulesetId, string rulesetIdentifier, bool legacySuffix)
            {
                string tableSuffix = legacySuffix ? $"_{rulesetIdentifier}" : string.Empty;

                ScoreTable = $"`osu`.`osu_scores{tableSuffix}`";
                HighScoreTable = $"`osu`.`{ScoreTable}_high`";
                LeadersTable = $"`osu`.`osu_leaders{tableSuffix}`";
                UserStatsTable = $"`osu`.`osu_user_stats{tableSuffix}`";
                ReplayTable = $"`osu`.`osu_replays{tableSuffix}`";
            }
        }
    }
}
