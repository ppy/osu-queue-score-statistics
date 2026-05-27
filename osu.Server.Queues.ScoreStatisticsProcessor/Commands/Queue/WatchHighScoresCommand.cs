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
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Queue
{
    /// <summary>
    /// Watches for new high scores from the osu_scores_high tables and imports into the new scores table.
    /// </summary>
    /// <remarks>
    /// This command is written under the assumption that only one importer instance is running concurrently.
    /// This is important to guarantee that scores are inserted in the same sequential order that they originally occured,
    /// which can be used for tie-breaker scenarios.
    /// </remarks>
    [Command("watch-high-scores", Description = "Watches for new high scores from the osu_scores_high tables and imports into the new scores table.")]
    public class WatchHighScoresCommand
    {
        /// <summary>
        /// The ruleset to run this import job for.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "--ruleset-id")]
        public int RulesetId { get; set; }

        [Option(CommandOptionType.SingleOrNoValue, Template = "--dry-run")]
        public bool DryRun { get; set; }

        private long lastCommitTimestamp;
        private long startupTimestamp;

        private ScoreStatisticsQueueProcessor scoreStatisticsQueueProcessor = null!;

        /// <summary>
        /// The number of seconds between console progress reports.
        /// </summary>
        private const double seconds_between_report = 2;

        private ulong lastQueueId;

        private readonly DateTimeOffset startedAt = DateTimeOffset.Now;

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(RulesetId);
            string highScoreTable = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId).HighScoreTable;

            using var db = await DatabaseAccess.GetConnectionAsync(cancellationToken);

            ulong? lastImportedLegacyScoreId = db.QuerySingleOrDefault<ulong?>($"SELECT MAX(legacy_score_id) FROM scores WHERE ruleset_id = {RulesetId}") ?? 0;

            var firstEntry = db.QuerySingleOrDefault<ScoreProcessQueue>($"SELECT * FROM score_process_queue WHERE mode = {RulesetId} ORDER BY queue_id LIMIT 1");

            if (firstEntry == null)
            {
                Console.WriteLine("Couldn't find any scores to process");
                return -1;
            }

            if (lastImportedLegacyScoreId >= firstEntry.score_id)
            {
                var entry = db.QuerySingleOrDefault<ScoreProcessQueue>(
                    $"SELECT * FROM score_process_queue WHERE mode = {RulesetId} AND score_id = {lastImportedLegacyScoreId + 1} ORDER BY queue_id LIMIT 1");

                if (entry != null)
                {
                    lastQueueId = entry.queue_id;
                    Console.WriteLine($"Continuing watch mode for {ruleset.ShortName} from last processed legacy score (score_id: {entry.score_id} queue_id: {entry.queue_id})");
                }
                else
                {
                    // There may have been no new scores since the last run.
                    entry = db.QuerySingle<ScoreProcessQueue>($"SELECT * FROM score_process_queue WHERE mode = {RulesetId} AND score_id = {lastImportedLegacyScoreId}");
                    lastQueueId = entry.queue_id;
                    Console.WriteLine($"Continuing watch mode for {ruleset.ShortName} from last processed legacy score (queue_id: {entry.queue_id})");
                }
            }
            else
            {
                lastQueueId = firstEntry.queue_id;
                Console.WriteLine($"WARNING: Continuing watch mode from start of the processing queue (score_id: {firstEntry.score_id} queue_id: {firstEntry.queue_id})");
                Console.WriteLine("This implies that a full import hasn't been run yet, you might want to run another import first to catch up.");
                Console.WriteLine();
                Console.WriteLine("You have 10 seconds to decide if you really want to do this..");
                await Task.Delay(10000, cancellationToken);
            }

            scoreStatisticsQueueProcessor = new ScoreStatisticsQueueProcessor();
            Console.WriteLine($"Pushing imported scores to redis queue {scoreStatisticsQueueProcessor.QueueName}");

            if (DryRun)
                Console.WriteLine("RUNNING IN DRY RUN MODE.");

            Console.WriteLine();
            Console.WriteLine("Starting processing in 5 seconds...");
            await Task.Delay(5000, cancellationToken);

            HighScore? pendingProcessing = null;
            int pendingProcessingWaitTime = 0;

            using (var dbMainQuery = await DatabaseAccess.GetConnectionAsync(cancellationToken))
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    HighScore[] highScores = (await dbMainQuery.QueryAsync<HighScore>(
                                                 "SELECT q.*, h.*, s.id as new_id, s.user_id as new_user_id FROM osu.score_process_queue q "
                                                 + $"LEFT JOIN {highScoreTable} h USING (score_id) "
                                                 + $"LEFT JOIN scores s ON q.score_id = s.legacy_score_id AND s.ruleset_id = {RulesetId} "
                                                 + $"WHERE queue_id >= @lastQueueId AND mode = {RulesetId} ORDER BY queue_id LIMIT 50", new
                                                 {
                                                     lastQueueId,
                                                     RulesetId,
                                                 }))
                                             // there might be multiple queue entries for the same insert. this can cause issues due to how we do the mapping lookup so let's fix that.
                                             .DistinctBy(s => s.score_id)
                                             .OrderBy(s => s.score_id)
                                             .ToArray();

                    var lastPending = pendingProcessing;
                    pendingProcessing = highScores.FirstOrDefault(s => s.status == 0);

                    const int sleep = 500;

                    if (pendingProcessing != null)
                    {
                        // the pending score may have changed, so reset the wait time at this point.
                        if (pendingProcessing.score_id != lastPending?.score_id)
                        {
                            pendingProcessingWaitTime = sleep;

                            Console.WriteLine($"Waiting on processing of (score_id: {pendingProcessing.score_id} queue_id: {pendingProcessing.queue_id})");
                            Thread.Sleep(sleep);
                            continue;
                        }

                        // 5 minute timeout
                        if (pendingProcessingWaitTime > 300000)
                        {
                            Console.WriteLine($"Importing score which was not processed due to timeout (score_id: {pendingProcessing.score_id} queue_id: {pendingProcessing.queue_id})");

                            // only process up to the timed-out entry.
                            highScores = highScores.Where(s => s.queue_id <= pendingProcessing.queue_id).ToArray();
                        }
                        else
                        {
                            pendingProcessingWaitTime += sleep;

                            Console.WriteLine($"Waiting on processing of (score_id: {pendingProcessing.score_id} queue_id: {pendingProcessing.queue_id})");
                            Thread.Sleep(sleep);
                            continue;
                        }
                    }

                    pendingProcessing = null;
                    pendingProcessingWaitTime = 0;

                    if (highScores.Length == 0)
                    {
                        Thread.Sleep(sleep);
                        continue;
                    }

                    var inserter = new BatchInserter(ruleset, highScores, dryRun: DryRun);

                    while (!inserter.Task.IsCompleted)
                    {
                        outputProgress();
                        Thread.Sleep(10);
                    }

                    if (inserter.Task.IsFaulted)
                    {
                        Console.WriteLine("ERROR: Inserter failed. Aborting for safety.");
                        Console.WriteLine($"Running batches were processing up to {lastQueueId}.");
                        Console.WriteLine();

                        throw inserter.Task.Exception!;
                    }

                    pushCompletedScoreToQueue(inserter);

                    ulong lastScoreId = highScores.Last().score_id;
                    lastQueueId = highScores.Max(score => score.queue_id!.Value);
                    Console.WriteLine($"Workers processed up to (score_id: {lastScoreId} queue_id: {lastQueueId})");
                    lastQueueId++;
                }
            }

            outputFinalStats();
            return 0;
        }

        private void pushCompletedScoreToQueue(BatchInserter inserter)
        {
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

                // Only set startup timestamp after first insert actual insert/update run to avoid weighting during catch-up.
                if (inserted > 0 && startupTimestamp == 0)
                    startupTimestamp = lastCommitTimestamp;

                double secondsSinceStart = (double)(currentTimestamp - startupTimestamp) / 1000;
                double processingRate = BatchInserter.TotalInsertCount / secondsSinceStart;

                Console.WriteLine($"Inserting up to {lastQueueId:N0}: "
                                  + $"{BatchInserter.TotalInsertCount:N0} inserted {processingRate:N0}/s");

                lastCommitTimestamp = currentTimestamp;
            }
        }

        private void outputFinalStats()
        {
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine($"Cancelled after {(DateTimeOffset.Now - startedAt).TotalSeconds} seconds.");
            Console.WriteLine($"Final stats: {BatchInserter.TotalInsertCount} inserted");
            Console.WriteLine($"Resume from start id {lastQueueId}");
            Console.WriteLine();
            Console.WriteLine();
        }
    }
}
