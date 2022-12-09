using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class TotalScoreProcessorTests : DatabaseTest
    {
        [Fact]
        public void TestTotalScoreIncrease()
        {
            AddBeatmap();

            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 100000, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 200000, CancellationToken);
        }

        [Fact]
        public void TestTotalScoreForLegacyScoreDoesntIncrease()
        {
            AddBeatmap();

            var score = CreateTestScore();

            score.Score.ScoreInfo.LegacyScoreId = 1234;

            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            Processor.PushToQueue(score);
            WaitForTotalProcessed(1, CancellationToken);

            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);
        }

        [Fact]
        public void TestTotalScoreReprocessDoesntIncrease()
        {
            AddBeatmap();

            var score = CreateTestScore();

            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 100000, CancellationToken);

            score.MarkProcessed();

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 100000, CancellationToken);
        }
    }
}
