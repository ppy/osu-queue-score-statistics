// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Game.Beatmaps.Legacy;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    // See: https://github.com/ppy/osu-performance/blob/83c02f50315a4ef7feea80acb84c66ee437d7210/include/pp/Common.h#L109-L129
    public class LegacyModsHelperTests
    {
        [Fact]
        public void OsuWithoutFlashlight()
        {
            Assert.Equal(
                LegacyMods.DoubleTime | LegacyMods.HalfTime | LegacyMods.HardRock | LegacyMods.Easy,
                LegacyModsHelper.MaskRelevantMods(LegacyModsHelper.ALL_MODS & ~LegacyMods.Flashlight, false, 0));
        }

        [Fact]
        public void OsuWithFlashlight()
        {
            Assert.Equal(
                LegacyMods.DoubleTime | LegacyMods.HalfTime | LegacyMods.HardRock | LegacyMods.Easy | LegacyMods.Flashlight | LegacyMods.Hidden,
                LegacyModsHelper.MaskRelevantMods(LegacyModsHelper.ALL_MODS, false, 0));
        }

        [Fact]
        public void PartialMods()
        {
            Assert.Equal(
                LegacyMods.DoubleTime,
                LegacyModsHelper.MaskRelevantMods(LegacyMods.DoubleTime, false, 0));
        }

        [Fact]
        public void Taiko()
        {
            Assert.Equal(
                LegacyMods.DoubleTime | LegacyMods.HalfTime | LegacyMods.HardRock | LegacyMods.Easy,
                LegacyModsHelper.MaskRelevantMods(LegacyModsHelper.ALL_MODS, false, 1));
        }

        [Fact]
        public void Catch()
        {
            Assert.Equal(
                LegacyMods.DoubleTime | LegacyMods.HalfTime | LegacyMods.HardRock | LegacyMods.Easy,
                LegacyModsHelper.MaskRelevantMods(LegacyModsHelper.ALL_MODS, false, 2));
        }

        [Fact]
        public void ManiaNonConvert()
        {
            Assert.Equal(
                LegacyMods.DoubleTime | LegacyMods.HalfTime | LegacyMods.HardRock | LegacyMods.Easy,
                LegacyModsHelper.MaskRelevantMods(LegacyModsHelper.ALL_MODS, false, 3));
        }

        [Fact]
        public void ManiaConvert()
        {
            Assert.Equal(
                LegacyMods.DoubleTime | LegacyMods.HalfTime | LegacyMods.HardRock | LegacyMods.Easy | LegacyModsHelper.KEY_MODS,
                LegacyModsHelper.MaskRelevantMods(LegacyModsHelper.ALL_MODS, true, 3));
        }
    }
}
