using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Xunit;
using Xunit.Sdk;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class StatisticsUpdateTests : IDisposable
    {
        private readonly ScoreStatisticsProcessor processor;

        private readonly CancellationTokenSource cts = new CancellationTokenSource(10000);

        private const int max_combo = 1337;

        private const int test_beatmap_id = 172;

        public StatisticsUpdateTests()
        {
            processor = new ScoreStatisticsProcessor();
            processor.ClearQueue();

            using (var db = processor.GetDatabaseConnection())
            {
                // just a safety measure for now to ensure we don't hit production. since i was running on production until now.
                // will throw if not on test database.
                db.Query<int>("SELECT * FROM test_database");

                db.Execute("TRUNCATE TABLE osu_user_stats");
                db.Execute("TRUNCATE TABLE osu_user_stats_mania");
                db.Execute("TRUNCATE TABLE osu_user_beatmap_playcount");
                db.Execute("TRUNCATE TABLE osu_user_month_playcount");
                db.Execute($"TRUNCATE TABLE {SoloScore.TABLE_NAME}");
                db.Execute($"TRUNCATE TABLE {ProcessHistory.TABLE_NAME}");
                db.Execute($"TRUNCATE TABLE {SoloScorePerformance.TABLE_NAME}");
            }

            Task.Run(() => processor.Run(cts.Token), cts.Token);
        }

        [Fact]
        public void TestPlaycountIncreaseMania()
        {
            waitForDatabaseState("SELECT playcount FROM osu_user_stats_mania WHERE user_id = 2", (int?)null, cts.Token);

            processor.PushToQueue(CreateTestScore(3));
            waitForDatabaseState("SELECT playcount FROM osu_user_stats_mania WHERE user_id = 2", 1, cts.Token);

            processor.PushToQueue(CreateTestScore(3));
            waitForDatabaseState("SELECT playcount FROM osu_user_stats_mania WHERE user_id = 2", 2, cts.Token);
        }

        [Fact]
        public void TestPlaycountIncrease()
        {
            waitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", (int?)null, cts.Token);

            processor.PushToQueue(CreateTestScore());
            waitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 1, cts.Token);

            processor.PushToQueue(CreateTestScore());
            waitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 2, cts.Token);
        }

        [Fact]
        public void TestPlayTimeIncrease()
        {
            waitForDatabaseState("SELECT total_seconds_played FROM osu_user_stats WHERE user_id = 2", (int?)null, cts.Token);

            // Beatmap used in test score is 158 seconds.

            var testScore = CreateTestScore();
            testScore.Score.ScoreInfo.EndedAt = testScore.Score.ScoreInfo.StartedAt!.Value + TimeSpan.FromSeconds(50);

            processor.PushToQueue(testScore);
            waitForDatabaseState("SELECT total_seconds_played FROM osu_user_stats WHERE user_id = 2", 50, cts.Token);

            testScore = CreateTestScore();
            testScore.Score.ScoreInfo.EndedAt = testScore.Score.ScoreInfo.StartedAt!.Value + TimeSpan.FromSeconds(100);

            processor.PushToQueue(testScore);
            waitForDatabaseState("SELECT total_seconds_played FROM osu_user_stats WHERE user_id = 2", 150, cts.Token);
        }

        [Fact]
        public void TestPlayTimeIncreaseHigherThanBeatmapLength()
        {
            waitForDatabaseState("SELECT total_seconds_played FROM osu_user_stats WHERE user_id = 2", (int?)null, cts.Token);

            // Beatmap used in test score is 158 seconds.

            var testScore = CreateTestScore();
            testScore.Score.ScoreInfo.EndedAt = testScore.Score.ScoreInfo.StartedAt!.Value + TimeSpan.FromSeconds(200);

            processor.PushToQueue(testScore);
            waitForDatabaseState("SELECT total_seconds_played FROM osu_user_stats WHERE user_id = 2", 158, cts.Token);
        }

        [Fact]
        public void TestPlayTimeIncreaseHigherThanBeatmapLengthWithModApplication()
        {
            waitForDatabaseState("SELECT total_seconds_played FROM osu_user_stats WHERE user_id = 2", (int?)null, cts.Token);

            // Beatmap used in test score is 158 seconds.
            // Double time means this is reduced to 105 seconds.

            var testScore = CreateTestScore();
            testScore.Score.ScoreInfo.Mods = new[]
            {
                new APIMod(new OsuModDoubleTime()),
            };
            testScore.Score.ScoreInfo.EndedAt = testScore.Score.ScoreInfo.StartedAt!.Value + TimeSpan.FromSeconds(200);

            processor.PushToQueue(testScore);
            waitForDatabaseState("SELECT total_seconds_played FROM osu_user_stats WHERE user_id = 2", 105, cts.Token);
        }

        [Fact]
        public void TestPlayTimeIncreaseHigherThanBeatmapLengthWithModApplicationCustomRate()
        {
            waitForDatabaseState("SELECT total_seconds_played FROM osu_user_stats WHERE user_id = 2", (int?)null, cts.Token);

            // Beatmap used in test score is 158 seconds.
            // Double time with custom rate means this is reduced to 112 seconds.

            var testScore = CreateTestScore();
            testScore.Score.ScoreInfo.Mods = new[]
            {
                new APIMod(new OsuModDoubleTime { SpeedChange = { Value = 1.4 } }),
            };
            testScore.Score.ScoreInfo.EndedAt = testScore.Score.ScoreInfo.StartedAt!.Value + TimeSpan.FromSeconds(200);

            processor.PushToQueue(testScore);
            waitForDatabaseState("SELECT total_seconds_played FROM osu_user_stats WHERE user_id = 2", 112, cts.Token);
        }

        [Fact]
        public void TestProcessingSameScoreTwiceRaceCondition()
        {
            waitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", (int?)null, cts.Token);

            var score = CreateTestScore();

            processor.PushToQueue(score);
            processor.PushToQueue(score);
            processor.PushToQueue(score);
            processor.PushToQueue(score);
            processor.PushToQueue(score);

            waitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 1, cts.Token);

            waitForTotalProcessed(5, cts.Token);

            // check only one score was counted, even though many were pushed.
            waitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 1, cts.Token);
        }

        [Fact]
        public void TestPlaycountReprocessDoesntIncrease()
        {
            var score = CreateTestScore();

            waitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", (int?)null, cts.Token);

            processor.PushToQueue(score);
            waitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 1, cts.Token);

            // the score will be marked as processed (in the database) at this point, so should not increase the playcount if processed a second time.
            score.MarkProcessed();

            processor.PushToQueue(score);
            waitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 1, cts.Token);
        }

        [Fact]
        public void TestMaxComboIncrease()
        {
            waitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", (int?)null, cts.Token);

            processor.PushToQueue(CreateTestScore());
            waitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", max_combo, cts.Token);

            var score = CreateTestScore();
            score.Score.ScoreInfo.MaxCombo++;

            processor.PushToQueue(score);
            waitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", max_combo + 1, cts.Token);
        }

        [Fact]
        public void TestMaxComboDoesntIncreaseIfNotRanked()
        {
            waitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", (int?)null, cts.Token);

            processor.PushToQueue(CreateTestScore());
            waitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", max_combo, cts.Token);

            try
            {
                using (var db = processor.GetDatabaseConnection())
                    db.Execute($"UPDATE osu_beatmaps SET approved = 0 WHERE beatmap_id = {test_beatmap_id}");

                var testScore = CreateTestScore();
                testScore.Score.ScoreInfo.MaxCombo++;

                processor.PushToQueue(testScore);
                waitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", max_combo, cts.Token);
            }
            finally
            {
                using (var db = processor.GetDatabaseConnection())
                    db.Execute($"UPDATE osu_beatmaps SET approved = 1 WHERE beatmap_id = {test_beatmap_id}");
            }
        }

        [Fact]
        public void TestMaxComboDoesntIncreaseIfLower()
        {
            waitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", (int?)null, cts.Token);

            processor.PushToQueue(CreateTestScore());
            waitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", max_combo, cts.Token);

            var score = CreateTestScore();
            score.Score.ScoreInfo.MaxCombo--;

            processor.PushToQueue(score);
            waitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", max_combo, cts.Token);
        }

        [Fact]
        public void TestMaxComboDoesntIncreaseIfAutomationMod()
        {
            waitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", (int?)null, cts.Token);

            var score = CreateTestScore();
            score.Score.ScoreInfo.MaxCombo++;
            score.Score.ScoreInfo.Mods = new[]
            {
                new APIMod(new OsuModRelax()),
            };

            // Due to how the waiting for database test works, we can't check, for null.
            // Therefore push a non-automated score *after* the automated score, and ensure the combo matches the second.
            processor.PushToQueue(score);
            processor.PushToQueue(CreateTestScore());

            waitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", max_combo, cts.Token);
        }

        [Fact]
        public void TestUserBeatmapPlaycountIncrease()
        {
            waitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {test_beatmap_id}", (int?)null, cts.Token);

            processor.PushToQueue(CreateTestScore());
            waitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {test_beatmap_id}", 1, cts.Token);

            processor.PushToQueue(CreateTestScore());
            waitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {test_beatmap_id}", 2, cts.Token);
        }

        [Fact]
        public void TestUserBeatmapPlaycountReprocessDoesntIncrease()
        {
            var score = CreateTestScore();

            waitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {test_beatmap_id}", (int?)null, cts.Token);

            processor.PushToQueue(score);
            waitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {test_beatmap_id}", 1, cts.Token);

            // the score will be marked as processed (in the database) at this point, so should not increase the playcount if processed a second time.
            score.MarkProcessed();

            processor.PushToQueue(score);
            waitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {test_beatmap_id}", 1, cts.Token);
        }

        [Fact]
        public void TestMonthlyPlaycountIncrease()
        {
            waitForDatabaseState("SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2", (int?)null, cts.Token);

            processor.PushToQueue(CreateTestScore());
            waitForDatabaseState("SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2 AND `year_month` = '2002'", 1, cts.Token);

            processor.PushToQueue(CreateTestScore());
            waitForDatabaseState("SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2 AND `year_month` = '2002'", 2, cts.Token);
            waitForDatabaseState("SELECT COUNT(*) FROM osu_user_month_playcount WHERE user_id = 2", 1, cts.Token);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void TestMonthlyPlaycountReprocessOldVersionIncrease(int version)
        {
            var score = CreateTestScore();

            waitForDatabaseState("SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2", (int?)null, cts.Token);
            processor.PushToQueue(score);

            score.MarkProcessed();

            // check reprocessing results in increase.
            Debug.Assert(score.ProcessHistory != null);
            score.ProcessHistory.processed_version = (byte)version;

            processor.PushToQueue(score);
            waitForDatabaseState("SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2 AND `year_month` = '2002'", 2, cts.Token);
        }

        [Fact]
        public void TestMonthlyPlaycountReprocessDoesntIncrease()
        {
            var score = CreateTestScore();

            waitForDatabaseState("SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2", (int?)null, cts.Token);

            processor.PushToQueue(score);
            waitForDatabaseState("SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2 AND `year_month` = '2002'", 1, cts.Token);

            // the score will be marked as processed (in the database) at this point, so should not increase the playcount if processed a second time.
            score.MarkProcessed();

            processor.PushToQueue(score);
            waitForDatabaseState("SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2 AND `year_month` = '2002'", 1, cts.Token);
            waitForDatabaseState("SELECT COUNT(*) FROM osu_user_month_playcount WHERE user_id = 2", 1, cts.Token);
        }

        [Fact]
        public void TestTotalScoreIncrease()
        {
            waitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", (int?)null, cts.Token);

            processor.PushToQueue(CreateTestScore());
            waitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 100000, cts.Token);

            processor.PushToQueue(CreateTestScore());
            waitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 200000, cts.Token);
        }

        [Fact]
        public void TestTotalScoreReprocessDoesntIncrease()
        {
            var score = CreateTestScore();

            waitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", (int?)null, cts.Token);

            processor.PushToQueue(score);
            waitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 100000, cts.Token);

            score.MarkProcessed();

            processor.PushToQueue(score);
            waitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 100000, cts.Token);
        }

        [Fact]
        public void TestHitStatisticsIncrease()
        {
            waitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", (int?)null, cts.Token);

            processor.PushToQueue(CreateTestScore());
            waitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 5, cts.Token);

            processor.PushToQueue(CreateTestScore());
            waitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 10, cts.Token);
        }

        [Fact]
        public void TestHitStatisticsReprocessOldVersionIncrease()
        {
            var score = CreateTestScore();

            waitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", (int?)null, cts.Token);
            processor.PushToQueue(score);

            waitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 5, cts.Token);

            score.MarkProcessed();

            // intentionally set to an older version to make sure it doesn't revert hit statistics.
            Debug.Assert(score.ProcessHistory != null);
            score.ProcessHistory.processed_version = 1;

            processor.PushToQueue(score);
            waitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 10, cts.Token);
        }

        [Fact]
        public void TestHitStatisticsReprocessDoesntIncrease()
        {
            var score = CreateTestScore();

            waitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", (int?)null, cts.Token);
            processor.PushToQueue(score);

            waitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 5, cts.Token);

            // the score will be marked as processed (in the database) at this point, so should not increase the playcount if processed a second time.
            score.MarkProcessed();

            processor.PushToQueue(score);
            waitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 5, cts.Token);
        }

        private static ulong scoreIDSource;

        public static ScoreItem CreateTestScore(int rulesetId = 0)
        {
            var row = new SoloScore
            {
                id = Interlocked.Increment(ref scoreIDSource),
                user_id = 2,
                beatmap_id = test_beatmap_id,
                ruleset_id = rulesetId,
                created_at = DateTimeOffset.Now,
                updated_at = DateTimeOffset.Now,
            };

            var startTime = new DateTimeOffset(new DateTime(2020, 02, 05));
            var scoreInfo = new SoloScoreInfo
            {
                ID = row.id,
                UserID = row.user_id,
                BeatmapID = row.beatmap_id,
                RulesetID = row.ruleset_id,
                StartedAt = startTime,
                EndedAt = startTime + TimeSpan.FromSeconds(180),
                MaxCombo = max_combo,
                TotalScore = 100000,
                Rank = ScoreRank.S,
                Statistics =
                {
                    { HitResult.Perfect, 5 },
                    { HitResult.LargeBonus, 0 }
                },
                Passed = true
            };

            row.ScoreInfo = scoreInfo;

            return new ScoreItem(row);
        }

        private void waitForTotalProcessed(int count, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (processor.TotalProcessed == count)
                    return;

                Thread.Sleep(50);
            }

            throw new XunitException("All scores were not successfully processed");
        }

        private void waitForDatabaseState<T>(string sql, T expected, CancellationToken cancellationToken)
        {
            using (var db = processor.GetDatabaseConnection())
            {
                while (true)
                {
                    if (!Debugger.IsAttached)
                        cancellationToken.ThrowIfCancellationRequested();

                    var lastValue = db.QueryFirstOrDefault<T>(sql);
                    if ((expected == null && lastValue == null) || expected?.Equals(lastValue) == true)
                        return;

                    Thread.Sleep(50);
                }
            }
        }

#pragma warning disable CA1816
        public void Dispose()
#pragma warning restore CA1816
        {
            cts.Cancel();
        }
    }
}
