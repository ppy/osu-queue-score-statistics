using Dapper;
using osu.Game.Online.API;
using osu.Game.Rulesets.Osu.Mods;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class MaxComboProcessorTests : DatabaseTest
    {
        [Fact]
        public void TestMaxComboIncrease()
        {
            AddBeatmap();

            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", MAX_COMBO, CancellationToken);

            var score = CreateTestScore();
            score.Score.max_combo++;

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", MAX_COMBO + 1, CancellationToken);
        }

        [Fact]
        public void TestMaxComboDoesntIncreaseIfNotRanked()
        {
            AddBeatmap();

            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", MAX_COMBO, CancellationToken);

            try
            {
                using (var db = Processor.GetDatabaseConnection())
                    db.Execute($"UPDATE osu_beatmaps SET approved = 0 WHERE beatmap_id = {TEST_BEATMAP_ID}");

                var testScore = CreateTestScore();
                testScore.Score.max_combo++;

                PushToQueueAndWaitForProcess(testScore);
                WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", MAX_COMBO, CancellationToken);
            }
            finally
            {
                using (var db = Processor.GetDatabaseConnection())
                    db.Execute($"UPDATE osu_beatmaps SET approved = 1 WHERE beatmap_id = {TEST_BEATMAP_ID}");
            }
        }

        [Fact]
        public void TestMaxComboDoesntIncreaseIfLower()
        {
            AddBeatmap();

            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            PushToQueueAndWaitForProcess(CreateTestScore());
            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", MAX_COMBO, CancellationToken);

            var score = CreateTestScore();
            score.Score.max_combo--;

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", MAX_COMBO, CancellationToken);
        }

        [Fact]
        public void TestMaxComboDoesntIncreaseIfAutomationMod()
        {
            AddBeatmap();

            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            var score = CreateTestScore();
            score.Score.max_combo++;
            score.Score.ScoreData.Mods = new[]
            {
                new APIMod(new OsuModRelax()),
            };

            PushToQueueAndWaitForProcess(score);

            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", 0, CancellationToken);
        }

        [Fact]
        public void FailedScoreDoesNotProcess()
        {
            AddBeatmap();

            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", (int?)null, CancellationToken);

            SetScoreForBeatmap(TEST_BEATMAP_ID, s => s.Score.passed = false);

            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", 0, CancellationToken);
        }
    }
}
