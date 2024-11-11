// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Game.Rulesets;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Queue
{
    /// <summary>
    /// Watches for new scores from the osu_scores tables and imports into the new scores table.
    /// </summary>
    /// <remarks>
    /// This command is written under the assumption that only one importer instance is running concurrently.
    /// This is important to guarantee that scores are inserted in the same sequential order that they originally occured,
    /// which can be used for tie-breaker scenarios.
    /// </remarks>
    [Command("watch-scores", Description = "Watches for new (non-)high scores from the osu_scores tables and imports into the new scores table.")]
    public class WatchScoresCommand
    {
        /// <summary>
        /// The ruleset to run this import job for.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "--ruleset-id")]
        public int RulesetId { get; set; }

        /// <summary>
        /// When set to <c>true</c>, scores will not be queued to the score statistics processor.
        /// </summary>
        [Option(CommandOptionType.SingleOrNoValue, Template = "--skip-score-processor")]
        public bool SkipScoreProcessor { get; set; }

        [Option(CommandOptionType.SingleOrNoValue, Template = "--dry-run")]
        public bool DryRun { get; set; }

        private long lastCommitTimestamp;
        private long startupTimestamp;

        private ScoreStatisticsQueueProcessor? scoreStatisticsQueueProcessor;

        /// <summary>
        /// The number of seconds between console progress reports.
        /// </summary>
        private const double seconds_between_report = 2;

        private ulong lastScoreId;

        private readonly DateTimeOffset startedAt = DateTimeOffset.Now;

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(RulesetId);
            string scoreTable = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId).ScoreTable;

            using var db = await DatabaseAccess.GetConnectionAsync(cancellationToken);

            lastScoreId = await db.QuerySingleAsync<ulong>($"SELECT MAX(score_id) FROM {scoreTable}");

            if (!SkipScoreProcessor)
            {
                scoreStatisticsQueueProcessor = new ScoreStatisticsQueueProcessor();
                Console.WriteLine($"Pushing imported scores to redis queue {scoreStatisticsQueueProcessor.QueueName}");
            }

            if (DryRun)
                Console.WriteLine("RUNNING IN DRY RUN MODE.");

            Console.WriteLine();
            Console.WriteLine("Starting processing in 5 seconds...");
            await Task.Delay(5000, cancellationToken);

            using (var dbMainQuery = await DatabaseAccess.GetConnectionAsync(cancellationToken))
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    HighScore[] scores = (await dbMainQuery.QueryAsync<HighScore>(
                            $"SELECT * FROM {scoreTable} "
                            // A delay is applied here to be sure that high_score_id has been populated.
                            // This is a safety as web-10 doesn't hold a transaction between the insert to `osu_scores` and `osu_scores_high`.
                            // Worst case scenario is that a high score exists twice, once with missing `legacy_score_id`.
                            + "WHERE score_id >= @lastScoreId AND date < DATE_SUB(NOW(), INTERVAL 5 SECOND) AND high_score_id IS NULL ORDER BY score_id LIMIT 50", new
                            {
                                lastScoreId = lastScoreId,
                                RulesetId,
                            }))
                        .ToArray();

                    if (scores.Length == 0)
                    {
                        Thread.Sleep(500);
                        continue;
                    }

                    // Need to obtain score_id before zeroing them out.
                    lastScoreId = scores.Last().score_id;

                    var inserter = new BatchInserter(ruleset, scores, importLegacyPP: SkipScoreProcessor, dryRun: DryRun, throwOnFailure: false);

                    while (!inserter.Task.IsCompleted)
                    {
                        outputProgress();
                        Thread.Sleep(10);
                    }

                    if (inserter.Task.IsFaulted)
                    {
                        Console.WriteLine("ERROR: Inserter failed. Aborting for safety.");
                        Console.WriteLine($"Running batches were processing up to {lastScoreId}.");
                        Console.WriteLine();

                        throw inserter.Task.Exception!;
                    }

                    pushCompletedScoreToQueue(inserter);

                    Console.WriteLine($"Workers processed up to (score_id: {lastScoreId})");
                    lastScoreId++;
                }
            }

            outputFinalStats();
            return 0;
        }

        private void pushCompletedScoreToQueue(BatchInserter inserter)
        {
            if (scoreStatisticsQueueProcessor == null) return;

            var scoreStatisticsItems = inserter.ScoreStatisticsItems.ToList();

            if (scoreStatisticsItems.Any())
            {
                if (!DryRun)
                    scoreStatisticsQueueProcessor.PushToQueue(scoreStatisticsItems);
                Console.WriteLine($"Queued {scoreStatisticsItems.Count} item(s) for statistics processing");
            }
        }

        private void outputProgress()
        {
            long currentTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            if ((currentTimestamp - lastCommitTimestamp) / 1000f >= seconds_between_report)
            {
                int inserted = Interlocked.Exchange(ref BatchInserter.CurrentReportInsertCount, 0);
                int deleted = Interlocked.Exchange(ref BatchInserter.CurrentReportDeleteCount, 0);

                // Only set startup timestamp after first insert actual insert/update run to avoid weighting during catch-up.
                if (inserted > 0 && startupTimestamp == 0)
                    startupTimestamp = lastCommitTimestamp;

                double secondsSinceStart = (double)(currentTimestamp - startupTimestamp) / 1000;
                double processingRate = BatchInserter.TotalInsertCount / secondsSinceStart;

                Console.WriteLine($"Inserting up to {lastScoreId:N0}: "
                                  + $"{BatchInserter.TotalInsertCount:N0} ins {BatchInserter.TotalDeleteCount:N0} del {BatchInserter.TotalSkipCount:N0} skip (+{inserted:N0} new +{deleted:N0} del) {processingRate:N0}/s");

                lastCommitTimestamp = currentTimestamp;
            }
        }

        private void outputFinalStats()
        {
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine($"Cancelled after {(DateTimeOffset.Now - startedAt).TotalSeconds} seconds.");
            Console.WriteLine($"Final stats: {BatchInserter.TotalInsertCount} inserted, {BatchInserter.TotalSkipCount} skipped, {BatchInserter.TotalDeleteCount} deleted");
            Console.WriteLine($"Resume from start id {lastScoreId}");
            Console.WriteLine();
            Console.WriteLine();
        }
    }
}
