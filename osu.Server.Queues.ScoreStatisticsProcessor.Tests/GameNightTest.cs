// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Game.Online.API;
using osu.Game.Rulesets.Osu.Mods;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class GameNightTest : MedalAwarderTest
    {
        private readonly Beatmap beatmap;

        public GameNightTest()
        {
            AddMedal(340);
            beatmap = AddBeatmap();
        }

        [Fact]
        public void TestMedalAwarded()
        {
            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.ScoreData.Mods = [new APIMod(new OsuModBubbles())]);
            AssertSingleMedalAwarded(340);
        }

        [Fact]
        public void TestMedalAwardedWithExtraMods()
        {
            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.ScoreData.Mods = [new APIMod(new OsuModBubbles()), new APIMod(new OsuModClassic())]);
            AssertSingleMedalAwarded(340);
        }

        [Fact]
        public void TestMedalNotAwardedIfFunModsMissing()
        {
            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.ScoreData.Mods = [new APIMod(new OsuModClassic())]);
            AssertNoMedalsAwarded();
        }
    }
}
