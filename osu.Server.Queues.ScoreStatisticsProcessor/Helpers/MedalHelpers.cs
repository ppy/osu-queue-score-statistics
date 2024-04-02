using osu.Game.Rulesets.Mods;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public static class MedalHelpers
    {
        public static bool IsDifficultyReductionMod(Mod mod)
        {
            // This is a bit dodgy, but should be getting removed at a later date.
            // The rationale for treating unranked mods as "difficulty reduction" for medal purposes
            // is that unranked mods generally will not have been given consideration with regard to difficulty,
            // so we generally cannot rely on having the correct star rating present for those
            // or even conclusively say if they are difficulty reduction or not.
            if (!mod.Ranked)
                return true;

            switch (mod.Type)
            {
                case ModType.DifficultyReduction:
                case ModType.Automation:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Some medals on stable require no mods to be active,
        /// but lazer submission specifics mean that some mods should be allowed.
        /// <list type="bullet">
        /// <item>Classic mod marks stable scores, so it should be allowed.</item>
        /// <item>System mods like touch device should also be allowed, because they're typically not in the user's direct control.</item>
        /// </list>
        /// </summary>
        public static bool IsPermittedInNoModContext(Mod m)
        {
            switch (m)
            {
                // Allow classic mod
                case ModClassic:
                    return true;

                default:
                    return m.Type == ModType.System;
            }
        }
    }
}
