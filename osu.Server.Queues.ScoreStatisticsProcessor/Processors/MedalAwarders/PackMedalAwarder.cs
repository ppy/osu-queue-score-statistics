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
        public bool RunOnFailedScores => false;

        public IEnumerable<Medal> Check(SoloScoreInfo score, IEnumerable<Medal> medals, MySqlConnection conn, MySqlTransaction transaction)
        {
            // Do a global check to see if this beatmapset is contained in *any* pack.
            var validPacksForBeatmapSet = conn.Query<int>("SELECT pack_id FROM osu_beatmappacks_items WHERE beatmapset_id = @beatmapSetId LIMIT 1", new
            {
                beatmapSetId = score.Beatmap!.OnlineBeatmapSetID,
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
                // Spotlights January/February 2017 (1186-1189 packid)
                162 => checkPack(1186) || checkPack(1187) || checkPack(1188) || checkPack(1189),
                // Spotlights March 2017 (1201-1204 packid)
                163 => checkPack(1201) || checkPack(1202) || checkPack(1203) || checkPack(1204),
                // Spotlights April 2017 (1219-1222 packid)
                164 => checkPack(1219) || checkPack(1220) || checkPack(1221) || checkPack(1222),
                // Spotlights May 2017 (1228-1232)
                165 => checkPack(1228) || checkPack(1229) || checkPack(1230) || checkPack(1232),
                // Spotlights June 2017 (1244-1247)
                166 => checkPack(1244) || checkPack(1245) || checkPack(1246) || checkPack(1247),
                // Spotlights July 2017 (1253-1256)
                167 => checkPack(1253) || checkPack(1254) || checkPack(1255) || checkPack(1256),
                // Spotlights August 2017 (1264-1268)
                169 => checkPack(1264) || checkPack(1265) || checkPack(1267) || checkPack(1268),
                // MOtOLOiD pack (pack_id 1284)
                179 => checkPack(1284),
                // Spotlights September 2017 (1280-1283)
                180 => checkPack(1280) || checkPack(1281) || checkPack(1282) || checkPack(1283),
                // Spotlights October 2017 (1292-1295)
                181 => checkPack(1292) || checkPack(1293) || checkPack(1294) || checkPack(1295),
                // Spotlights November 2017 (1302-1305)
                182 => checkPack(1302) || checkPack(1303) || checkPack(1304) || checkPack(1305),
                // Spotlights December 2017 (1331-1334)
                183 => checkPack(1331) || checkPack(1332) || checkPack(1333) || checkPack(1334),
                // Spotlights January 2018 (1354-1357)
                184 => checkPack(1354) || checkPack(1355) || checkPack(1356) || checkPack(1357),
                // Mappers Guild I, January 22~ 2018 (1365)
                185 => checkPack(1365),
                // Spotlights February 2018 (1379-1382)
                186 => checkPack(1379) || checkPack(1380) || checkPack(1381) || checkPack(1382),
                // Spotlights March 2018 (1405, 1407, 1408) - this month has no mania cuz ???
                187 => checkPack(1405) || checkPack(1407) || checkPack(1408),
                // Spotlights April 2018 (1430, 1431, 1432, 1433)
                188 => checkPack(1430) || checkPack(1431) || checkPack(1432) || checkPack(1433),
                // Cranky Pack (1437)
                189 => checkPack(1437),
                // Mappers' Guild Pack 2 (1450)
                190 => checkPack(1450),
                // Mappers' Guild Pack 3 (1480)
                191 => checkPack(1480),
                // Summer Spotlights 2018 (1508-1511)
                205 => checkPack(1508) || checkPack(1509) || checkPack(1510) || checkPack(1511),
                // Mappers' Guild Pack 4: Culprate (1535)
                206 => checkPack(1535),
                // Seasonal Spotlights: Fall 2018 (1548-1551)
                207 => checkPack(1548) || checkPack(1549) || checkPack(1550) || checkPack(1551),
                // Mappers' Guild Pack 5: HyuN (1581)
                208 => checkPack(1581),
                // Seasonal Spotlights: Winter 2018 (1623-1626)
                209 => checkPack(1623) || checkPack(1624) || checkPack(1625) || checkPack(1626),
                // Seasonal Spotlights: Spring 2019 (1670-1673)
                210 => checkPack(1670) || checkPack(1671) || checkPack(1672) || checkPack(1673),
                // ICDD artist pack (1688)
                213 => checkPack(1688),
                // Tieff MG artist pack (1649)
                214 => checkPack(1649),
                // Seasonal Spotlights: Summer 2019 (1722-1725)
                215 => checkPack(1722) || checkPack(1723) || checkPack(1724) || checkPack(1725),
                // Mappers Guild Pack III redux (1689)
                226 => checkPack(1689),
                // Mappers' Guild Pack IV redux (1757)
                227 => checkPack(1757),
                // Seasonal Spotlights: Autumn 2019 (1798-1801)
                228 => checkPack(1798) || checkPack(1799) || checkPack(1800) || checkPack(1801),
                // Afterparty Featured Artist pack (1542)
                229 => checkPack(1542),
                // Ben Briggs FA pack (1687)
                230 => checkPack(1687),
                // Carpool Tunnel FA pack (1805)
                231 => checkPack(1805),
                // Creo FA pack (1807)
                232 => checkPack(1807),
                // cYsmix FA pack (1808)
                233 => checkPack(1808),
                // Fractal Dreamers FA pack (1809)
                234 => checkPack(1809),
                // LukHash FA pack (1758)
                235 => checkPack(1758),
                // *namirin FA pack (1704)
                236 => checkPack(1704),
                // onumi FA pack (1804)
                237 => checkPack(1804),
                // The Flashbulb FA pack (1762)
                238 => checkPack(1762),
                // Undead Corporation FA pack (1810)
                239 => checkPack(1810),
                // Wisp X FA pack (1806)
                240 => checkPack(1806),
                // Seasonal Spotlights: Winter 2020 (1896-1899)
                241 => checkPack(1896) || checkPack(1897) || checkPack(1898) || checkPack(1899),
                // Camellia I Beatmap Pack (2051)
                246 => checkPack(2051),
                // Camellia II Beatmap Pack (2053, CHALLENGE SET)
                247 => checkPack(2053, true),
                // Celldweller Beatmap Pack (2040)
                248 => checkPack(2040),
                // Cranky II Beatmap Pack (2049)
                249 => checkPack(2049),
                // Cute Anime Girls FA Pack (2031)
                250 => checkPack(2031),
                // ELFENSJoN beatmap pack (2047)
                251 => checkPack(2047),
                // Hyper Potions beatmap pack (2037)
                252 => checkPack(2037),
                // Kola Kid beatmap pack (2044)
                253 => checkPack(2044),
                // LeaF beatmap pack (2039)
                254 => checkPack(2039),
                // Panda Eyes beatmap pack (2043)
                255 => checkPack(2043),
                // PUP beatmap pack (2048)
                256 => checkPack(2048),
                // Ricky Montgomery beatmap pack (2046)
                257 => checkPack(2046),
                // Rin beatmap pack (1759)
                258 => checkPack(1759),
                // S3RL beatmap pack (2045)
                259 => checkPack(2045),
                // Sound Souler beatmap pack (2038)
                260 => checkPack(2038),
                // Teminite beatmap pack (2042)
                261 => checkPack(2042),
                // VINXIS beatmap pack (2041)
                262 => checkPack(2041),
                // Mappers' Guild Pack 5 (2032, diff allowed)
                263 => checkPack(2032),
                // Mappers' Guild Pack 6 (2033, diff allowed)
                264 => checkPack(2033),
                // Mappers' Guild Pack 7 (2034, CHALLENGE SET)
                265 => checkPack(2034, true),
                // Mappers' Guild Pack 8 (2035, CHALLENGE SET)
                266 => checkPack(2035, true),
                // Mappers' Guild Pack 9 (2036, CHALLENGE SET)
                267 => checkPack(2036, true),
                // Touhou Pack (2457)
                282 => checkPack(2457),
                // ginkiha Pack (2458)
                283 => checkPack(2458),
                // MUZZ Pack (2459, challenge pack)
                284 => checkPack(2459, true),
                // Vocaloid Pack (2481)
                288 => checkPack(2481),
                // Maduk Pack (2482)
                289 => checkPack(2482),
                // Aitsuki Nakuru Pack (2483)
                290 => checkPack(2483),
                // Arabl'eyeS Pack (291-293 are taiko/fruits/mania hits progression), (2521, challenge pack)
                294 => checkPack(2521, true),
                // Omoi Pack (2522)
                295 => checkPack(2522),
                // Chill Pack (2523)
                296 => checkPack(2523),

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
                    modsCriteria = @"AND json_search(data, 'one', 'EZ', null, '$.mods[*].acronym') IS NULL"
                                   + " AND json_search(data, 'one', 'NF', null, '$.mods[*].acronym') IS NULL"
                                   + " AND json_search(data, 'one', 'HT', null, '$.mods[*].acronym') IS NULL"
                                   + " AND json_search(data, 'one', 'SO', null, '$.mods[*].acronym') IS NULL";
                }

                // ensure the correct mode, if one is specified
                int? packRulesetId = conn.QuerySingle<int?>($"SELECT playmode FROM `osu_beatmappacks` WHERE pack_id = {packId}", transaction: transaction);

                if (packRulesetId != null)
                {
                    if (score.RulesetID != packRulesetId)
                        return false;

                    rulesetCriteria = $"AND ruleset_id = {packRulesetId}";
                }

                // TODO: no index on (beatmap_id, user_id) may mean this is too slow.
                int completed = conn.QuerySingle<int>(
                    "SELECT COUNT(distinct p.beatmapset_id)"
                    + "FROM osu_beatmappacks_items p "
                    + "JOIN osu_beatmaps b USING (beatmapset_id) "
                    + "JOIN scores s USING (beatmap_id)"
                    + $"WHERE s.user_id = {score.UserID} AND pack_id = {packId} {rulesetCriteria} {modsCriteria}", transaction: transaction);

                int countForPack = conn.QuerySingle<int>($"SELECT COUNT(*) FROM `osu_beatmappacks_items` WHERE pack_id = {packId}", transaction: transaction);

                return completed >= countForPack;
            }
        }
    }
}
