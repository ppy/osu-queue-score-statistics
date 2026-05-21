using System;
using System.Collections.Generic;
using System.Diagnostics;
using osu.Game.Rulesets.Scoring;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class HitStatisticsProcessorTests : DatabaseTest
    {
        public HitStatisticsProcessorTests()
        {
            AddBeatmap();
        }

        [Fact]
        public void TestHitStatisticsIncrease()
        {
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 5, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 10, CancellationToken);
        }

        [Fact]
        public void TestHitStatisticsReprocessOldVersionIncrease()
        {
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
        public void TestDoesNotIncreaseIfFailedAndPlayTooShort()
        {
            var score = CreateTestScore();

            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);
            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 5, CancellationToken);

            score = CreateTestScore();
            score.Score.ended_at = score.Score.started_at!.Value + TimeSpan.FromSeconds(4);
            score.Score.passed = false;

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 5, CancellationToken);
        }

        [Fact]
        public void TestHitStatisticsReprocessDoesntIncrease()
        {
            var score = CreateTestScore();

            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);
            PushToQueueAndWaitForProcess(score);

            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 5, CancellationToken);

            // the score will be marked as processed (in the database) at this point, so should not increase the playcount if processed a second time.
            score.MarkProcessed();

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats WHERE user_id = 2", 5, CancellationToken);
        }

        [Fact]
        public void TestHitStatisticsIncreaseOnCatchTickHits()
        {
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats_fruits WHERE user_id = 2", (int?)null, CancellationToken);

            var testScore = CreateTestScore(rulesetId: 2);
            testScore.Score.ScoreData.Statistics = new Dictionary<HitResult, int>
            {
                [HitResult.Great] = 100,
                [HitResult.LargeTickHit] = 20,
                [HitResult.SmallTickHit] = 40,
                [HitResult.LargeTickMiss] = 1,
                [HitResult.SmallTickMiss] = 2,
            };

            PushToQueueAndWaitForProcess(testScore);
            WaitForDatabaseState("SELECT count300, count100, count50, countmiss FROM osu_user_stats_fruits WHERE user_id = 2", (100, 20, 40, 1), CancellationToken);
        }
    }
}
