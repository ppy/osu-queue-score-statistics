// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Game.Beatmaps.Legacy;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    public class LegacyModsHelper
    {
        public const LegacyMods ALL_MODS = (LegacyMods)int.MaxValue;

        public const LegacyMods KEY_MODS = LegacyMods.Key1 | LegacyMods.Key2 | LegacyMods.Key3 | LegacyMods.Key4 | LegacyMods.Key5 | LegacyMods.Key6 | LegacyMods.Key7 | LegacyMods.Key8
                                           | LegacyMods.Key9 | LegacyMods.KeyCoop;

        // See: https://github.com/ppy/osu-performance/blob/83c02f50315a4ef7feea80acb84c66ee437d7210/include/pp/Common.h#L109-L129
        public static LegacyMods MaskRelevantMods(LegacyMods mods, bool isConvertedBeatmap, int rulesetId)
        {
            LegacyMods relevantMods = LegacyMods.DoubleTime | LegacyMods.HalfTime | LegacyMods.HardRock | LegacyMods.Easy;

            switch (rulesetId)
            {
                case 0:
                    if ((mods & LegacyMods.Flashlight) > 0)
                        relevantMods |= LegacyMods.Flashlight | LegacyMods.Hidden;
                    else
                        relevantMods |= LegacyMods.Flashlight;
                    break;

                case 3:
                    if (isConvertedBeatmap)
                        relevantMods |= KEY_MODS;
                    break;
            }

            return mods & relevantMods;
        }
    }
}
