using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class PlayCountProcessorTests : DatabaseTest
    {
        [Fact]
        public void TestPlaycountIncreaseMania()
        {
            AddBeatmap();

            WaitForDatabaseState("SELECT playcount FROM osu_user_stats_mania WHERE user_id = 2", (int?)null, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore(3));
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats_mania WHERE user_id = 2", 1, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore(3));
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats_mania WHERE user_id = 2", 2, CancellationToken);
        }

        [Fact]
        public void TestGlobalPlaycountsIncrement()
        {
            const int attempt_count = 100;

            AddBeatmap();

            Task.Run(() =>
            {
                for (int i = 0; i < attempt_count; i++)
                {
                    int offset = i - attempt_count;
                    SetScoreForBeatmap(TEST_BEATMAP_ID, s => s.Score.created_at = DateTimeOffset.Now.AddMinutes(offset));
                }
            });

            WaitForDatabaseState("SELECT IF(count > 0, 1, 0) FROM osu_counts WHERE name = 'playcount'", 1, CancellationToken);
            WaitForDatabaseState($"SELECT IF(playcount > 0, 1, 0) FROM osu_beatmaps WHERE beatmap_id = {TEST_BEATMAP_ID}", 1, CancellationToken);
            WaitForDatabaseState($"SELECT IF(play_count > 0, 1, 0) FROM osu_beatmapsets WHERE beatmapset_id = {TEST_BEATMAP_SET_ID}", 1, CancellationToken);
        }

        [Fact]
        public void TestPlaycountIncrease()
        {
            AddBeatmap();

            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 1, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 2, CancellationToken);
        }

        [Fact]
        public void TestProcessingSameScoreTwiceRaceCondition()
        {
            AddBeatmap();

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
            AddBeatmap();

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
            AddBeatmap();

            WaitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {TEST_BEATMAP_ID}", (int?)null, CancellationToken);

            for (int i = 0; i < 30; i++)
                SetScoreForBeatmap(TEST_BEATMAP_ID);

            WaitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {TEST_BEATMAP_ID}", 12, CancellationToken);
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 12, CancellationToken);
        }

        [Fact]
        public void TestUserBeatmapPlaycountIncrease()
        {
            AddBeatmap();

            WaitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {TEST_BEATMAP_ID}", (int?)null, CancellationToken);

            SetScoreForBeatmap(TEST_BEATMAP_ID);
            WaitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {TEST_BEATMAP_ID}", 1, CancellationToken);

            SetScoreForBeatmap(TEST_BEATMAP_ID);
            WaitForDatabaseState($"SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 and beatmap_id = {TEST_BEATMAP_ID}", 2, CancellationToken);
        }

        [Fact]
        public void TestUserBeatmapPlaycountReprocessDoesntIncrease()
        {
            AddBeatmap();

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
            AddBeatmap();

            WaitForDatabaseState("SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2", (int?)null, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2 AND `year_month` = '2002'", 1, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2 AND `year_month` = '2002'", 2, CancellationToken);
            WaitForDatabaseState("SELECT COUNT(*) FROM osu_user_month_playcount WHERE user_id = 2", 1, CancellationToken);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void TestMonthlyPlaycountReprocessOldVersionIncrease(int version)
        {
            AddBeatmap();

            var score = CreateTestScore();

            WaitForDatabaseState("SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2", (int?)null, CancellationToken);
            PushToQueueAndWaitForProcess(score);

            score.MarkProcessed();

            // check reprocessing results in increase.
            Debug.Assert(score.ProcessHistory != null);
            score.ProcessHistory.processed_version = (byte)version;

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2 AND `year_month` = '2002'", 2, CancellationToken);
        }

        [Fact]
        public void TestMonthlyPlaycountReprocessDoesntIncrease()
        {
            AddBeatmap();

            var score = CreateTestScore();

            WaitForDatabaseState("SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2", (int?)null, CancellationToken);

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2 AND `year_month` = '2002'", 1, CancellationToken);

            // the score will be marked as processed (in the database) at this point, so should not increase the playcount if processed a second time.
            score.MarkProcessed();

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2 AND `year_month` = '2002'", 1, CancellationToken);
            WaitForDatabaseState("SELECT COUNT(*) FROM osu_user_month_playcount WHERE user_id = 2", 1, CancellationToken);
        }
    }
}
