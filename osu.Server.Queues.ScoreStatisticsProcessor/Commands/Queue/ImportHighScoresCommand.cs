// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using MySqlConnector;
using osu.Game.Rulesets;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Queue
{
    /// <summary>
    /// Imports high scores from the osu_scores_high tables into the new scores table.
    /// </summary>
    /// <remarks>
    /// This command is written under the assumption that only one importer instance is running concurrently.
    /// This is important to guarantee that scores are inserted in the same sequential order that they originally occured,
    /// which can be used for tie-breaker scenarios.
    /// </remarks>
    [Command("import-high-scores", Description = "Imports high scores from the osu_scores_high tables into the new scores table.")]
    public class ImportHighScoresCommand
    {
        /// <summary>
        /// The ruleset to run this import job for.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "--ruleset-id")]
        public int RulesetId { get; set; }

        /// <summary>
        /// The high score ID to start the import process from. This can be used to perform batch reimporting for special cases.
        /// When a value is specified, execution will end after all available items are processed.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "--start-id")]
        public ulong? StartId { get; set; }

        /// <summary>
        /// Whether to adjust processing rate based on slave latency. Defaults to <c>false</c>.
        /// </summary>
        [Option(CommandOptionType.SingleOrNoValue, Template = "--check-slave-latency")]
        public bool CheckSlaveLatency { get; set; }

        /// <summary>
        /// Whether to skip pushing imported score to the elasticsearch indexing queue.
        /// </summary>
        [Option(CommandOptionType.SingleOrNoValue, Template = "--skip-indexing")]
        public bool SkipIndexing { get; set; }

        /// <summary>
        /// The number of processing threads. Note that too many threads may lead to table fragmentation.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "--thread-count")]
        public int ThreadCount { get; set; } = 2;

        /// <summary>
        /// The number of scores to run in each batch. Setting this higher will cause larger SQL statements for insert.
        /// </summary>
        [Option(CommandOptionType.SingleValue, Template = "--batch-size")]
        public int InsertBatchSize { get; set; } = 256000;

        private long lastCommitTimestamp;
        private long startupTimestamp;
        private long lastLatencyCheckTimestamp;

        private ElasticQueuePusher? elasticQueueProcessor;

        /// <summary>
        /// The number of seconds between console progress reports.
        /// </summary>
        private const double seconds_between_report = 2;

        /// <summary>
        /// The number of seconds between checks for slave latency.
        /// </summary>
        private const int seconds_between_latency_checks = 60;

        /// <summary>
        /// The latency a slave is allowed to fall behind before we start to panic.
        /// </summary>
        private const int maximum_slave_latency_seconds = 60;

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(RulesetId);
            string highScoreTable = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId).HighScoreTable;

            DateTimeOffset start = DateTimeOffset.Now;

            ulong lastId = StartId ?? 0;

            Console.WriteLine();
            Console.WriteLine($"Sourcing from {highScoreTable} for {ruleset.ShortName} starting from {lastId}");
            Console.WriteLine($"Insert size: {InsertBatchSize}");
            Console.WriteLine($"Threads: {ThreadCount}");

            ulong maxProcessableId = getMaxProcessable(ruleset);

            Console.WriteLine(maxProcessableId != ulong.MaxValue
                ? $"Will process scores up to ID {maxProcessableId}"
                : "Will process all scores to end of table (could not determine queue state from `score_process_queue`)");

            if (!SkipIndexing)
            {
                elasticQueueProcessor = new ElasticQueuePusher();
                Console.WriteLine($"Indexing to elasticsearch queue(s) {elasticQueueProcessor.ActiveQueues}");
            }

            using (var dbMainQuery = DatabaseAccess.GetConnection())
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (CheckSlaveLatency)
                        checkSlaveLatency(dbMainQuery);

                    var highScores = await dbMainQuery.QueryAsync<HighScore>($"SELECT * FROM {highScoreTable} h WHERE score_id >= @lastId AND score_id <= @maxProcessableId" +
                                                                             " ORDER BY score_id LIMIT @batchSize", new
                    {
                        lastId,
                        maxProcessableId,
                        batchSize = InsertBatchSize * ThreadCount,
                        rulesetId = ruleset.RulesetInfo.OnlineID,
                    });

                    if (!highScores.Any())
                    {
                        Console.WriteLine("No scores found, all done!");
                        break;
                    }

                    List<BatchInserter> runningBatches = new List<BatchInserter>();

                    var orderedHighScores = highScores.OrderBy(s => s.beatmap_id).ThenBy(s => s.score_id);

                    int? lastBeatmapId = null;

                    List<HighScore> batch = new List<HighScore>();

                    foreach (var score in orderedHighScores)
                    {
                        batch.Add(score);

                        // Ensure batches are only ever split on dealing with scores from a new beatmap_id.
                        // This is to enforce insertion order per-beatmap as we may use this to decide ordering in tiebreaker scenarios.
                        if (lastBeatmapId != score.beatmap_id && batch.Count >= InsertBatchSize)
                            queueNextBatch();

                        lastBeatmapId = score.beatmap_id;
                    }

                    queueNextBatch();

                    // update lastId to allow the next bulk query to start from the correct location.
                    lastId = highScores.Max(s => s.score_id);

                    while (!runningBatches.All(t => t.Task.IsCompleted))
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

                            double secondsLeft = (maxProcessableId - lastId) / processingRate;

                            int etaHours = (int)(secondsLeft / 3600);
                            int etaMins = (int)(secondsLeft - etaHours * 3600) / 60;
                            string eta = $"{etaHours}h{etaMins}m";

                            Console.WriteLine($"Inserting up to {lastId:N0} "
                                              + $"[{runningBatches.Count(t => t.Task.IsCompleted),-2}/{runningBatches.Count}] "
                                              + $"{BatchInserter.TotalInsertCount:N0} inserted {BatchInserter.TotalSkipCount:N0} skipped (+{inserted:N0}) {processingRate:N0}/s {eta}");

                            lastCommitTimestamp = currentTimestamp;
                        }

                        Thread.Sleep(10);
                    }

                    if (runningBatches.Any(t => t.Task.IsFaulted))
                    {
                        Console.WriteLine("ERROR: At least one tasks were faulted. Aborting for safety.");
                        Console.WriteLine($"Running batches were processing up to {lastId}.");
                        Console.WriteLine();

                        for (int i = 0; i < runningBatches.Count; i++)
                        {
                            var batchInserter = runningBatches[i];

                            string status = batchInserter.Task.IsFaulted ? $"FAILED ({batchInserter.Task.Exception?.Message})" : "success";
                            Console.WriteLine($"{i,-3} {batchInserter.Scores.First().score_id} - {batchInserter.Scores.Last().score_id}: {status}");
                        }

                        Console.WriteLine();
                        Console.WriteLine(runningBatches.First(t => t.Task.IsFaulted).Task.Exception?.ToString());
                        return -1;
                    }

                    if (elasticQueueProcessor != null)
                    {
                        Debug.Assert(!runningBatches.SelectMany(b => b.ScoreStatisticsItems).Any());

                        var elasticItems = runningBatches.SelectMany(b => b.ElasticScoreItems).ToList();

                        if (elasticItems.Any())
                        {
                            elasticQueueProcessor.PushToQueue(elasticItems);
                            Console.WriteLine($"Queued {elasticItems.Count} item(s) for indexing");
                        }
                    }

                    Console.WriteLine($"Workers processed up to score_id {lastId}");
                    lastId++;

                    void queueNextBatch()
                    {
                        if (batch.Count == 0)
                            return;

                        runningBatches.Add(new BatchInserter(ruleset, batch.ToArray(), importLegacyPP: true));
                        batch.Clear();
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine();

            if (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"Cancelled after {(DateTimeOffset.Now - start).TotalSeconds} seconds.");
                Console.WriteLine($"Final stats: {BatchInserter.TotalInsertCount} inserted, {BatchInserter.TotalSkipCount} skipped");
                Console.WriteLine($"Resume from start id {lastId}");
            }
            else
            {
                Console.WriteLine($"Finished in {(DateTimeOffset.Now - start).TotalSeconds} seconds.");
                Console.WriteLine($"Final stats: {BatchInserter.TotalInsertCount} inserted, {BatchInserter.TotalSkipCount} skipped");
            }

            Console.WriteLine();
            Console.WriteLine();
            return 0;
        }

        private ulong getMaxProcessable(Ruleset ruleset)
        {
            try
            {
                // when doing a single run, we need to make sure not to run into scores which are in the process queue (to avoid
                // touching them while they are still being written).
                using (var db = DatabaseAccess.GetConnection())
                    return db.QuerySingle<ulong?>($"SELECT MIN(score_id) FROM score_process_queue WHERE is_deletion = 0 AND mode = {ruleset.RulesetInfo.OnlineID}") ?? ulong.MaxValue;
            }
            catch
            {
                using (var db = DatabaseAccess.GetConnection())
                {
                    string highScoreTable = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId).HighScoreTable;
                    return db.QuerySingle<ulong>($"SELECT MAX(score_id) FROM {highScoreTable}");
                }
            }
        }

        private void checkSlaveLatency(MySqlConnection db)
        {
            long currentTimestamp = DateTimeOffset.Now.ToUnixTimeSeconds();

            if (currentTimestamp - lastLatencyCheckTimestamp < seconds_between_latency_checks)
                return;

            lastLatencyCheckTimestamp = DateTimeOffset.Now.ToUnixTimeSeconds();

            int? latency;

            do
            {
                // This latency is best-effort, and randomly queried from available hosts (with rough precedence of the importance of the host).
                // When we detect a high latency value, a recovery period should be introduced where we are pretty sure that we're back in a good
                // state before resuming operations.
                latency = db.QueryFirstOrDefault<int?>("SELECT `count` FROM `osu_counts` WHERE NAME = 'slave_latency'");

                if (latency == null)
                    return;

                Console.WriteLine($"Current slave latency of {latency} seconds exceeded maximum of {maximum_slave_latency_seconds} seconds.");
                Console.WriteLine($"Sleeping for {latency} seconds to allow catch-up.");
                Thread.Sleep(latency.Value * 1000);
            } while (latency > maximum_slave_latency_seconds);
        }
    }
}
