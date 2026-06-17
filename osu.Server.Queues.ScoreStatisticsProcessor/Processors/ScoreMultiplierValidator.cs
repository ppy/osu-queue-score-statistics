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
using StatsdClient;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    [UsedImplicitly]
    public class ScoreMultiplierValidator : IProcessor
    {
        private const string scores_modified_metric = $@"{nameof(ScoreMultiplierValidator)}.scores_modified";

        public bool RunOnFailedScores => true;
        public bool RunOnLegacyScores => true;
        public bool RunOnVeryShortPlays => true;

        // Must run before any processor that reads total score.
        public int Order => int.MinValue;

        public void RevertFromUserStats(SoloScore score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction, List<Action> postTransactionActions,
                                        DogStatsdService dogStatsd)
        {
        }

        public void ApplyToUserStats(SoloScore score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction, List<Action> postTransactionActions, DogStatsdService dogStatsd)
        {
            if (score.ScoreData.TotalScoreWithoutMods is not long totalScoreWithoutMods)
            {
                dogStatsd.Increment(scores_modified_metric, tags: ["unranked"]);
                throw new ProcessingAbortedException("Score is missing total score without mods.");
            }

            Ruleset ruleset = ScoreStatisticsQueueProcessor.AVAILABLE_RULESETS.Single(r => r.RulesetInfo.OnlineID == score.ruleset_id);
            var scoreMultiplierCalculator = ruleset.CreateScoreMultiplierCalculator(new ScoreMultiplierContext(score.beatmap!.GetLegacyBeatmapConversionDifficultyInfo()));
            uint expectedTotalScore = (uint)Math.Round(totalScoreWithoutMods * scoreMultiplierCalculator.CalculateFor(score.ScoreData.Mods.Select(m => m.ToMod(ruleset))));

            if (expectedTotalScore != score.total_score)
            {
                score.total_score = expectedTotalScore;
                conn.Execute(@"UPDATE `scores` SET `total_score` = @expectedTotal WHERE `id` = @id", new
                {
                    expectedTotal = expectedTotalScore,
                    id = score.id,
                }, transaction);
                dogStatsd.Increment(scores_modified_metric, tags: ["total_score_corrected"]);
            }
        }

        public void ApplyGlobal(SoloScore score, MySqlConnection conn)
        {
        }
    }
}
