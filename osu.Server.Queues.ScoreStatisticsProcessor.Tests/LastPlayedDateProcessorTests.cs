// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class LastPlayedDateProcessorTests : DatabaseTest
    {
        [Fact]
        public void LastPlayedDateUpdates()
        {
            var beatmap = AddBeatmap();

            var firstDate = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.ended_at = firstDate);
            WaitForDatabaseState("SELECT `last_played` FROM `osu_user_stats` WHERE user_id = 2", firstDate, CancellationToken);

            var secondDate = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.ended_at = secondDate;
                s.Score.passed = false;
            });
            WaitForDatabaseState("SELECT `last_played` FROM `osu_user_stats` WHERE user_id = 2", secondDate, CancellationToken);

            // thirdDate is earlier than secondDate => it is expected that `last_played` is not inadvertently rolled back
            var thirdDate = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);
            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.ended_at = thirdDate;
                s.Score.passed = false;
            });
            WaitForDatabaseState("SELECT `last_played` FROM `osu_user_stats` WHERE user_id = 2", secondDate, CancellationToken);
        }
    }
}
