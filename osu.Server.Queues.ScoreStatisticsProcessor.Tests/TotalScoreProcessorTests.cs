using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class TotalScoreProcessorTests : DatabaseTest
    {
        [Fact]
        public void TestTotalScoreIncrease()
        {
            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            Processor.PushToQueue(CreateTestScore());
            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 100000, CancellationToken);

            Processor.PushToQueue(CreateTestScore());
            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 200000, CancellationToken);
        }

        [Fact]
        public void TestTotalScoreReprocessDoesntIncrease()
        {
            var score = CreateTestScore();

            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            Processor.PushToQueue(score);
            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 100000, CancellationToken);

            score.MarkProcessed();

            Processor.PushToQueue(score);
            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 100000, CancellationToken);
        }
    }
}
