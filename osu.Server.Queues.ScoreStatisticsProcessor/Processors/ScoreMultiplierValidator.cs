// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    [UsedImplicitly]
    public class ScoreMultiplierValidator : IProcessor
    {
        public bool RunOnFailedScores => true;
        public bool RunOnLegacyScores => true;

        // Must run before any processor that reads total score.
        public int Order => int.MinValue;

        public void RevertFromUserStats(SoloScore score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction, List<Action> postTransactionActions)
        {
        }

        public void ApplyToUserStats(SoloScore score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction, List<Action> postTransactionActions)
        {
            if (score.ScoreData.TotalScoreWithoutMods is not long totalScoreWithoutMods)
            {
                // TODO: probably add some visibility measure to track how often this is happening
                throw new ProcessingAbortedException("Score is missing total score without mods.");
            }

            Ruleset ruleset = ScoreStatisticsQueueProcessor.AVAILABLE_RULESETS.Single(r => r.RulesetInfo.OnlineID == score.ruleset_id);
            var scoreMultiplierCalculator = ruleset.CreateScoreMultiplierCalculator(new ScoreMultiplierContext(score.beatmap!.GetLegacyBeatmapConversionDifficultyInfo()));
            uint expectedTotalScore = (uint)Math.Round(totalScoreWithoutMods * scoreMultiplierCalculator.CalculateFor(score.ScoreData.Mods.Select(m => m.ToMod(ruleset))));

            if (expectedTotalScore != score.total_score)
            {
                // TODO: probably add some visibility measure to track how often this is happening
                score.total_score = expectedTotalScore;
                conn.Execute(@"UPDATE `scores` SET `total_score` = @expectedTotal WHERE `id` = @id", new
                {
                    expectedTotal = expectedTotalScore,
                    id = score.id,
                }, transaction);
            }
        }

        public void ApplyGlobal(SoloScore score, MySqlConnection conn)
        {
        }
    }
}
