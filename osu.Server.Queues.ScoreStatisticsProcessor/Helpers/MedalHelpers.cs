using Dapper;
using osu.Game.Rulesets.Mods;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

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

        /// <summary>
        /// Checks whether the user associated with the given <paramref name="context"/> has passed pack with ID <paramref name="packId"/>.
        /// </summary>
        /// <param name="context">The context in which the medal is being checked.</param>
        /// <param name="noReductionMods">Whether difficulty reduction mods are allowed (challenge packs don't allow them).</param>
        /// <param name="packId">The ID of the pack to check.</param>
        /// <returns></returns>
        public static bool UserPassedPack(MedalAwarderContext context, bool noReductionMods, int packId)
        {
            string modsCriteria = string.Empty;
            string rulesetCriteria;

            if (noReductionMods)
            {
                modsCriteria = @"AND json_search(data, 'one', 'EZ', null, '$.mods[*].acronym') IS NULL"
                               + " AND json_search(data, 'one', 'NF', null, '$.mods[*].acronym') IS NULL"
                               + " AND json_search(data, 'one', 'HT', null, '$.mods[*].acronym') IS NULL"
                               + " AND json_search(data, 'one', 'DC', null, '$.mods[*].acronym') IS NULL"
                               + " AND json_search(data, 'one', 'SO', null, '$.mods[*].acronym') IS NULL"
                               // this conditional's goal is to exclude plays with unranked mods from query.
                               // the reason why this is done in this roundabout way is that expressing the query in SQL is complicated otherwise,
                               // and materialising the collection of all scores to check this C#-side is likely to be prohibitively expensive.
                               + " AND s.pp IS NOT NULL";
            }

            // ensure the correct mode, if one is specified
            int? packRulesetId = context.Connection.QuerySingle<int?>($"SELECT playmode FROM `osu_beatmappacks` WHERE pack_id = {packId}", transaction: context.Transaction);

            if (packRulesetId != null)
            {
                if (context.Score.ruleset_id != packRulesetId)
                    return false;

                rulesetCriteria = $"AND ruleset_id = {packRulesetId}";
            }
            else
            {
                rulesetCriteria = "AND `s`.`ruleset_id` = `b`.`playmode`";
            }

            // TODO: no index on (beatmap_id, user_id) may mean this is too slow.
            // note that the `preserve = 1` condition relies on the flag being set before score processing (https://github.com/ppy/osu-web/pull/10946).
            int completed = context.Connection.QuerySingle<int>(
                "SELECT COUNT(distinct p.beatmapset_id)"
                + "FROM osu_beatmappacks_items p "
                + "JOIN osu_beatmaps b USING (beatmapset_id) "
                + "JOIN scores s USING (beatmap_id)"
                + $"WHERE s.user_id = {context.Score.user_id} AND s.passed = 1 AND s.preserve = 1 AND pack_id = {packId} {rulesetCriteria} {modsCriteria}", transaction: context.Transaction);

            int countForPack = context.Connection.QuerySingle<int>($"SELECT COUNT(*) FROM `osu_beatmappacks_items` WHERE pack_id = {packId}", transaction: context.Transaction);

            return completed >= countForPack;
        }
    }
}
