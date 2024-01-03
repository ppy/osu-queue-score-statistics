using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public static class ModHelper
    {
        public static bool IsDifficultyReductionMod(this Mod mod)
        {
            switch (mod)
            {
                case ModEasy:
                case ModNoFail:
                case ModHalfTime:
                case OsuModSpunOut:
                    return true;

                // Difficulty adjust can be used to decrease the difficulty of the map,
                // so let's consider it difficulty reducing for now.
                case ModDifficultyAdjust difficultyAdjust:
                    return !difficultyAdjust.UsesDefaultConfiguration;

                default:
                    return false;
            }
        }
    }
}
