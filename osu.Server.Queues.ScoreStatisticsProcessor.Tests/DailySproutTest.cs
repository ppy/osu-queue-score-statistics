// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Dapper;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class DailySproutTest : MedalAwarderTest
    {
        private readonly Beatmap beatmap;

        public DailySproutTest()
        {
            beatmap = AddBeatmap();
            AddMedal(336);
        }

        [Fact]
        public void MedalNotAwardedIfNoDailyChallengesOnRecord()
        {
            SetScoreForBeatmap(beatmap.beatmap_id);
            AssertNoMedalsAwarded();
        }

        [Fact]
        public void MedalAwardedOnDailyChallengeCompletion()
        {
            using (var db = Processor.GetDatabaseConnection())
                db.Execute("INSERT INTO `daily_challenge_user_stats` (`user_id`, `daily_streak_best`) VALUES (2, 1)");

            ulong roomId = CreateMultiplayerRoom("daily challenge", "playlists", "daily_challenge");
            ulong playlistItemId = CreatePlaylistItem(beatmap, roomId);

            SetMultiplayerScoreForBeatmap(beatmap.beatmap_id, playlistItemId);
            AssertSingleMedalAwarded(336);
        }

        [Fact]
        public void MedalNotAwardedOnRandomBeatmapCompletionWithPastDailyStreakOnRecord()
        {
            using (var db = Processor.GetDatabaseConnection())
                db.Execute("INSERT INTO `daily_challenge_user_stats` (`user_id`, `daily_streak_best`) VALUES (2, 1)");
            SetScoreForBeatmap(beatmap.beatmap_id);
            AssertNoMedalsAwarded();
        }
    }
}
