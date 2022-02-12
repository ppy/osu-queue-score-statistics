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
                        AimDifficulty = databasedAttribs[1].value,
                        SpeedDifficulty = databasedAttribs[3].value,
                        OverallDifficulty = databasedAttribs[5].value,
                        ApproachRate = databasedAttribs[7].value,
                        MaxCombo = (int)databasedAttribs[9].value,
                        StarRating = databasedAttribs[11].value,
                        FlashlightDifficulty = databasedAttribs.GetValueOrDefault(17)?.value ?? 0,
                        HitCircleCount = databasedBeatmap.countNormal,
                        SpinnerCount = databasedBeatmap.countSpinner,
                    };

                case 1:
                    return new TaikoDifficultyAttributes
                    {
                        MaxCombo = (int)databasedAttribs[9].value,
                        StarRating = databasedAttribs[11].value,
                        GreatHitWindow = databasedAttribs[13].value
                    };

                case 2:
                    return new CatchDifficultyAttributes
                    {
                        StarRating = databasedAttribs[1].value,
                        ApproachRate = databasedAttribs[7].value,
                        MaxCombo = (int)databasedAttribs[9].value
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
    }
}
