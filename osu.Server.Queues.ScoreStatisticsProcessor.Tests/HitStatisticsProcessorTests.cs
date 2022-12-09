using System.Diagnostics;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class HitStatisticsProcessorTests : DatabaseTest
    {
        [Fact]
        public void TestHitStatisticsIncrease()
        {
            AddBeatmap();

            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 5, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 10, CancellationToken);
        }

        [Fact]
        public void TestHitStatisticsReprocessOldVersionIncrease()
        {
            AddBeatmap();

            var score = CreateTestScore();

            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);
            PushToQueueAndWaitForProcess(score);

            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 5, CancellationToken);

            score.MarkProcessed();

            // intentionally set to an older version to make sure it doesn't revert hit statistics.
            Debug.Assert(score.ProcessHistory != null);
            score.ProcessHistory.processed_version = 1;

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 10, CancellationToken);
        }

        [Fact]
        public void TestHitStatisticsReprocessDoesntIncrease()
        {
            AddBeatmap();

            var score = CreateTestScore();

            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);
            PushToQueueAndWaitForProcess(score);

            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 5, CancellationToken);

            // the score will be marked as processed (in the database) at this point, so should not increase the playcount if processed a second time.
            score.MarkProcessed();

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 5, CancellationToken);
        }
    }
}
