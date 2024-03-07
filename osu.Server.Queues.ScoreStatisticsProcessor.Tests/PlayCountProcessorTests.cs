using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using osu.Game.Rulesets.Scoring;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class PlayCountProcessorTests : DatabaseTest
    {
        private const int beatmap_length = 158;

        public PlayCountProcessorTests()
        {
            AddBeatmap(b => b.total_length = beatmap_length);
        }

        [Fact]
        public void TestPlaycountIncreaseMania()
        {
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats_mania WHERE user_id = 2", (int?)null, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore(3));
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats_mania WHERE user_id = 2", 1, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore(3));
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats_mania WHERE user_id = 2", 2, CancellationToken);
        }

        [Fact]
        public Task TestGlobalPlaycountsIncrement()
        {
            const int attempt_count = 100;

            var cts = new CancellationTokenSource();

            var incrementTask = Task.Run(() =>
            {
                for (int i = 0; i < attempt_count; i++)
                {
                    if (cts.IsCancellationRequested)
                        break;

                    int offset = i - attempt_count;
                    SetScoreForBeatmap(TEST_BEATMAP_ID, s => s.Score.ended_at = DateTimeOffset.Now.AddMinutes(offset));
                }
            }, cts.Token);

            WaitForDatabaseState("SELECT IF(count > 0, 1, 0) FROM osu_counts WHERE name = 'playcount'", 1, CancellationToken);
            WaitForDatabaseState($"SELECT IF(playcount > 0, 1, 0) FROM osu_beatmaps WHERE beatmap_id = {TEST_BEATMAP_ID}", 1, CancellationToken);
            WaitForDatabaseState($"SELECT IF(play_count > 0, 1, 0) FROM osu_beatmapsets WHERE beatmapset_id = {TEST_BEATMAP_SET_ID}", 1, CancellationToken);

            cts.Cancel();

            return incrementTask;
        }

        [Fact]
        public Task TestGlobalPassCountsIncrementOnPass()
        {
            const int attempt_count = 100;

            var cts = new CancellationTokenSource();

            var incrementTask = Task.Run(() =>
            {
                for (int i = 0; i < attempt_count; i++)
                {
                    if (cts.IsCancellationRequested)
                        break;

                    int offset = i - attempt_count;
                    SetScoreForBeatmap(TEST_BEATMAP_ID, s => s.Score.ended_at = DateTimeOffset.Now.AddMinutes(offset));
                }
            }, cts.Token);

            WaitForDatabaseState($"SELECT IF(passcount > 0, 1, 0) FROM osu_beatmaps WHERE beatmap_id = {TEST_BEATMAP_ID}", 1, CancellationToken);

            cts.Cancel();

            return incrementTask;
        }

        [Fact]
        public Task TestGlobalPassCountsDoNotIncrementOnFail()
        {
            const int attempt_count = 100;

            var cts = new CancellationTokenSource();

            var incrementTask = Task.Run(() =>
            {
                for (int i = 0; i < attempt_count; i++)
                {
                    if (cts.IsCancellationRequested)
                        break;

                    int offset = i - attempt_count;
                    SetScoreForBeatmap(TEST_BEATMAP_ID, s =>
                    {
                        s.Score.passed = false;
                        s.Score.ended_at = DateTimeOffset.Now.AddMinutes(offset);
                    });
                }
            }, cts.Token);

            WaitForDatabaseState($"SELECT IF(passcount > 0, 1, 0) FROM osu_beatmaps WHERE beatmap_id = {TEST_BEATMAP_ID}", 0, CancellationToken);

            cts.Cancel();

            return incrementTask;
        }

        [Fact]
        public void TestPlaycountIncrease()
        {
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 1, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 2, CancellationToken);
        }

        [Fact]
        public void TestPlaycountDoesNotIncreaseIfFailedAndPlayTooShort()
        {
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            var score = CreateTestScore();
            score.Score.ended_at = score.Score.started_at!.Value + TimeSpan.FromSeconds(4);
            score.Score.passed = false;

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 0, CancellationToken);
        }

        [Fact]
        public void TestPlaycountDoesNotIncreaseIfFailedAndScoreTooLow()
        {
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            var score = CreateTestScore();
            score.Score.total_score = 20;
            score.Score.passed = false;

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 0, CancellationToken);
        }

        [Theory]
        [InlineData(3, 40)]
        [InlineData(9, 100)]
        [InlineData(19, 200)]
        [InlineData(19, 500)]
        public void TestPlaycountDoesNotIncreaseIfFailedAndTooFewObjectsHit(int hitCount, int totalCount)
        {
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            var score = CreateTestScore();
            score.Score.ScoreData.Statistics = new Dictionary<HitResult, int> { [HitResult.Great] = hitCount };
            score.Score.ScoreData.MaximumStatistics = new Dictionary<HitResult, int> { [HitResult.Great] = totalCount };
            score.Score.passed = false;

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 0, CancellationToken);
        }

        [Fact]
        public void TestPlaycountDoesIncreaseIfPassedAndPlayShort()
        {
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            var score = CreateTestScore();
            score.Score.ended_at = score.Score.started_at!.Value + TimeSpan.FromSeconds(4);

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 1, CancellationToken);
        }

        [Fact]
        public void TestProcessingSameScoreTwiceRaceCondition()
        {
            IgnoreProcessorExceptions();

            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            var score = CreateTestScore();

            Processor.PushToQueue(score);
            Processor.PushToQueue(score);
            Processor.PushToQueue(score);
            Processor.PushToQueue(score);
            Processor.PushToQueue(score);

            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 1, CancellationToken);

            WaitForTotalProcessed(5, CancellationToken);

            // check only one score was counted, even though many were pushed.
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 1, CancellationToken);
        }

        [Fact]
        public void TestPlaycountReprocessDoesntIncrease()
        {
            var score = CreateTestScore();

            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 1, CancellationToken);

            // the score will be marked as processed (in the database) at this point, so should not increase the playcount if processed a second time.
            score.MarkProcessed();

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 1, CancellationToken);
        }

        [Fact]
        public void TestRapidPlaysHasLimitedIncrease()
        {
            WaitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {TEST_BEATMAP_ID}", (int?)null, CancellationToken);

            for (int i = 0; i < 30; i++)
                SetScoreForBeatmap(TEST_BEATMAP_ID);

            WaitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {TEST_BEATMAP_ID}", 12, CancellationToken);
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 12, CancellationToken);
        }

        [Fact]
        public void TestUserBeatmapPlaycountIncrease()
        {
            WaitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {TEST_BEATMAP_ID}", (int?)null, CancellationToken);

            SetScoreForBeatmap(TEST_BEATMAP_ID);
            WaitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {TEST_BEATMAP_ID}", 1, CancellationToken);

            SetScoreForBeatmap(TEST_BEATMAP_ID);
            WaitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {TEST_BEATMAP_ID}", 2, CancellationToken);
        }

        [Fact]
        public void TestUserBeatmapPlaycountReprocessDoesntIncrease()
        {
            var score = CreateTestScore();

            WaitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {TEST_BEATMAP_ID}", (int?)null, CancellationToken);

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {TEST_BEATMAP_ID}", 1, CancellationToken);

            // the score will be marked as processed (in the database) at this point, so should not increase the playcount if processed a second time.
            score.MarkProcessed();

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {TEST_BEATMAP_ID}", 1, CancellationToken);
        }

        [Fact]
        public void TestMonthlyPlaycountIncrease()
        {
            checkPlaycount(null);

            PushToQueueAndWaitForProcess(CreateTestScore());
            checkPlaycount(1);

            PushToQueueAndWaitForProcess(CreateTestScore());
            checkPlaycount(2);

            WaitForDatabaseState("SELECT COUNT(*) FROM osu_user_month_playcount WHERE user_id = 2", 1, CancellationToken);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void TestMonthlyPlaycountReprocessOldVersionIncrease(int version)
        {
            var score = CreateTestScore();

            checkPlaycount(null);
            PushToQueueAndWaitForProcess(score);

            score.MarkProcessed();

            // check reprocessing results in increase.
            Debug.Assert(score.ProcessHistory != null);
            score.ProcessHistory.processed_version = (byte)version;

            PushToQueueAndWaitForProcess(score);
            checkPlaycount(2);
        }

        [Fact]
        public void TestMonthlyPlaycountReprocessDoesntIncrease()
        {
            var score = CreateTestScore();

            checkPlaycount(null);

            PushToQueueAndWaitForProcess(score);
            checkPlaycount(1);

            // the score will be marked as processed (in the database) at this point, so should not increase the playcount if processed a second time.
            score.MarkProcessed();

            PushToQueueAndWaitForProcess(score);
            checkPlaycount(1);
            WaitForDatabaseState("SELECT COUNT(*) FROM osu_user_month_playcount WHERE user_id = 2", 1, CancellationToken);
        }

        private void checkPlaycount(int? expected)
        {
            var now = DateTimeOffset.Now;
            string yearMonth = $"{now.Year % 2000}{now.Month:00}";

            WaitForDatabaseState($"SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2 AND `year_month` = '{yearMonth}'", expected, CancellationToken);
        }
    }
}
