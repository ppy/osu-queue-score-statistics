// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Game.Beatmaps.Legacy;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class LegacyModsHelperTests
    {
        [Fact]
        public void OnlyBasicRelevantModsAreReturned()
        {
            Assert.Equal(
                LegacyMods.DoubleTime | LegacyMods.HalfTime | LegacyMods.HardRock | LegacyMods.Easy,
                LegacyModsHelper.MaskRelevantMods(LegacyMods.NoFail | LegacyMods.Perfect | LegacyMods.DoubleTime | LegacyMods.HalfTime | LegacyMods.HardRock | LegacyMods.Easy, false));
        }

        [Fact]
        public void KeyModsAreRelevantForConvertedBeatmaps()
        {
            Assert.Equal(
                LegacyMods.DoubleTime | LegacyMods.Key1,
                LegacyModsHelper.MaskRelevantMods(LegacyMods.DoubleTime | LegacyMods.Key1, true));
        }

        [Fact]
        public void KeyModsAreNotRelevantForManiaSpecificBeatmaps()
        {
            Assert.Equal(
                LegacyMods.DoubleTime,
                LegacyModsHelper.MaskRelevantMods(LegacyMods.DoubleTime | LegacyMods.Key1, false));
        }
    }
}
