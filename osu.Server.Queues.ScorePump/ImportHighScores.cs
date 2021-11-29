// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Dapper;
using Dapper.Contrib.Extensions;
using McMaster.Extensions.CommandLineUtils;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Server.Queues.ScoreStatisticsProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScorePump
{
    [Command("import-high-scores", Description = "Imports high scores from the osu_scores_high tables into solo_scores.")]
    public class ImportHighScores : ScorePump
    {
        [Option(CommandOptionType.SingleValue)]
        public int RulesetId { get; set; }

        [Option(CommandOptionType.SingleValue)]
        public long StartId { get; set; }

        public int OnExecute(CancellationToken cancellationToken)
        {
            using (var dbMainQuery = Queue.GetDatabaseConnection())
            using (var db = Queue.GetDatabaseConnection())
            {
                Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(RulesetId);
                string highScoreTable = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId).HighScoreTable;
                string query = $"SELECT * FROM {highScoreTable} WHERE score_id >= @startId";

                Console.WriteLine($"Querying with \"{query}\"");

                var highScores = dbMainQuery.Query<HighScore>(query, new { startId = StartId }, buffered: false);

                foreach (var highScore in highScores)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    Console.WriteLine($"Reading score {highScore.score_id}");

                    // Only used to calculate accuracy and statistics.
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

                    // Convert to new score format
                    var soloScore = new SoloScore
                    {
                        user_id = highScore.user_id,
                        beatmap_id = highScore.beatmap_id,
                        ruleset_id = RulesetId,
                        preserve = true,
                        ScoreInfo = new SoloScoreInfo
                        {
                            // TODO: this is going to conflict between rulesets and also against existing lazer scores. needs some thought before running against production.
                            id = highScore.score_id,
                            user_id = highScore.user_id,
                            beatmap_id = highScore.beatmap_id,
                            ruleset_id = RulesetId,
                            passed = true,
                            total_score = highScore.score,
                            accuracy = scoreInfo.Accuracy,
                            max_combo = highScore.maxcombo,
                            rank = highScore.rank,
                            mods = ruleset.ConvertFromLegacyMods((LegacyMods)highScore.enabled_mods).Select(m => new APIMod(m)).ToList(),
                            statistics = scoreInfo.Statistics,
                            started_at = highScore.date,
                            ended_at = highScore.date,
                            created_at = highScore.date,
                            updated_at = highScore.date
                        },
                        created_at = highScore.date,
                        updated_at = highScore.date,
                    };

                    // Todo: Import highScore.hidden somehow?

                    using (var transaction = db.BeginTransaction())
                    {
                        soloScore.id = db.Insert(soloScore, transaction);

                        // Update data to match the row ID.
                        db.Execute("UPDATE solo_scores s SET s.data = JSON_SET(s.data, '$.id', s.id) WHERE s.id = @id", soloScore, transaction);

                        db.Execute("INSERT INTO solo_scores_performance (score_id, pp) VALUES (@scoreId, @pp)", new
                        {
                            scoreId = soloScore.id,
                            highScore.pp
                        }, transaction);

                        transaction.Commit();
                    }
                }
            }

            return 0;
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
            public ScoreRank rank { get; set; }
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
