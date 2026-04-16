// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors.MedalAwarders
{
    [UsedImplicitly]
    public class LazerModIntroductionMedalAwarder : IMedalAwarder
    {
        public bool RunOnFailedScores => false;
        public bool RunOnLegacyScores => false;

        public IEnumerable<Medal> Check(IEnumerable<Medal> medals, MedalAwarderContext context)
        {
            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(context.Score.ruleset_id);
            Mod[] mods = context.Score.ScoreData.Mods.Select(m => m.ToMod(ruleset)).ToArray();

            foreach (var medal in medals)
            {
                switch (medal.achievement_id)
                {
                    // Gear Shift
                    case 339:
                    {
                        if (mods.Any(m => m.Type == ModType.Conversion))
                            yield return medal;

                        break;
                    }

                    // Game Night
                    case 340:
                    {
                        if (mods.Any(m => m.Type == ModType.Fun))
                            yield return medal;

                        break;
                    }
                }
            }
        }
    }
}
