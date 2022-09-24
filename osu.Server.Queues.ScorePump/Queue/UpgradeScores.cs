// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Dapper.Contrib.Extensions;
using McMaster.Extensions.CommandLineUtils;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScorePump.Queue
{
    [Command("upgrade-scores", Description = "Upgrades scores from the solo scores table, ensuring total score and accuracy values are up-to-date.")]
    public class UpgradeScores : QueueCommand
    {
        private const int batch_size = 10000;

        /// <summary>
        /// The score ID to start the process from. This can be used to resume an existing job.
        /// </summary>
        [Option(CommandOptionType.SingleValue)]
        public ulong StartId { get; set; }

        /// <summary>
        /// The amount of time to sleep between score batches.
        /// </summary>
        [Option(CommandOptionType.SingleValue)]
        public int Delay { get; set; }

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"Processing next {batch_size} scores starting from {StartId}");

                using (var db = Queue.GetDatabaseConnection())
                {
                    int updateCount = 0;

                    using (var transaction = await db.BeginTransactionAsync(cancellationToken))
                    {
                        SoloScore[] scores = (await db.QueryAsync<SoloScore>($"SELECT * FROM {SoloScore.TABLE_NAME} WHERE `id` >= @StartId LIMIT {batch_size}", new
                        {
                            StartId = StartId
                        }, transaction)).ToArray();

                        if (scores.Length == 0)
                            break;

                        foreach (var score in scores)
                        {
                            bool requiresUpdate = ensureMaximumStatistics(score);
                            requiresUpdate |= ensureCorrectTotalScore(score);

                            if (requiresUpdate)
                            {
                                await db.UpdateAsync(score, transaction);
                                updateCount++;
                            }
                        }

                        await transaction.CommitAsync(cancellationToken);

                        StartId = scores.Max(s => s.id) + 1;
                    }

                    Console.WriteLine($"Updated {updateCount} rows");
                }

                if (Delay > 0)
                {
                    Console.WriteLine($"Waiting {Delay}ms...");
                    await Task.Delay(Delay, cancellationToken);
                }
            }

            Console.WriteLine("Finished.");
            return 0;
        }

        private bool ensureMaximumStatistics(SoloScore score)
        {
            if (score.ScoreInfo.MaximumStatistics.Sum(s => s.Value) > 0)
                return false;

            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(score.ruleset_id);
            HitResult maxBasicResult = ruleset.GetHitResults().Select(h => h.result).Where(h => h.IsBasic()).MaxBy(Judgement.ToNumericResult);

            foreach ((HitResult result, int count) in score.ScoreInfo.Statistics)
            {
                switch (result)
                {
                    case HitResult.LargeTickHit:
                    case HitResult.LargeTickMiss:
                        score.ScoreInfo.MaximumStatistics[HitResult.LargeTickHit] = score.ScoreInfo.MaximumStatistics.GetValueOrDefault(HitResult.LargeTickHit) + count;
                        break;

                    case HitResult.SmallTickHit:
                    case HitResult.SmallTickMiss:
                        score.ScoreInfo.MaximumStatistics[HitResult.SmallTickHit] = score.ScoreInfo.MaximumStatistics.GetValueOrDefault(HitResult.SmallTickHit) + count;
                        break;

                    case HitResult.IgnoreHit:
                    case HitResult.IgnoreMiss:
                    case HitResult.SmallBonus:
                    case HitResult.LargeBonus:
                        break;

                    default:
                        score.ScoreInfo.MaximumStatistics[maxBasicResult] = score.ScoreInfo.MaximumStatistics.GetValueOrDefault(maxBasicResult) + count;
                        break;
                }
            }

            return true;
        }

        private bool ensureCorrectTotalScore(SoloScore score)
        {
            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(score.ruleset_id);
            ScoreInfo scoreInfo = score.ScoreInfo.ToScoreInfo(score.ScoreInfo.Mods.Select(m => m.ToMod(ruleset)).ToArray());
            scoreInfo.Ruleset = ruleset.RulesetInfo;

            ScoreProcessor scoreProcessor = ruleset.CreateScoreProcessor();
            scoreProcessor.Mods.Value = scoreInfo.Mods;

            int totalScore = (int)Math.Round(scoreProcessor.ComputeScore(ScoringMode.Standardised, scoreInfo));
            double accuracy = scoreProcessor.ComputeAccuracy(scoreInfo);

            if (totalScore == score.ScoreInfo.TotalScore && Math.Round(accuracy, 2) == Math.Round(score.ScoreInfo.Accuracy, 2))
                return false;

            score.ScoreInfo.TotalScore = totalScore;
            score.ScoreInfo.Accuracy = accuracy;

            return true;
        }
    }
}
