// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using MySqlConnector;
using Newtonsoft.Json;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Server.Queues.ScoreStatisticsProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScorePump.Queue
{
    [Command("import-high-scores", Description = $"Imports high scores from the osu_scores_high tables into the new {SoloScore.TABLE_NAME} table.")]
    public class ImportHighScores : QueueCommand
    {
        /// <summary>
        /// The ruleset to run this import job for.
        /// </summary>
        [Option(CommandOptionType.SingleValue)]
        public int RulesetId { get; set; }

        /// <summary>
        /// The high score ID to start the import process from. This can be used to resume an existing job, or perform catch-up on new scores.
        /// </summary>
        [Option(CommandOptionType.SingleValue)]
        public ulong StartId { get; set; }

        private long lastCommitTimestamp;
        private long lastLatencyCheckTimestamp;

        private static int currentReportInsertCount;
        private static int totalInsertCount;

        /// <summary>
        /// The number of scores done in a single processing query. These scores are read in one go, then distributed to parallel insertion workers.
        /// May be adjusted at runtime based on the replication state.
        /// </summary>
        private const int maximum_scores_per_query = 50000;

        /// <summary>
        /// In cases of slave replication latency, this will be the minimum scores processed per top-level query.
        /// </summary>
        private const int safe_minimum_scores_per_query = 500;

        /// <summary>
        /// The number of scores to run in each batch. Setting this higher will reduce the parallelism and in turn, the throughput of this process.
        /// </summary>
        private const int mysql_batch_size = 500;

        /// <summary>
        /// The number of seconds between console progress reports.
        /// </summary>
        private const int seconds_between_report = 2;

        /// <summary>
        /// The number of seconds between checks for slave latency.
        /// </summary>
        private const int seconds_between_latency_checks = 60;

        private int scoresPerQuery = safe_minimum_scores_per_query * 4;

        /// <summary>
        /// The latency a slave is allowed to fall behind before we start to panic.
        /// </summary>
        private const int maximum_slave_latency_seconds = 60;

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(RulesetId);
            string highScoreTable = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId).HighScoreTable;

            DateTimeOffset start = DateTimeOffset.Now;

            Console.WriteLine($"Sourcing from {highScoreTable} for {ruleset.ShortName} starting from {StartId}");
            Console.WriteLine($"Inserting into {SoloScore.TABLE_NAME}");

            Thread.Sleep(5000);

            using (var dbMainQuery = Queue.GetDatabaseConnection())
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    checkSlaveLatency(dbMainQuery);

                    Console.WriteLine($"Retrieving next {scoresPerQuery} scores from {StartId}");

                    var highScores = await dbMainQuery.QueryAsync<HighScore>($"SELECT * FROM {highScoreTable} WHERE score_id >= @startId LIMIT {scoresPerQuery}", new { startId = StartId });

                    if (!highScores.Any())
                    {
                        Console.WriteLine("No scores found");
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
                        if (lastBeatmapId != score.beatmap_id && batch.Count >= mysql_batch_size)
                            queueNextBatch();

                        lastBeatmapId = score.beatmap_id;
                    }

                    queueNextBatch();

                    // update StartId to allow the next bulk query to start from the correct location.
                    StartId = highScores.Max(s => s.score_id);

                    while (!runningBatches.All(t => t.Task.IsCompleted))
                    {
                        long currentTimestamp = DateTimeOffset.Now.ToUnixTimeSeconds();

                        if (currentTimestamp - lastCommitTimestamp >= seconds_between_report)
                        {
                            int inserted = Interlocked.Exchange(ref currentReportInsertCount, 0);

                            Console.WriteLine($"Inserting up to {StartId:N0} "
                                              + $"[{runningBatches.Count(t => t.Task.IsCompleted),-2}/{runningBatches.Count}] "
                                              + $"{totalInsertCount:N0} inserted (+{inserted:N0} new {inserted / seconds_between_report:N0}/s)");

                            lastCommitTimestamp = currentTimestamp;
                        }

                        Thread.Sleep(10);
                    }

                    if (runningBatches.Any(t => t.Task.IsFaulted))
                    {
                        Console.WriteLine("ERROR: At least one tasks were faulted. Aborting for safety.");
                        Console.WriteLine($"Running batches were processing up to {StartId}.");
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

                    Console.WriteLine($"Transaction commit at score_id {StartId}");
                    StartId++;

                    void queueNextBatch()
                    {
                        if (batch.Count == 0)
                            return;

                        runningBatches.Add(new BatchInserter(ruleset, () => Queue.GetDatabaseConnection(), batch.ToArray()));
                        batch.Clear();
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine();

            if (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"Cancelled after {(DateTimeOffset.Now - start).TotalSeconds} seconds.");
                Console.WriteLine($"Resume from start id {StartId}");
            }
            else
            {
                Console.WriteLine($"Finished in {(DateTimeOffset.Now - start).TotalSeconds} seconds.");
            }

            Console.WriteLine();
            Console.WriteLine();
            return 0;
        }

        private void checkSlaveLatency(MySqlConnection db)
        {
            long currentTimestamp = DateTimeOffset.Now.ToUnixTimeSeconds();

            if (currentTimestamp - lastLatencyCheckTimestamp < seconds_between_latency_checks)
                return;

            // This latency is best-effort, and randomly queried from available hosts (with rough precedence of the importance of the host).
            // When we detect a high latency value, a recovery period should be introduced where we are pretty sure that we're back in a good
            // state before resuming operations.
            int latency = db.QueryFirst<int>("SELECT `count` FROM `osu_counts` WHERE NAME = 'slave_latency'");

            if (latency > maximum_slave_latency_seconds)
            {
                Console.WriteLine($"Current slave latency of {latency} seconds exceeded maximum of {maximum_slave_latency_seconds} seconds.");
                Console.WriteLine($"Sleeping for {latency} seconds to allow catch-up.");

                Thread.Sleep(latency * 1000);

                // greatly reduce processing rate to allow for recovery.
                scoresPerQuery = Math.Max(safe_minimum_scores_per_query, scoresPerQuery - 500);
            }
            else if (latency > 2)
            {
                scoresPerQuery = Math.Max(safe_minimum_scores_per_query, scoresPerQuery - 200);
                Console.WriteLine($"Decreasing processing rate to {scoresPerQuery} due to latency of {latency}");
            }
            else if (scoresPerQuery < maximum_scores_per_query)
            {
                scoresPerQuery = Math.Min(maximum_scores_per_query, scoresPerQuery + 100);
                Console.WriteLine($"Increasing processing rate to {scoresPerQuery} due to latency of {latency}");
            }

            lastLatencyCheckTimestamp = DateTimeOffset.Now.ToUnixTimeSeconds();
        }

        /// <summary>
        /// Handles one batch insertion of <see cref="HighScore"/>s. Can be used to parallelize work.
        /// </summary>
        /// <remarks>
        /// Importantly, on a process-wide basis (with the requirement that only one import is happening at once from the same source),
        /// scores for the same beatmap should always be inserted using the same <see cref="BatchInserter"/>. This is to ensure that the new
        /// IDs given to inserted scores are still chronologically correct (we fallback to using IDs for tiebreaker cases where the stored timestamps
        /// are equal to the max precision of mysql TIMESTAMP).
        /// </remarks>
        private class BatchInserter
        {
            private readonly Ruleset ruleset;
            private readonly Func<MySqlConnection> getConnection;

            public HighScore[] Scores { get; }

            public Task Task { get; }

            public BatchInserter(Ruleset ruleset, Func<MySqlConnection> getConnection, HighScore[] scores)
            {
                this.ruleset = ruleset;
                this.getConnection = getConnection;

                Scores = scores;
                Task = Run(scores);
            }

            public async Task Run(HighScore[] scores)
            {
                using (var db = getConnection())
                using (var transaction = await db.BeginTransactionAsync())
                using (var insertCommand = db.CreateCommand())
                {
                    insertCommand.CommandText =
                        // main score insert
                        $"INSERT INTO {SoloScore.TABLE_NAME} (user_id, beatmap_id, ruleset_id, data, has_replay, preserve, created_at, updated_at) "
                        + $"VALUES (@userId, @beatmapId, {ruleset.RulesetInfo.OnlineID}, @data, @has_replay, 1, @date, @date);"
                        // pp insert
                        + $"INSERT INTO {SoloScorePerformance.TABLE_NAME} (score_id, pp) VALUES (LAST_INSERT_ID(), @pp);"
                        // mapping insert
                        + $"INSERT INTO {SoloScoreLegacyIDMap.TABLE_NAME} (ruleset_id, old_score_id, score_id) VALUES ({ruleset.RulesetInfo.OnlineID}, @oldScoreId, LAST_INSERT_ID());";

                    var userId = insertCommand.Parameters.Add("userId", MySqlDbType.UInt32);
                    var oldScoreId = insertCommand.Parameters.Add("oldScoreId", MySqlDbType.UInt64);
                    var beatmapId = insertCommand.Parameters.Add("beatmapId", MySqlDbType.UInt24);
                    var data = insertCommand.Parameters.Add("data", MySqlDbType.JSON);
                    var date = insertCommand.Parameters.Add("date", MySqlDbType.DateTime);
                    var hasReplay = insertCommand.Parameters.Add("has_replay", MySqlDbType.Bool);
                    var pp = insertCommand.Parameters.Add("pp", MySqlDbType.Float);

                    await insertCommand.PrepareAsync();

                    foreach (var highScore in scores)
                    {
                        ScoreInfo referenceScore = await createReferenceScore(ruleset, highScore, db, transaction);

                        pp.Value = highScore.pp;
                        userId.Value = highScore.user_id;
                        oldScoreId.Value = highScore.score_id;
                        beatmapId.Value = highScore.beatmap_id;
                        date.Value = highScore.date;
                        hasReplay.Value = highScore.replay;
                        data.Value = JsonConvert.SerializeObject(new SoloScoreInfo
                        {
                            // id will be written below in the UPDATE call.
                            UserID = highScore.user_id,
                            BeatmapID = highScore.beatmap_id,
                            RulesetID = ruleset.RulesetInfo.OnlineID,
                            Passed = true,
                            TotalScore = (int)referenceScore.TotalScore,
                            Accuracy = referenceScore.Accuracy,
                            MaxCombo = highScore.maxcombo,
                            Rank = Enum.TryParse(highScore.rank, out ScoreRank parsed) ? parsed : ScoreRank.D,
                            Mods = referenceScore.Mods.Select(m => new APIMod(m)).ToArray(),
                            Statistics = referenceScore.Statistics,
                            MaximumStatistics = referenceScore.MaximumStatistics,
                            EndedAt = highScore.date,
                            LegacyTotalScore = highScore.score,
                            LegacyScoreId = highScore.score_id
                        }, new JsonSerializerSettings
                        {
                            DefaultValueHandling = DefaultValueHandling.Ignore
                        });

                        insertCommand.Transaction = transaction;

                        // This could potentially be batched further (ie. to run more SQL statements in a single NonQuery call), but in practice
                        // this does not improve throughput.
                        await insertCommand.ExecuteNonQueryAsync();

                        Interlocked.Increment(ref currentReportInsertCount);
                        Interlocked.Increment(ref totalInsertCount);
                    }

                    await transaction.CommitAsync();
                }
            }

            /// <summary>
            /// Creates a partially-populated "reference" score that provides:
            /// <list type="bullet">
            /// <item><term><see cref="ScoreInfo.Ruleset"/></term></item>
            /// <item><term><see cref="ScoreInfo.Accuracy"/></term></item>
            /// <item><term><see cref="ScoreInfo.Mods"/></term></item>
            /// <item><term><see cref="ScoreInfo.Statistics"/></term></item>
            /// <item><term><see cref="ScoreInfo.MaximumStatistics"/></term></item>
            /// </list>
            /// </summary>
            private async Task<ScoreInfo> createReferenceScore(Ruleset ruleset, HighScore highScore, MySqlConnection connection, MySqlTransaction transaction)
            {
                var scoreInfo = new ScoreInfo
                {
                    Ruleset = ruleset.RulesetInfo,
                    Mods = ruleset.ConvertFromLegacyMods((LegacyMods)highScore.enabled_mods).ToArray(),
                    Statistics = new Dictionary<HitResult, int>(),
                    MaximumStatistics = new Dictionary<HitResult, int>(),
                };

                // Populate statistics and accuracy.
                scoreInfo.SetCount50(highScore.count50);
                scoreInfo.SetCount100(highScore.count100);
                scoreInfo.SetCount300(highScore.count300);
                scoreInfo.SetCountMiss(highScore.countmiss);
                scoreInfo.SetCountGeki(highScore.countgeki);
                scoreInfo.SetCountKatu(highScore.countkatu);
                LegacyScoreDecoder.PopulateAccuracy(scoreInfo);

                // Trim zero values from statistics.
                scoreInfo.Statistics = scoreInfo.Statistics.Where(kvp => kvp.Value != 0).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                // Populate the maximum statistics.
                HitResult maxBasicResult = ruleset.GetHitResults().Select(h => h.result).Where(h => h.IsBasic()).MaxBy(Judgement.ToNumericResult);

                foreach ((HitResult result, int count) in scoreInfo.Statistics)
                {
                    switch (result)
                    {
                        case HitResult.LargeTickHit:
                        case HitResult.LargeTickMiss:
                            scoreInfo.MaximumStatistics[HitResult.LargeTickHit] = scoreInfo.MaximumStatistics.GetValueOrDefault(HitResult.LargeTickHit) + count;
                            break;

                        case HitResult.SmallTickHit:
                        case HitResult.SmallTickMiss:
                            scoreInfo.MaximumStatistics[HitResult.SmallTickHit] = scoreInfo.MaximumStatistics.GetValueOrDefault(HitResult.SmallTickHit) + count;
                            break;

                        case HitResult.IgnoreHit:
                        case HitResult.IgnoreMiss:
                        case HitResult.SmallBonus:
                        case HitResult.LargeBonus:
                            break;

                        default:
                            scoreInfo.MaximumStatistics[maxBasicResult] = scoreInfo.MaximumStatistics.GetValueOrDefault(maxBasicResult) + count;
                            break;
                    }
                }

                // In osu! and osu!mania, some judgements affect combo but aren't stored to scores.
                // A special hit result is used to pad out the combo value to match, based on the max combo from the beatmap attributes.
                int maxComboFromStatistics = scoreInfo.MaximumStatistics.Where(kvp => kvp.Key.AffectsCombo()).Select(kvp => kvp.Value).DefaultIfEmpty(0).Sum();

                // Note that using BeatmapStore will fail if a particular difficulty attribute value doesn't exist in the database.
                // To get around this, we'll specifically look up the attribute directly from the database, utilising the most primitive mod lookup value
                // for the ruleset in order to prevent an additional failures if some new mod combinations were added as difficulty adjustment mods.
                // This is a little bit hacky and wouldn't be required if the databased attributes were always in-line with osu!lazer, but that's not the case.

                int difficultyMods = 0;

                switch (ruleset.RulesetInfo.OnlineID)
                {
                    case 0:
                    case 1:
                    case 2:
                        // In these rulesets, every mod combination has the same max combo. So use the attribute with mods=0.
                        break;

                    case 3:
                        // In mania, only keymods affect the number of objects and thus the max combo.
                        difficultyMods = highScore.enabled_mods & (int)LegacyModsHelper.KEY_MODS;
                        break;
                }

                int? maxComboFromAttributes = await connection.QuerySingleOrDefaultAsync<int?>(
                    $"SELECT `value` FROM {BeatmapDifficultyAttribute.TABLE_NAME} WHERE `beatmap_id` = @BeatmapId AND `mode` = @RulesetId AND `mods` = @Mods AND `attrib_id` = 9", new
                    {
                        BeatmapId = highScore.beatmap_id,
                        RulesetId = ruleset.RulesetInfo.OnlineID,
                        Mods = difficultyMods
                    }, transaction);

                if (maxComboFromAttributes == null)
                {
                    await Console.Error.WriteLineAsync($"{highScore.score_id}: Could not find difficulty attributes for beatmap {highScore.beatmap_id} in the database.");
                    return scoreInfo;
                }

#pragma warning disable CS0618
                if (maxComboFromAttributes > maxComboFromStatistics)
                    scoreInfo.MaximumStatistics[HitResult.LegacyComboIncrease] = maxComboFromAttributes.Value - maxComboFromStatistics;
#pragma warning restore CS0618

                ScoreProcessor scoreProcessor = ruleset.CreateScoreProcessor();
                int baseScore = scoreInfo.Statistics.Where(kvp => kvp.Key.AffectsAccuracy()).Sum(kvp => kvp.Value * Judgement.ToNumericResult(kvp.Key));
                int maxBaseScore = scoreInfo.MaximumStatistics.Where(kvp => kvp.Key.AffectsAccuracy()).Sum(kvp => kvp.Value * Judgement.ToNumericResult(kvp.Key));

                scoreInfo.TotalScore = (int)scoreProcessor.ComputeScore(ScoringMode.Standardised, scoreInfo);
                scoreInfo.Accuracy = maxBaseScore == 0 ? 1 : baseScore / (double)maxBaseScore;

                return scoreInfo;
            }
        }

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        [Serializable]
        private class HighScore
        {
            public ulong score_id { get; set; }
            public int beatmap_id { get; set; }
            public int user_id { get; set; }
            public int score { get; set; }
            public ushort maxcombo { get; set; }
            public string rank { get; set; } = null!; // Actually a ScoreRank, but reading as a string for manual parsing.
            public ushort count50 { get; set; }
            public ushort count100 { get; set; }
            public ushort count300 { get; set; }
            public ushort countmiss { get; set; }
            public ushort countgeki { get; set; }
            public ushort countkatu { get; set; }
            public bool perfect { get; set; }
            public int enabled_mods { get; set; }
            public DateTimeOffset date { get; set; }
            public float pp { get; set; }
            public bool replay { get; set; }
            public bool hidden { get; set; }
            public string country_acronym { get; set; } = null!;
        }
    }
}
