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
using osu.Game.Rulesets;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Server.Queues.ScoreStatisticsProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScorePump
{
    [Command("import-high-scores", Description = "Imports high scores from the osu_scores_high tables into the new solo_scores table.")]
    public class ImportHighScores : ScorePump
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
        public long StartId { get; set; }

        private long lastCommitTimestamp;

        private static int currentReportInsertCount;
        private static int totalInsertCount;

        /// <summary>
        /// The number of scores done in a single processing query. These scores are read in one go, then distributed to parallel insertion workers.
        /// May be adjusted at runtime based on the replication state.
        /// </summary>
        private const int initial_scores_per_query = 50000;

        /// <summary>
        /// The number of scores to run in each batch. Setting this higher will reduce the parallelism and in turn, the throughput of this process.
        /// </summary>
        private const int mysql_batch_size = 500;

        /// <summary>
        /// The number of seconds between console progress reports.
        /// </summary>
        private const int seconds_between_report = 2;

        private int scoresPerQuery = initial_scores_per_query;

        /// <summary>
        /// The latency a slave is allowed to fall behind before we start to panic.
        /// </summary>
        private const int maximum_slave_latency_seconds = 10;

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(RulesetId);
            string highScoreTable = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId).HighScoreTable;

            using (var dbMainQuery = Queue.GetDatabaseConnection())
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine($"Retrieving next {scoresPerQuery} scores starting from {StartId + 1}");

                    var highScores = await dbMainQuery.QueryAsync<HighScore>($"SELECT * FROM {highScoreTable} WHERE score_id > @startId LIMIT {scoresPerQuery}", new { startId = StartId });

                    if (!highScores.Any())
                        break;

                    List<Task> waitingTasks = new List<Task>();

                    var orderedHighScores = highScores.OrderBy(s => s.beatmap_id).ThenBy(s => s.score_id);

                    int? lastBeatmapId = null;

                    List<HighScore> batch = new List<HighScore>();

                    foreach (var score in orderedHighScores)
                    {
                        batch.Add(score);

                        if (lastBeatmapId != score.beatmap_id && batch.Count >= mysql_batch_size)
                            queueNextBatch();

                        lastBeatmapId = score.beatmap_id;
                    }

                    queueNextBatch();

                    // update StartId to allow the next bulk query to start from the correct location.
                    StartId = highScores.Max(s => s.score_id);

                    while (!waitingTasks.All(t => t.IsCompleted))
                    {
                        long currentTimestamp = DateTimeOffset.Now.ToUnixTimeSeconds();

                        if (currentTimestamp - lastCommitTimestamp >= seconds_between_report)
                        {
                            int inserted = Interlocked.Exchange(ref currentReportInsertCount, 0);

                            Console.WriteLine($"Writing up to {StartId} "
                                              + $"[{waitingTasks.Count(t => t.IsCompleted)}/{waitingTasks.Count}] "
                                              + $"{totalInsertCount} (+{inserted} new {inserted / seconds_between_report}/s)");

                            lastCommitTimestamp = currentTimestamp;

                            checkSlaveLatency(dbMainQuery);
                        }

                        Thread.Sleep(10);
                    }

                    void queueNextBatch()
                    {
                        if (batch.Count == 0)
                            return;

                        waitingTasks.Add(new BatchInserter(ruleset, () => Queue.GetDatabaseConnection(), cancellationToken).Run(batch.ToArray()));
                        batch.Clear();
                    }
                }
            }

            return 0;
        }

        private void checkSlaveLatency(MySqlConnection db)
        {
            // This latency is best-effort, and randomly queried from available hosts (with rough precedence of the importance of the host).
            // When we detect a high latency value, a recovery period should be introduced where we are pretty sure that we're back in a good
            // state before resuming operations.
            int latency = db.QueryFirst<int>("SELECT `count` FROM `osu_counts` WHERE NAME = 'slave_latency'");

            if (latency > maximum_slave_latency_seconds)
            {
                Console.WriteLine($"Current slave latency of {latency} seconds exceeded maximum of {maximum_slave_latency_seconds} seconds.");
                Console.WriteLine($"Sleeping for {latency} seconds to allow catch-up.");

                Thread.Sleep(latency * 1000);
            }

            scoresPerQuery = latency > 0
                ? Math.Max(1000, scoresPerQuery - (latency * 100))
                : Math.Min(initial_scores_per_query, scoresPerQuery + 100);
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
            private readonly CancellationToken cancellationToken;

            public BatchInserter(Ruleset ruleset, Func<MySqlConnection> getConnection, CancellationToken cancellationToken)
            {
                this.ruleset = ruleset;
                this.getConnection = getConnection;
                this.cancellationToken = cancellationToken;
            }

            public async Task Run(HighScore[] scores)
            {
                using (var db = getConnection())
                using (var transaction = await db.BeginTransactionAsync(cancellationToken))
                using (var insertCommand = db.CreateCommand())
                {
                    insertCommand.CommandText =
                        // main score insert
                        $"INSERT INTO {SoloScore.TABLE_NAME} (user_id, beatmap_id, ruleset_id, data, preserve, created_at, updated_at) "
                        + $"VALUES (@userId, @beatmapId, {ruleset.RulesetInfo.OnlineID}, @data, 1, @date, @date);"
                        // pp insert
                        + $"INSERT INTO {SoloScorePerformance.TABLE_NAME} (score_id, pp) VALUES (LAST_INSERT_ID(), @pp);"
                        // mapping insert
                        + $"INSERT INTO {SoloScoreLegacyIDMap.TABLE_NAME} (ruleset_id, old_score_id, score_id) VALUES ({ruleset.RulesetInfo.OnlineID}, @oldScoreId, LAST_INSERT_ID());";

                    var userId = insertCommand.Parameters.Add("userId", MySqlDbType.UInt32);
                    var oldScoreId = insertCommand.Parameters.Add("oldScoreId", MySqlDbType.UInt32);
                    var beatmapId = insertCommand.Parameters.Add("beatmapId", MySqlDbType.UInt24);
                    var data = insertCommand.Parameters.Add("data", MySqlDbType.JSON);
                    var date = insertCommand.Parameters.Add("date", MySqlDbType.DateTime);
                    var pp = insertCommand.Parameters.Add("pp", MySqlDbType.Float);

                    await insertCommand.PrepareAsync(cancellationToken);

                    foreach (var highScore in scores)
                    {
                        var (accuracy, statistics) = getAccuracyAndStatistics(ruleset, highScore);

                        pp.Value = highScore.pp;
                        userId.Value = highScore.user_id;
                        oldScoreId.Value = highScore.score_id;
                        beatmapId.Value = highScore.beatmap_id;
                        date.Value = highScore.date;
                        data.Value = JsonConvert.SerializeObject(new SoloScoreInfo
                        {
                            // id will be written below in the UPDATE call.
                            user_id = highScore.user_id,
                            beatmap_id = highScore.beatmap_id,
                            ruleset_id = ruleset.RulesetInfo.OnlineID,
                            passed = true,
                            total_score = highScore.score,
                            accuracy = accuracy,
                            max_combo = highScore.maxcombo,
                            rank = Enum.TryParse(highScore.rank, out ScoreRank parsed) ? parsed : ScoreRank.D,
                            mods = ruleset.ConvertFromLegacyMods((LegacyMods)highScore.enabled_mods).Select(m => new APIMod(m)).ToList(),
                            statistics = statistics,
                        });

                        insertCommand.Transaction = transaction;

                        // This could potentially be batched further (ie. to run more SQL statements in a single NonQuery call), but in practice
                        // this does not improve throughput.
                        await insertCommand.ExecuteNonQueryAsync(cancellationToken);

                        Interlocked.Increment(ref currentReportInsertCount);
                        Interlocked.Increment(ref totalInsertCount);
                    }

                    await transaction.CommitAsync(cancellationToken);
                }
            }

            private (double accuracy, Dictionary<HitResult, int> statistics) getAccuracyAndStatistics(Ruleset ruleset, HighScore highScore)
            {
                var scoreInfo = new ScoreInfo
                {
                    Ruleset = ruleset.RulesetInfo,
                };

                scoreInfo.SetCount50(highScore.count50);
                scoreInfo.SetCount100(highScore.count100);
                scoreInfo.SetCount300(highScore.count300);
                scoreInfo.SetCountMiss(highScore.countmiss);
                scoreInfo.SetCountGeki(highScore.countgeki);
                scoreInfo.SetCountKatu(highScore.countkatu);

                LegacyScoreDecoder.PopulateAccuracy(scoreInfo);

                return (scoreInfo.Accuracy, scoreInfo.Statistics);
            }
        }

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        [Serializable]
        private class HighScore
        {
            public uint score_id { get; set; }
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
