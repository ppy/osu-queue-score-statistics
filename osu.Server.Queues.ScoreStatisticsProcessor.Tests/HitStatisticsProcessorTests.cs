using System.Diagnostics;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class HitStatisticsProcessorTests : DatabaseTest
    {
        [Fact]
        public void TestHitStatisticsIncrease()
        {
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            Processor.PushToQueue(CreateTestScore());
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 5, CancellationToken);

            Processor.PushToQueue(CreateTestScore());
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 10, CancellationToken);
        }

        [Fact]
        public void TestHitStatisticsReprocessOldVersionIncrease()
        {
            var score = CreateTestScore();

            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);
            Processor.PushToQueue(score);

            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 5, CancellationToken);

            score.MarkProcessed();

            // intentionally set to an older version to make sure it doesn't revert hit statistics.
            Debug.Assert(score.ProcessHistory != null);
            score.ProcessHistory.processed_version = 1;

            Processor.PushToQueue(score);
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 10, CancellationToken);
        }

        [Fact]
        public void TestHitStatisticsReprocessDoesntIncrease()
        {
            var score = CreateTestScore();

            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);
            Processor.PushToQueue(score);

            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 5, CancellationToken);

            // the score will be marked as processed (in the database) at this point, so should not increase the playcount if processed a second time.
            score.MarkProcessed();

            Processor.PushToQueue(score);
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 5, CancellationToken);
        }
    }
}
