// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Catch.Difficulty;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Difficulty;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Difficulty;
using osu.Game.Rulesets.Taiko;
using osu.Game.Rulesets.Taiko.Difficulty;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    public static class LegacyRulesetHelper
    {
        public static Ruleset GetRulesetFromLegacyId(int legacyId)
        {
            switch (legacyId)
            {
                case 0:
                    return new OsuRuleset();

                case 1:
                    return new TaikoRuleset();

                case 2:
                    return new CatchRuleset();

                case 3:
                    return new ManiaRuleset();

                default:
                    throw new ArgumentException($"Invalid ruleset ID: {legacyId}", nameof(legacyId));
            }
        }

        public static DifficultyAttributes CreateDifficultyAttributes(int legacyId)
        {
            switch (legacyId)
            {
                case 0:
                    return new OsuDifficultyAttributes();

                case 1:
                    return new TaikoDifficultyAttributes();

                case 2:
                    return new CatchDifficultyAttributes();

                case 3:
                    return new ManiaDifficultyAttributes();

                default:
                    throw new ArgumentException($"Invalid ruleset ID: {legacyId}", nameof(legacyId));
            }
        }
    }
}
