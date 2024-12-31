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
        public void MedalNotAwardedIfNotEnoughDailyChallengesOnRecord(int bestStreak)
        {
            using (var db = Processor.GetDatabaseConnection())
                db.Execute($"INSERT INTO `daily_challenge_user_stats` (`user_id`, `daily_streak_best`) VALUES (2, {bestStreak})");

            ulong roomId = CreateMultiplayerRoom("daily challenge", "playlists", "daily_challenge");
            ulong playlistItemId = CreatePlaylistItem(beatmap, roomId);

            SetMultiplayerScoreForBeatmap(beatmap.beatmap_id, playlistItemId);
            SetScoreForBeatmap(beatmap.beatmap_id);
            AssertNoMedalsAwarded();
        }

        [Fact]
        public void MedalAwardedOnDailyChallengeIfLongestHistoricalStreakAtLeastThirtyDays()
        {
            using (var db = Processor.GetDatabaseConnection())
                db.Execute($"INSERT INTO `daily_challenge_user_stats` (`user_id`, `daily_streak_best`) VALUES (2, 30)");

            ulong roomId = CreateMultiplayerRoom("daily challenge", "playlists", "daily_challenge");
            ulong playlistItemId = CreatePlaylistItem(beatmap, roomId);

            SetMultiplayerScoreForBeatmap(beatmap.beatmap_id, playlistItemId);
            AssertSingleMedalAwarded(338);
        }

        [Fact]
        public void MedalNotAwardedOutsideOfDailyChallengeEvenWithLongEnoughHistoricalBestStreak()
        {
            using (var db = Processor.GetDatabaseConnection())
                db.Execute("INSERT INTO `daily_challenge_user_stats` (`user_id`, `daily_streak_best`) VALUES (2, 33)");
            SetScoreForBeatmap(beatmap.beatmap_id);
            AssertNoMedalsAwarded();
        }
    }
}
