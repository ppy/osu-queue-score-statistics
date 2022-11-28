// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
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
            //     //do a global check to see if this beatmapset is contained in *any* pack.
            //     //this is a huge optimisation since we run this function many times over.
            //     if ($checkedPacks === null)
            //         $checkedPacks = $conn->queryMany("SELECT pack_id FROM osu_beatmappacks_items WHERE beatmapset_id = {$beatmapSetId}");

            foreach (var medal in medals)
            {
                if (checkMedal(score, conn, transaction, medal))
                    yield return medal;
            }
        }

        private bool checkMedal(SoloScoreInfo score, MySqlConnection conn, MySqlTransaction transaction, Medal medal)
        {
            switch (medal.achievement_id)
            {
                case 7: //Pass all songs in Video Game Pack vol.1 (pack_id 40)
                    return checkPack(40, score, conn, transaction);

                case 8: //Pass all songs in Rhythm Game Pack vol.1 (pack_id 41)
                    return checkPack(41, score, conn, transaction);

                case 9: //Pass all songs in Internet! Pack vol.1 (pack_id 42)
                    return checkPack(42, score, conn, transaction);

                case 10: //Pass all songs in Anime Pack vol.1 (pack_id 43)
                    return checkPack(43, score, conn, transaction);

                case 11: //Pass all songs in Game Music Pack vol.2 (pack_id 48)
                    return checkPack(48, score, conn, transaction);

                case 12: //Pass all songs in Anime Pack vol.2 (pack_id 49)
                    return checkPack(49, score, conn, transaction);

                case 18: //Pass all songs in Internet! Pack vol.2 (pack_id 93)
                    return checkPack(93, score, conn, transaction);

                case 19: //Pass all songs in Rhythm Game Pack vol.2 (pack_id 94)
                    return checkPack(94, score, conn, transaction);

                case 14: //Pass all songs in Game Music Pack vol.3 (pack_id 70)
                    return checkPack(70, score, conn, transaction);

                case 25: //Pass all songs in Anime Pack vol.3 (pack_id 207)
                    return checkPack(207, score, conn, transaction);

                case 27: //Pass all songs in Internet! Pack vol.3 (pack_id 209)
                    return checkPack(209, score, conn, transaction);

                case 26: //Pass all songs in Rhythm Game Pack vol.3 (pack_id 208)
                    return checkPack(208, score, conn, transaction);

                case 37: //Pass all songs in Video Game Pack vol.4
                    return checkPack(364, score, conn, transaction);

                case 34: //Pass all songs in Anime vol.4
                    return checkPack(363, score, conn, transaction);

                case 35: //Pass all songs in rhythm vol.4
                    return checkPack(365, score, conn, transaction);

                case 36: //Pass all songs in internet vol.4
                    return checkPack(366, score, conn, transaction);
            }

            return false;
        }

        private bool checkPack(int packId, SoloScoreInfo score, MySqlConnection conn, MySqlTransaction transaction, bool noReductionMods = false)
        {
            //     global $conn, $highScoreTable, $checkedPacks;
            //
            //     $beatmapSetId = score['beatmapset_id'];
            //     $mode = score['mode'];
            //
            //     //do a global check to see if this beatmapset is contained in *any* pack.
            //     //this is a huge optimisation since we run this function many times over.
            //     if ($checkedPacks === null)
            //         $checkedPacks = $conn->queryMany("SELECT pack_id FROM osu_beatmappacks_items WHERE beatmapset_id = {$beatmapSetId}");
            //
            //     if (!in_array($packId, $checkedPacks))
            //         return false;
            //
            //     $userId = score['user_id'];
            //
            //     $modsCriteria = "";
            //
            //     if ($noReductionMods)
            //     {
            //         $diffReduction = NoFail | Easy | HalfTime | SpunOut;
            //         $modsCriteria = "AND (enabled_mods & $diffReduction) = 0";
            //     }
            //
            //     // ensure the correct mode, if one is specified
            //     $packMode = $conn->queryOne("SELECT IFNULL(playmode, -1) FROM `osu_beatmappacks` WHERE pack_id = $packId");
            //     $countForPack = $conn->queryOne("SELECT COUNT(*) FROM `osu_beatmappacks_items` WHERE pack_id = $packId");
            //
            //     if ($packMode >= 0)
            //     {
            //         if ($mode != $packMode)
            //             return false;
            //
            //         // can assume it's for the current mode only
            //         $completed = $conn->queryOne("SELECT COUNT(distinct p.beatmapset_id) FROM osu_beatmappacks_items p JOIN osu_beatmaps b USING (beatmapset_id) JOIN $highScoreTable s USING (beatmap_id) WHERE s.user_id = {$userId} AND pack_id = {$packId} $modsCriteria");
            //     }
            //     else
            //     {
            //         $completed = $conn->queryOne("SELECT COUNT(*) FROM (
            //         SELECT p.beatmapset_id FROM osu_beatmappacks_items p JOIN osu_beatmaps b USING (beatmapset_id) JOIN osu_scores_high s USING (beatmap_id) WHERE s.user_id = {$userId} AND b.playmode = 0 AND pack_id = {$packId} $modsCriteria UNION DISTINCT
            //         SELECT p.beatmapset_id FROM osu_beatmappacks_items p JOIN osu_beatmaps b USING (beatmapset_id) JOIN osu_scores_taiko_high s USING (beatmap_id) WHERE s.user_id = {$userId} AND b.playmode = 1 AND pack_id = {$packId} $modsCriteria UNION DISTINCT
            //         SELECT p.beatmapset_id FROM osu_beatmappacks_items p JOIN osu_beatmaps b USING (beatmapset_id) JOIN osu_scores_fruits_high s USING (beatmap_id) WHERE s.user_id = {$userId} AND b.playmode = 2 AND pack_id = {$packId} $modsCriteria UNION DISTINCT
            //         SELECT p.beatmapset_id FROM osu_beatmappacks_items p JOIN osu_beatmaps b USING (beatmapset_id) JOIN osu_scores_mania_high s USING (beatmap_id) WHERE s.user_id = {$userId} AND b.playmode = 3 AND pack_id = {$packId} $modsCriteria) a");
            //     }
            //
            //     return $completed >= $countForPack;
            return false;
        }
    }
}
