using Dapper;
using osu.Game.Online.API;
using osu.Game.Rulesets.Osu.Mods;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class MaxComboProcessorTests : StatisticsProcessorTest
    {
        [Fact]
        public void TestMaxComboIncrease()
        {
            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", (int?)null, Cts.Token);

            Processor.PushToQueue(CreateTestScore());
            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", MAX_COMBO, Cts.Token);

            var score = CreateTestScore();
            score.Score.ScoreInfo.MaxCombo++;

            Processor.PushToQueue(score);
            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", MAX_COMBO + 1, Cts.Token);
        }

        [Fact]
        public void TestMaxComboDoesntIncreaseIfNotRanked()
        {
            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", (int?)null, Cts.Token);

            Processor.PushToQueue(CreateTestScore());
            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", MAX_COMBO, Cts.Token);

            try
            {
                using (var db = Processor.GetDatabaseConnection())
                    db.Execute($"UPDATE osu_beatmaps SET approved = 0 WHERE beatmap_id = {TEST_BEATMAP_ID}");

                var testScore = CreateTestScore();
                testScore.Score.ScoreInfo.MaxCombo++;

                Processor.PushToQueue(testScore);
                WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", MAX_COMBO, Cts.Token);
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
            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", (int?)null, Cts.Token);

            Processor.PushToQueue(CreateTestScore());
            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", MAX_COMBO, Cts.Token);

            var score = CreateTestScore();
            score.Score.ScoreInfo.MaxCombo--;

            Processor.PushToQueue(score);
            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", MAX_COMBO, Cts.Token);
        }

        [Fact]
        public void TestMaxComboDoesntIncreaseIfAutomationMod()
        {
            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", (int?)null, Cts.Token);

            var score = CreateTestScore();
            score.Score.ScoreInfo.MaxCombo++;
            score.Score.ScoreInfo.Mods = new[]
            {
                new APIMod(new OsuModRelax()),
            };

            // Due to how the waiting for database test works, we can't check, for null.
            // Therefore push a non-automated score *after* the automated score, and ensure the combo matches the second.
            Processor.PushToQueue(score);
            Processor.PushToQueue(CreateTestScore());

            WaitForDatabaseState("SELECT max_combo FROM osu_user_stats WHERE user_id = 2", MAX_COMBO, Cts.Token);
        }
    }
}