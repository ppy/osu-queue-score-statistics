using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Mods;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public static class ModHelper
    {
        public static bool IsDifficultyReductionMod(this Mod mod)
        {
            switch (mod)
            {
                // Allow the use of the mods that are also available on stable
                case ModSuddenDeath:
                case ManiaModFadeIn:
                case ModDoubleTime:
                case ModFlashlight:
                case ModNightcore:
                case ModHardRock:
                case ModPerfect:
                case ModHidden:
                    return false;

                // Disallow the use of the other mods
                default:
                    return true;
            }
        }
    }
}
