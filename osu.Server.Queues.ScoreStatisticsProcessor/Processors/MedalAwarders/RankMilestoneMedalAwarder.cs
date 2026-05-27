// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using JetBrains.Annotations;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors.MedalAwarders
{
    [UsedImplicitly]
    public class RankMilestoneMedalAwarder : IMedalAwarder
    {
        public bool RunOnFailedScores => false;

        public bool RunOnLegacyScores => false; // Legacy scores are handled by web-10.

        public IEnumerable<Medal> Check(IEnumerable<Medal> medals, MedalAwarderContext context)
        {
            foreach (var medal in medals)
            {
                if (checkMedal(medal, context))
                    yield return medal;
            }
        }

        private bool checkMedal(Medal medal, MedalAwarderContext context)
        {
            if (context.UserStats.rank_score_index <= 0)
                return false;

            switch (medal.achievement_id)
            {
                case 50:
                    return context.UserStats.rank_score_index <= 50000;

                case 51:
                    return context.UserStats.rank_score_index <= 10000;

                case 52:
                    return context.UserStats.rank_score_index <= 5000;

                case 53:
                    return context.UserStats.rank_score_index <= 1000;

                default:
                    return false;
            }
        }
    }
}
