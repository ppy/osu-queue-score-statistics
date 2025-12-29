// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Game.Online.API;
using osu.Game.Rulesets.Osu.Difficulty;
using osu.Game.Rulesets.Osu.Mods;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class GearShiftTest : MedalAwarderTest
    {
        private readonly Beatmap beatmap;

        public GearShiftTest()
        {
            AddMedal(339);
            beatmap = AddBeatmap();

            AddBeatmapAttributes<OsuDifficultyAttributes>(beatmap.beatmap_id, mods: [new OsuModDoubleTime()]);
        }

        [Fact]
        public void TestMedalAwarded()
        {
            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.ScoreData.Mods = [new APIMod(new OsuModAlternate())]);
            AssertMedalAwarded(339);
        }

        [Fact]
        public void TestMedalAwardedWithExtraMods()
        {
            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.ScoreData.Mods = [new APIMod(new OsuModAlternate()), new APIMod(new OsuModDoubleTime())]);
            AssertMedalAwarded(339);
        }

        [Fact]
        public void TestMedalNotAwardedIfConversionModsMissing()
        {
            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.ScoreData.Mods = [new APIMod(new OsuModFreezeFrame())]);
            AssertNoMedalsAwarded();
        }
    }
}
