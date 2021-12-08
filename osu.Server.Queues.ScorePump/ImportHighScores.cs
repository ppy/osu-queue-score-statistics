// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Dapper;
using Dapper.Contrib.Extensions;
using McMaster.Extensions.CommandLineUtils;
using MySqlConnector;
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

        public int OnExecute(CancellationToken cancellationToken)
        {
            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(RulesetId);
            string highScoreTable = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId).HighScoreTable;

            using (var dbMainQuery = Queue.GetDatabaseConnection())
            using (var db = Queue.GetDatabaseConnection())
            {
                var transaction = db.BeginTransaction();

                var insertPPCommand = db.CreateCommand();

                insertPPCommand.CommandText = $"INSERT INTO {SoloScorePerformance.TABLE_NAME} (score_id, pp) VALUES (@insertId, @pp)";

                var insertPPScoreID = insertPPCommand.Parameters.Add("insertId", MySqlDbType.Int64);
                var insertPPvalue = insertPPCommand.Parameters.Add("pp", MySqlDbType.Float);

                insertPPCommand.Prepare();

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

                        // Convert to new score format
                        var soloScore = new SoloScore
                        {
                            user_id = highScore.user_id,
                            beatmap_id = highScore.beatmap_id,
                            ruleset_id = RulesetId,
                            preserve = true,
                            ScoreInfo = new SoloScoreInfo
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
                            },
                            created_at = highScore.date,
                            updated_at = highScore.date,
                        };

                        long insertId = db.Insert(soloScore, transaction);

                        insertPPScoreID.Value = insertId;
                        insertPPvalue.Value = highScore.pp;
                        insertPPCommand.Transaction = transaction;

                        insertPPCommand.ExecuteNonQuery();

                        Interlocked.Increment(ref currentTransactionInsertCount);

                        long currentTimestamp = DateTimeOffset.Now.ToUnixTimeSeconds();

                        if (lastCommitTimestamp != currentTimestamp)
                        {
                            transaction.Commit();

                            int inserted = Interlocked.Exchange(ref currentTransactionInsertCount, 0);

                            Console.WriteLine($"Written up to old:{highScore.score_id} new:{insertId} ({inserted}/s)");

                            transaction = db.BeginTransaction();
                            lastCommitTimestamp = currentTimestamp;
                        }

                        // update StartId to allow the next bulk query to start from the correct location.
                        StartId = highScore.score_id;
                    }
                }

                transaction.Commit();
            }

            return 0;
        }

        private (double accuracy, Dictionary<HitResult, int> statistics) getAccuracyAndStatistics(Ruleset ruleset, HighScore highScore)
        {
            var scoreInfo = new ScoreInfo
            {
                Ruleset = ruleset.RulesetInfo,
                RulesetID = RulesetId,
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
            public short maxcombo { get; set; }
            public string rank { get; set; } = null!; // Actually a ScoreRank, but reading as a string for manual parsing.
            public short count50 { get; set; }
            public short count100 { get; set; }
            public short count300 { get; set; }
            public short countmiss { get; set; }
            public short countgeki { get; set; }
            public short countkatu { get; set; }
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
