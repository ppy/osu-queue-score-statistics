using osu.Game.Rulesets.Mods;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public static class ModHelper
    {
        public static bool IsDifficultyReductionMod(this Mod mod)
        {
            switch (mod.Type)
            {
                case ModType.DifficultyReduction:
                case ModType.Automation:
                    return true;

                default:
                    return false;
            }
        }
    }
}
