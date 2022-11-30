using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class TotalScoreProcessorTests : DatabaseTest
    {
        [Fact]
        public void TestTotalScoreIncrease()
        {
            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 100000, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 200000, CancellationToken);
        }

        [Fact]
        public void TestTotalScoreReprocessDoesntIncrease()
        {
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
