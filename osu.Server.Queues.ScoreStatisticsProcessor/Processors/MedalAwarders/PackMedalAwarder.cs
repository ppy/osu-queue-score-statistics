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
            // Whether to disallow difficulty reduction mods to still achieve the medal.
            bool noReductionMods = false;
            int packId;

            switch (medal.achievement_id)
            {
                default:
                    return false;

                case 7:
                    //Pass all songs in Video Game Pack vol.1 (pack_id 40)
                    packId = 40;
                    break;

                case 8:
                    //Pass all songs in Rhythm Game Pack vol.1 (pack_id 41)
                    packId = 41;
                    break;

                case 9:
                    //Pass all songs in Internet! Pack vol.1 (pack_id 42)
                    packId = 42;
                    break;

                case 10:
                    //Pass all songs in Anime Pack vol.1 (pack_id 43)
                    packId = 43;
                    break;

                case 11:
                    //Pass all songs in Game Music Pack vol.2 (pack_id 48)
                    packId = 48;
                    break;

                case 12:
                    //Pass all songs in Anime Pack vol.2 (pack_id 49)
                    packId = 49;
                    break;

                case 18:
                    //Pass all songs in Internet! Pack vol.2 (pack_id 93)
                    packId = 93;
                    break;

                case 19:
                    //Pass all songs in Rhythm Game Pack vol.2 (pack_id 94)
                    packId = 94;
                    break;

                case 14:
                    //Pass all songs in Game Music Pack vol.3 (pack_id 70)
                    packId = 70;
                    break;

                case 25:
                    //Pass all songs in Anime Pack vol.3 (pack_id 207)
                    packId = 207;
                    break;

                case 27:
                    //Pass all songs in Internet! Pack vol.3 (pack_id 209)
                    packId = 209;
                    break;

                case 26:
                    //Pass all songs in Rhythm Game Pack vol.3 (pack_id 208)
                    packId = 208;
                    break;

                case 37:
                    //Pass all songs in Video Game Pack vol.4
                    packId = 364;
                    break;

                case 34:
                    //Pass all songs in Anime vol.4
                    packId = 363;
                    break;

                case 35:
                    //Pass all songs in rhythm vol.4
                    packId = 365;
                    break;

                case 36:
                    //Pass all songs in internet vol.4
                    packId = 366;
                    break;
            }

            if (!validPacksForBeatmapSet.Contains(packId))
                return false;

            return checkPack(packId, score, conn, transaction, noReductionMods);
        }

        private bool checkPack(int packId, SoloScoreInfo score, MySqlConnection conn, MySqlTransaction transaction, bool noReductionMods = false)
        {
            string modsCriteria = "";

            if (noReductionMods)
            {
                // TODO: correct this to be valid for `solo_scores`.
                modsCriteria = "AND (enabled_mods & $diffReduction) = 0";
            }

            // ensure the correct mode, if one is specified
            int packRulesetId = conn.QuerySingle<int>("SELECT IFNULL(playmode, -1) FROM `osu_beatmappacks` WHERE pack_id = $packId", transaction);
            int countForPack = conn.QuerySingle<int>("SELECT COUNT(*) FROM `osu_beatmappacks_items` WHERE pack_id = $packId", transaction);
            int completed;

            if (packRulesetId >= 0)
            {
                if (score.RulesetID != packRulesetId)
                    return false;

                // can assume it's for the current ruleset only

                // TODO: this query may require an index on (user_id, beatmap_id) to be efficient. right now it is going to be relying on
                // (user_id, ruleset) and potentially (beatmap_id) if mysql is smart enough.
                completed = conn.QuerySingle<int>(
                    "SELECT COUNT(distinct p.beatmapset_id)"
                    + "FROM osu_beatmappacks_items p "
                    + "JOIN osu_beatmaps b USING (beatmapset_id) "
                    + "JOIN solo_scores s USING (beatmap_id)"
                    + $"WHERE s.user_id = {score.UserID} AND pack_id = {packId} AND ruleset_id = {score.RulesetID} {modsCriteria}");
            }
            else
            {
                // check across all rulesets

                // TODO: confirm this is what we want. currently medals are NOT awarded for converts, but this will allow them to be.
                completed = conn.QuerySingle<int>(
                    "SELECT COUNT(distinct p.beatmapset_id)"
                    + "FROM osu_beatmappacks_items p "
                    + "JOIN osu_beatmaps b USING (beatmapset_id) "
                    + "JOIN solo_scores s USING (beatmap_id)"
                    + $"WHERE s.user_id = {score.UserID} AND pack_id = {packId} {modsCriteria}");
            }

            return completed >= countForPack;
        }
    }
}
