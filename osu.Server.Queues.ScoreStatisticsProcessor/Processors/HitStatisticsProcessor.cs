// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
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
        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (previousVersion >= 2)
                adjustStatisticsFromScore(score, userStats, true);
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            adjustStatisticsFromScore(score, userStats);
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }

        private static void adjustStatisticsFromScore(SoloScoreInfo score, UserStats userStats, bool revert = false)
        {
            int multiplier = revert ? -1 : 1;

            foreach (var (result, count) in score.Statistics)
            {
                switch (result)
                {
                    case HitResult.Miss:
                        userStats.countMiss += multiplier * count;
                        break;

                    case HitResult.Meh:
                        userStats.count50 += multiplier * count;
                        break;

                    case HitResult.Ok:
                    case HitResult.Good:
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
