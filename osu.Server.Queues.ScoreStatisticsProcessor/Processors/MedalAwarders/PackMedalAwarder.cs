// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using Dapper;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors.MedalAwarders
{
    [UsedImplicitly]
    public class PackMedalAwarder : IMedalAwarder
    {
        public IEnumerable<Medal> Check(SoloScoreInfo score, IEnumerable<Medal> medals, MySqlConnection conn, MySqlTransaction transaction)
        {
            int beatmapSetId = conn.QuerySingle<int>("SELECT beatmapset_id FROM osu_beatmaps WHERE beatmap_id = @beatmapId", new
            {
                beatmapId = score.BeatmapID,
            }, transaction);

            // Do a global check to see if this beatmapset is contained in *any* pack.
            var validPacksForBeatmapSet = conn.Query<int>("SELECT pack_id FROM osu_beatmappacks_items WHERE beatmapset_id = @beatmapSetId LIMIT 1", new
            {
                beatmapSetId = beatmapSetId,
            }, transaction: transaction);

            foreach (var medal in medals)
            {
                if (checkMedal(score, medal, validPacksForBeatmapSet, conn, transaction))
                    yield return medal;
            }
        }

        private bool checkMedal(SoloScoreInfo score, Medal medal, IEnumerable<int> validPacksForBeatmapSet, MySqlConnection conn, MySqlTransaction transaction)
        {
            return medal.achievement_id switch
            {
                // Pass all songs in Video Game Pack vol.1
                7 => checkPack(40),
                // Pass all songs in Rhythm Game Pack vol.1
                8 => checkPack(41),
                // Pass all songs in Internet! Pack vol.1
                9 => checkPack(42),
                // Pass all songs in Anime Pack vol.1
                10 => checkPack(43),
                // Pass all songs in Game Music Pack vol.2
                11 => checkPack(48),
                // Pass all songs in Anime Pack vol.2
                12 => checkPack(49),
                // Pass all songs in Internet! Pack vol.2
                18 => checkPack(93),
                // Pass all songs in Rhythm Game Pack vol.2
                19 => checkPack(94),
                // Pass all songs in Game Music Pack vol.3
                14 => checkPack(70),
                // Pass all songs in Anime Pack vol.3
                25 => checkPack(207),
                // Pass all songs in Internet! Pack vol.3
                27 => checkPack(209),
                // Pass all songs in Rhythm Game Pack vol.3
                26 => checkPack(208),
                // Pass all songs in Video Game Pack vol.4
                37 => checkPack(364),
                // Pass all songs in Anime vol.4
                34 => checkPack(363),
                // Pass all songs in rhythm vol.4
                35 => checkPack(365),
                // Pass all songs in internet vol.4
                36 => checkPack(366),

                _ => false,
            };

            bool checkPack(int packId, bool noReductionMods = false)
            {
                if (!validPacksForBeatmapSet.Contains(packId))
                    return false;

                string modsCriteria = string.Empty;
                string rulesetCriteria = string.Empty;

                if (noReductionMods)
                {
                    // TODO: correct this to be valid for `solo_scores`.
                    modsCriteria = "AND (enabled_mods & diff_reduction_mods) = 0";
                }

                // ensure the correct mode, if one is specified
                int? packRulesetId = conn.QuerySingle<int?>($"SELECT playmode FROM `osu_beatmappacks` WHERE pack_id = {packId}", transaction: transaction);

                if (packRulesetId != null)
                {
                    if (score.RulesetID != packRulesetId)
                        return false;

                    rulesetCriteria = $"AND ruleset_id = {packRulesetId}";
                }

                int completed = conn.QuerySingle<int>(
                    "SELECT COUNT(distinct p.beatmapset_id)"
                    + "FROM osu_beatmappacks_items p "
                    + "JOIN osu_beatmaps b USING (beatmapset_id) "
                    + "JOIN solo_scores s USING (beatmap_id)"
                    + $"WHERE s.user_id = {score.UserID} AND pack_id = {packId} {rulesetCriteria} {modsCriteria}", transaction: transaction);

                int countForPack = conn.QuerySingle<int>($"SELECT COUNT(*) FROM `osu_beatmappacks_items` WHERE pack_id = {packId}", transaction: transaction);

                return completed >= countForPack;
            }
        }
    }
}
