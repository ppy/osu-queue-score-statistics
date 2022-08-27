// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Dapper.Contrib.Extensions;
using McMaster.Extensions.CommandLineUtils;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScorePump
{
    [Command("add-maximum-statistics-to-lazer-scores", Description = "Updates the stored solo score data.")]
    public class AddMaximumStatisticsToLazerScores : ScorePump
    {
        private const int batch_size = 10000;

        /// <summary>
        /// The score ID to start the process from. This can be used to resume an existing job.
        /// </summary>
        [Option(CommandOptionType.SingleValue)]
        public long StartId { get; set; }

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
                    using (var transaction = await db.BeginTransactionAsync(cancellationToken))
                    {
                        SoloScore[] scores = (await db.QueryAsync<SoloScore>($"SELECT * FROM {SoloScore.TABLE_NAME} WHERE `id` >= @StartId", new
                        {
                            StartId = StartId
                        }, transaction)).ToArray();

                        if (scores.Length == 0)
                            break;

                        foreach (var score in scores)
                        {
                            if (ensureMaximumStatistics(score))
                                await db.UpdateAsync(score, transaction);
                        }

                        await transaction.CommitAsync(cancellationToken);

                        StartId = scores.Max(s => s.id) + 1;
                    }
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
            if (score.ScoreInfo.maximum_statistics.Count > 0)
                return false;

            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(score.ruleset_id);
            HitResult maxBasicResult = ruleset.GetHitResults().Select(h => h.result).Where(h => h.IsBasic()).MaxBy(Judgement.ToNumericResult);

            foreach ((HitResult result, int count) in score.ScoreInfo.statistics)
            {
                switch (result)
                {
                    case HitResult.LargeTickHit:
                    case HitResult.LargeTickMiss:
                        score.ScoreInfo.maximum_statistics[HitResult.LargeTickHit] = score.ScoreInfo.maximum_statistics.GetValueOrDefault(HitResult.LargeTickHit) + count;
                        break;

                    case HitResult.SmallTickHit:
                    case HitResult.SmallTickMiss:
                        score.ScoreInfo.maximum_statistics[HitResult.SmallTickHit] = score.ScoreInfo.maximum_statistics.GetValueOrDefault(HitResult.SmallTickHit) + count;
                        break;

                    case HitResult.IgnoreHit:
                    case HitResult.IgnoreMiss:
                    case HitResult.SmallBonus:
                    case HitResult.LargeBonus:
                        break;

                    default:
                        score.ScoreInfo.maximum_statistics[maxBasicResult] = score.ScoreInfo.maximum_statistics.GetValueOrDefault(maxBasicResult) + count;
                        break;
                }
            }

            return true;
        }
    }
}
