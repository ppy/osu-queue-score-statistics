// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using JetBrains.Annotations;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors.MedalAwarders
{
    [UsedImplicitly]
    public class DailyChallengeMedalAwarder : IMedalAwarder
    {
        public bool RunOnFailedScores => false;
        public bool RunOnLegacyScores => false;

        public IEnumerable<Medal> Check(IEnumerable<Medal> medals, MedalAwarderContext context)
        {
            foreach (var medal in medals)
            {
                switch (medal.achievement_id)
                {
                    case 336:
                    {
                        if (context.DailyChallengeUserStats.daily_streak_best >= 1)
                            yield return medal;

                        break;
                    }

                    case 337:
                    {
                        if (context.DailyChallengeUserStats.daily_streak_best >= 7)
                            yield return medal;

                        break;
                    }

                    case 338:
                    {
                        if (context.DailyChallengeUserStats.daily_streak_best >= 30)
                            yield return medal;

                        break;
                    }
                }
            }
        }
    }
}
