// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Game.Rulesets.Catch.Difficulty;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mania.Difficulty;
using osu.Game.Rulesets.Osu.Difficulty;
using osu.Game.Rulesets.Taiko.Difficulty;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    // Todo: Merge with osu-difficulty-calculator.
    public static class DifficultyAttributeExtensions
    {
        public static DifficultyAttributes Map(this Dictionary<int, BeatmapDifficultyAttribute> databasedAttribs, int rulesetId, Beatmap databasedBeatmap)
        {
            switch (rulesetId)
            {
                case 0:
                    return new OsuDifficultyAttributes
                    {
                        AimStrain = databasedAttribs[1].value,
                        SpeedStrain = databasedAttribs[3].value,
                        OverallDifficulty = databasedAttribs[5].value,
                        ApproachRate = databasedAttribs[7].value,
                        MaxCombo = Convert.ToInt32(databasedAttribs[9].value),
                        StarRating = databasedAttribs[11].value,
                        HitCircleCount = databasedBeatmap.countNormal,
                        SpinnerCount = databasedBeatmap.countSpinner,
                    };

                case 1:
                    return new TaikoDifficultyAttributes
                    {
                        MaxCombo = Convert.ToInt32(databasedAttribs[9].value),
                        StarRating = databasedAttribs[11].value,
                        GreatHitWindow = databasedAttribs[13].value
                    };

                case 2:
                    return new CatchDifficultyAttributes
                    {
                        StarRating = databasedAttribs[1].value,
                        ApproachRate = databasedAttribs[7].value,
                        MaxCombo = Convert.ToInt32(databasedAttribs[9].value)
                    };

                case 3:
                    return new ManiaDifficultyAttributes
                    {
                        StarRating = databasedAttribs[11].value,
                        GreatHitWindow = databasedAttribs[13].value,
                        ScoreMultiplier = databasedAttribs[15].value
                    };

                default:
                    throw new ArgumentException($"Invalid ruleset ({rulesetId}).", nameof(rulesetId));
            }
        }

        public static IEnumerable<(int id, object value)> Map(this DifficultyAttributes attributes)
        {
            switch (attributes)
            {
                case OsuDifficultyAttributes osu:
                    yield return (1, osu.AimStrain);
                    yield return (3, osu.SpeedStrain);
                    yield return (5, osu.OverallDifficulty);
                    yield return (7, osu.ApproachRate);
                    yield return (9, osu.MaxCombo);
                    yield return (11, attributes.StarRating);

                    break;

                case TaikoDifficultyAttributes taiko:
                    yield return (9, taiko.MaxCombo);
                    yield return (11, attributes.StarRating);
                    yield return (13, taiko.GreatHitWindow);

                    break;

                case CatchDifficultyAttributes @catch:
                    // Todo: Catch should not output star rating in the 'aim' attribute.
                    yield return (1, @catch.StarRating);
                    yield return (7, @catch.ApproachRate);
                    yield return (9, @catch.MaxCombo);

                    break;

                case ManiaDifficultyAttributes mania:
                    // Todo: Mania doesn't output MaxCombo attribute for some reason.
                    yield return (11, attributes.StarRating);
                    yield return (13, mania.GreatHitWindow);
                    yield return (15, mania.ScoreMultiplier);

                    break;
            }
        }
    }
}
