using System.Diagnostics;
using System.Threading.Tasks;
using Dapper.Contrib.Extensions;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
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
            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 10081, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 20162, CancellationToken);
        }

        [Fact]
        public void TestLevelIncrease()
        {
            AddBeatmap();

            WaitForDatabaseState("SELECT level FROM osu_user_stats WHERE user_id = 2", (float?)null, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT level FROM osu_user_stats WHERE user_id = 2", 1, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT level FROM osu_user_stats WHERE user_id = 2", 2, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            PushToQueueAndWaitForProcess(CreateTestScore());
            PushToQueueAndWaitForProcess(CreateTestScore());
            PushToQueueAndWaitForProcess(CreateTestScore());
            PushToQueueAndWaitForProcess(CreateTestScore());
            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT level FROM osu_user_stats WHERE user_id = 2", 3, CancellationToken);
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

            // partial processing is actually expected to happen here (for pp), but the user's total should still be zero.
            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 0, CancellationToken);
        }

        [Fact]
        public void TestTotalScoreReprocessDoesntIncrease()
        {
            AddBeatmap();

            var score = CreateTestScore();

            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 10081, CancellationToken);

            score.MarkProcessed();

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 10081, CancellationToken);
        }

        [Fact]
        public async Task TestTotalScoreReprocessCorrectlyHandlesSwitchFromStandardisedToClassic()
        {
            AddBeatmap();

            var score = CreateTestScore();

            // crudely and manually simulate score being processed on an earlier version, when we weren't using standardised scoring yet.
            // we're doing this manually, since we don't actually _maintain_ the old version of forward processing
            // anymore given current code structure, just the rollback code.
            score.ProcessHistory = new ProcessHistory
            {
                score_id = (long)score.Score.id,
                processed_version = 2
            };

            using (var db = Processor.GetDatabaseConnection())
            using (var transaction = await db.BeginTransactionAsync())
            {
                var userStats = await DatabaseHelper.GetUserStatsAsync(score.Score.ScoreInfo, db, transaction);

                Debug.Assert(userStats != null);
                userStats.total_score = score.Score.ScoreInfo.TotalScore;
                await DatabaseHelper.UpdateUserStatsAsync(userStats, db, transaction);

                await db.InsertAsync(score.ProcessHistory, transaction);

                await transaction.CommitAsync();
            }

            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 100000, CancellationToken);

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT total_score FROM osu_user_stats WHERE user_id = 2", 10081, CancellationToken);
        }
    }
}
