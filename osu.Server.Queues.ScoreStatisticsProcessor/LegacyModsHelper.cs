// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Game.Beatmaps.Legacy;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    public class LegacyModsHelper
    {
        public static LegacyMods MaskRelevantMods(LegacyMods mods, bool isConvertedBeatmap)
        {
            LegacyMods relevantMods = LegacyMods.DoubleTime | LegacyMods.HalfTime | LegacyMods.HardRock | LegacyMods.Easy;

            if (isConvertedBeatmap)
                relevantMods |= keyMods;

            return mods & relevantMods;
        }

        private static LegacyMods keyMods => LegacyMods.Key1 | LegacyMods.Key2 | LegacyMods.Key3 | LegacyMods.Key4 | LegacyMods.Key5 | LegacyMods.Key6 | LegacyMods.Key7 | LegacyMods.Key8
                                             | LegacyMods.Key9 | LegacyMods.Key1;
    }
}
