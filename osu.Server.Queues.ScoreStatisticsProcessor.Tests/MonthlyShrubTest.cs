// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Dapper;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class MonthlyShrubTest : MedalAwarderTest
    {
        private readonly Beatmap beatmap;

        public MonthlyShrubTest()
        {
            beatmap = AddBeatmap();
            AddMedal(338);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(9)]
        [InlineData(26)]
        public void MedalNotAwardedIfNotEnoughDailyChallengesOnRecord(int dailyChallengeCount)
        {
            using (var db = Processor.GetDatabaseConnection())
                db.Execute($"INSERT INTO `daily_challenge_user_stats` (`user_id`, `daily_streak_best`) VALUES (2, {dailyChallengeCount})");
            SetScoreForBeatmap(beatmap.beatmap_id);
            AssertNoMedalsAwarded();
        }

        [Fact]
        public void MedalAwardedIfAtLeastThirtyDailyChallengesOnRecord()
        {
            using (var db = Processor.GetDatabaseConnection())
                db.Execute("INSERT INTO `daily_challenge_user_stats` (`user_id`, `daily_streak_best`) VALUES (2, 30)");
            SetScoreForBeatmap(beatmap.beatmap_id);
            AssertSingleMedalAwarded(338);
        }
    }
}
