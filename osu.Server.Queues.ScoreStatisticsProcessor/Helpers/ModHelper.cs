using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public static class ModHelper
    {
        public static bool IsDifficultyReductionMod(this Mod mod)
        {
            if (mod.Type == ModType.DifficultyReduction || mod.Type == ModType.Automation)
                return true;

            switch (mod)
            {
                // Difficulty adjust can be used to decrease the difficulty of the map,
                // so let's consider it difficulty reducing for now.
                case ModDifficultyAdjust difficultyAdjust:
                    return !difficultyAdjust.UsesDefaultConfiguration;

                case OsuModTargetPractice:
                    return true;

                // Allow the use of certain Fun mods that are not considered difficulty-reducing
                case ModMuted:
                case ModNoScope:
                    return false;

                // Disallow the use of the other Fun mods
                default:
                    return mod.Type == ModType.Fun;
            }
        }
    }
}
