// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
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
    internal class InsertParameters
    {
        public MySqlParameter UserId;
        public MySqlParameter OldScoreId;
        public MySqlParameter BeatmapId;
        public MySqlParameter Data;
        public MySqlParameter Date;
        public MySqlParameter PP;

        public InsertParameters(MySqlParameter userId, MySqlParameter oldScoreId, MySqlParameter beatmapId, MySqlParameter data, MySqlParameter date, MySqlParameter pp)
        {
            UserId = userId;
            OldScoreId = oldScoreId;
            BeatmapId = beatmapId;
            Data = data;
            Date = date;
            PP = pp;
        }
    }

    [Command("import-high-scores", Description = "Imports high scores from the osu_scores_high tables into the new solo scores table.")]
    public class ImportHighScores : ScorePump
    {
        private long lastCommitTimestamp;
        private int currentTransactionInsertCount;

        [Option(CommandOptionType.SingleValue)]
        public int RulesetId { get; set; }

        [Option(CommandOptionType.SingleValue)]
        public long StartId { get; set; }

        private const int scores_per_query = 10000;

        private const int seconds_between_transactions = 1;

        private const int insert_size = 100;

        public int OnExecute(CancellationToken cancellationToken)
        {
            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(RulesetId);
            string highScoreTable = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId).HighScoreTable;

            using (var dbMainQuery = Queue.GetDatabaseConnection())
            using (var db = Queue.GetDatabaseConnection())
            {
                var transaction = db.BeginTransaction();

                var insertCommand = db.CreateCommand();

                InsertParameters[] insertCommandParameters = new InsertParameters[insert_size];

                for (int i = 0; i < insert_size; i++)
                {
                    insertCommand.CommandText +=
                        // main score insert
                        $"INSERT INTO {SoloScore.TABLE_NAME} (user_id, beatmap_id, ruleset_id, data, preserve, created_at, updated_at) "
                        + $"VALUES (@userId{i}, @beatmapId{i}, {RulesetId}, @data{i}, 1, @date{i}, @date{i});"
                        // pp insert
                        + $"INSERT INTO {SoloScorePerformance.TABLE_NAME} (score_id, pp) VALUES (@@LAST_INSERT_ID, @pp{i});"
                        // mapping insert
                        + $"INSERT INTO {SoloScoreLegacyIDMap.TABLE_NAME} (ruleset_id, old_score_id, score_id) VALUES ({RulesetId}, @oldScoreId{i}, @@LAST_INSERT_ID);";

                    insertCommandParameters[i] = new InsertParameters(
                        insertCommand.Parameters.Add($"userId{i}", MySqlDbType.UInt32),
                        insertCommand.Parameters.Add($"oldScoreId{i}", MySqlDbType.UInt32),
                        insertCommand.Parameters.Add($"beatmapId{i}", MySqlDbType.UInt24),
                        insertCommand.Parameters.Add($"data{i}", MySqlDbType.JSON),
                        insertCommand.Parameters.Add($"date{i}", MySqlDbType.DateTime),
                        insertCommand.Parameters.Add($"pp{i}", MySqlDbType.Float)
                    );
                }

                insertCommand.Prepare();

                int currentCommandIndex = -1;

                InsertParameters getNextInsertion()
                {
                    if (currentCommandIndex == insert_size - 1)
                    {
                        insertCommand.Transaction = transaction;
                        insertCommand.ExecuteNonQuery();

                        Interlocked.Add(ref currentTransactionInsertCount, insert_size);

                        long currentTimestamp = DateTimeOffset.Now.ToUnixTimeSeconds();

                        if (currentTimestamp - lastCommitTimestamp >= seconds_between_transactions)
                        {
                            transaction.Commit();

                            int inserted = Interlocked.Exchange(ref currentTransactionInsertCount, 0);

                            Console.WriteLine($"Written up to {insertCommandParameters[currentCommandIndex].OldScoreId.Value} (+{inserted} rows {inserted / seconds_between_transactions}/s)");

                            transaction = db.BeginTransaction();
                            lastCommitTimestamp = currentTimestamp;
                        }
                    }

                    currentCommandIndex = (currentCommandIndex + 1) % insert_size;

                    return insertCommandParameters[currentCommandIndex];
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine($"Retrieving next {scores_per_query} scores starting from {StartId + 1}");

                    var highScores = dbMainQuery.Query<HighScore>($"SELECT * FROM {highScoreTable} WHERE score_id > @startId LIMIT {scores_per_query}", new { startId = StartId });

                    if (!highScores.Any())
                        break;

                    foreach (var highScore in highScores)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        var (accuracy, statistics) = getAccuracyAndStatistics(ruleset, highScore);

                        var insertion = getNextInsertion();

                        insertion.PP.Value = highScore.pp;
                        insertion.UserId.Value = highScore.user_id;
                        insertion.OldScoreId.Value = highScore.score_id;
                        insertion.BeatmapId.Value = highScore.beatmap_id;
                        insertion.Date.Value = highScore.date;
                        insertion.Data.Value = JsonConvert.SerializeObject(new SoloScoreInfo
                        {
                            // id will be written below in the UPDATE call.
                            user_id = highScore.user_id,
                            beatmap_id = highScore.beatmap_id,
                            ruleset_id = RulesetId,
                            passed = true,
                            total_score = highScore.score,
                            accuracy = accuracy,
                            max_combo = highScore.maxcombo,
                            rank = Enum.TryParse(highScore.rank, out ScoreRank parsed) ? parsed : ScoreRank.D,
                            mods = ruleset.ConvertFromLegacyMods((LegacyMods)highScore.enabled_mods).Select(m => new APIMod(m)).ToList(),
                            statistics = statistics,
                        });

                        // update StartId to allow the next bulk query to start from the correct location.
                        StartId = highScore.score_id;
                    }
                }

                // TODO: final insert needs to be run somehow...
                transaction.Commit();
            }

            return 0;
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
