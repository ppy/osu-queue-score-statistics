// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Game.Online.API;
using osu.Game.Rulesets.Osu.Mods;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class PlayTimeProcessorTests : StatisticsProcessorTest
    {
        [Fact]
        public void TestPlayTimeIncrease()
        {
            WaitForDatabaseState("SELECT total_seconds_played FROM osu_user_stats WHERE user_id = 2", (int?)null, Cts.Token);

            // Beatmap used in test score is 158 seconds.

            var testScore = CreateTestScore();
            testScore.Score.ScoreInfo.EndedAt = testScore.Score.ScoreInfo.StartedAt!.Value + TimeSpan.FromSeconds(50);

            Processor.PushToQueue(testScore);
            WaitForDatabaseState("SELECT total_seconds_played FROM osu_user_stats WHERE user_id = 2", 50, Cts.Token);

            testScore = CreateTestScore();
            testScore.Score.ScoreInfo.EndedAt = testScore.Score.ScoreInfo.StartedAt!.Value + TimeSpan.FromSeconds(100);

            Processor.PushToQueue(testScore);
            WaitForDatabaseState("SELECT total_seconds_played FROM osu_user_stats WHERE user_id = 2", 150, Cts.Token);
        }

        [Fact]
        public void TestPlayTimeIncreaseHigherThanBeatmapLength()
        {
            WaitForDatabaseState("SELECT total_seconds_played FROM osu_user_stats WHERE user_id = 2", (int?)null, Cts.Token);

            // Beatmap used in test score is 158 seconds.

            var testScore = CreateTestScore();
            testScore.Score.ScoreInfo.EndedAt = testScore.Score.ScoreInfo.StartedAt!.Value + TimeSpan.FromSeconds(200);

            Processor.PushToQueue(testScore);
            WaitForDatabaseState("SELECT total_seconds_played FROM osu_user_stats WHERE user_id = 2", 158, Cts.Token);
        }

        [Fact]
        public void TestPlayTimeIncreaseHigherThanBeatmapLengthWithModApplication()
        {
            WaitForDatabaseState("SELECT total_seconds_played FROM osu_user_stats WHERE user_id = 2", (int?)null, Cts.Token);

            // Beatmap used in test score is 158 seconds.
            // Double time means this is reduced to 105 seconds.

            var testScore = CreateTestScore();
            testScore.Score.ScoreInfo.Mods = new[]
            {
                new APIMod(new OsuModDoubleTime()),
            };
            testScore.Score.ScoreInfo.EndedAt = testScore.Score.ScoreInfo.StartedAt!.Value + TimeSpan.FromSeconds(200);

            Processor.PushToQueue(testScore);
            WaitForDatabaseState("SELECT total_seconds_played FROM osu_user_stats WHERE user_id = 2", 105, Cts.Token);
        }

        [Fact]
        public void TestPlayTimeIncreaseHigherThanBeatmapLengthWithModApplicationCustomRate()
        {
            WaitForDatabaseState("SELECT total_seconds_played FROM osu_user_stats WHERE user_id = 2", (int?)null, Cts.Token);

            // Beatmap used in test score is 158 seconds.
            // Double time with custom rate means this is reduced to 112 seconds.

            var testScore = CreateTestScore();
            testScore.Score.ScoreInfo.Mods = new[]
            {
                new APIMod(new OsuModDoubleTime { SpeedChange = { Value = 1.4 } }),
            };
            testScore.Score.ScoreInfo.EndedAt = testScore.Score.ScoreInfo.StartedAt!.Value + TimeSpan.FromSeconds(200);

            Processor.PushToQueue(testScore);
            WaitForDatabaseState("SELECT total_seconds_played FROM osu_user_stats WHERE user_id = 2", 112, Cts.Token);
        }
    }
}
