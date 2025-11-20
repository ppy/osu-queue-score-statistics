// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Rulesets.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Increment hit statistics (300/100/50/miss) for the user.
    /// </summary>
    [UsedImplicitly]
    public class HitStatisticsProcessor : IProcessor
    {
        public bool RunOnFailedScores => true;

        public bool RunOnLegacyScores => false;

        public void RevertFromUserStats(SoloScore score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction, List<Action> postTransactionActions)
        {
            if (previousVersion >= 2)
                adjustStatisticsFromScore(score, userStats, true);
        }

        public void ApplyToUserStats(SoloScore score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction, List<Action> postTransactionActions)
        {
            adjustStatisticsFromScore(score, userStats);
        }

        public void ApplyGlobal(SoloScore score, MySqlConnection conn)
        {
        }

        private static void adjustStatisticsFromScore(SoloScore score, UserStats userStats, bool revert = false)
        {
            int multiplier = revert ? -1 : 1;

            foreach ((var result, int count) in score.ScoreData.Statistics)
            {
                if (count < 0)
                    return;

                switch (result)
                {
                    case HitResult.Miss:
                    case HitResult.LargeTickMiss when score.ruleset_id == 2:
                        userStats.countMiss += multiplier * count;
                        break;

                    case HitResult.Meh:
                    case HitResult.SmallTickHit when score.ruleset_id == 2:
                        userStats.count50 += multiplier * count;
                        break;

                    case HitResult.Ok:
                    case HitResult.Good:
                    case HitResult.LargeTickHit when score.ruleset_id == 2:
                        userStats.count100 += multiplier * count;
                        break;

                    case HitResult.Great:
                    case HitResult.Perfect:
                        userStats.count300 += multiplier * count;
                        break;
                }
            }
        }
    }
}
